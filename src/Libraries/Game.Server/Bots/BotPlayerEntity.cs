using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Items;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.API.Game.World;
using QuantumCore.API.Packets;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Skills;
using QuantumCore.Game.World;
using QuantumCore.Game.World.Entities;

namespace QuantumCore.Game.Bots;

/// <summary>
/// A <see cref="PlayerEntity"/> that, on its own tick, wanders around its spawn point and fights nearby
/// monsters - all without any client ever sending a packet. Reuses the exact same building blocks real
/// players and monsters use: <see cref="Entity.Goto"/> for movement (same call <c>CharacterMoveHandler</c>
/// makes for a real player and <c>SimpleBehaviour</c> makes for a monster) and <see cref="Entity.Attack"/>
/// for combat (same call <c>AttackHandler</c> makes for a real player). Nothing here is bot-specific
/// machinery - it is ordinary server-side code, which is exactly what this feasibility study set out to
/// demonstrate.
/// </summary>
public class BotPlayerEntity : PlayerEntity
{
    private const int WanderMinDelayMs = 5000;
    private const int WanderMaxDelayMs = 12000;
    private const int WanderMinDistance = 300; // ~3m, same convention as SimpleBehaviour's mob wander
    private const int WanderMaxDistance = 700; // ~7m
    private const int MaxPositionAttempts = 16;

    private const double AggroRange = 600; // ~6m: start engaging any non-dead monster within this range
    private const double GiveUpRange = 1200; // ~12m: stop chasing a target that got this far away
    private const double MeleeAttackRange = 200; // ~2m
    private const double AttackConeHalfAngle = 60; // 120deg total arc in front of the bot, like a sword swing
    private static readonly TimeSpan AttackCooldown = TimeSpan.FromSeconds(1.2); // was 2s, sped up on request

    // How often the bot ATTEMPTS a skill cast, not the skill's own cooldown (PlayerSkills.Use() already
    // tracks that per-skill internally and just no-ops harmlessly if the roll picks one still on
    // cooldown/unaffordable) - deliberately longer than AttackCooldown so casts read as an occasional
    // "special move" layered on top of ordinary melee swings, not a spam of animations-that-don't-play
    // (see BroadcastAttack's own doc comment on why bots can't show a cast animation to observers either
    // way - this is purely about pacing how often the real effect - damage/buff - fires).
    private static readonly TimeSpan SkillAttemptCooldown = TimeSpan.FromSeconds(7);

    // A real combo's length/animation is picked by each OBSERVING client from its own per-race/weapon
    // motion data (CRaceData::TComboData::ComboIndexVector, RaceMotionData.cpp) using the Argument byte we
    // send as the combo step index - we don't have that per-weapon table parsed out, so 3 is used as a
    // conservative, common basic-weapon combo length rather than a value read from real game data.
    private const byte ComboMaxIndex = 3;

    // Level-~80-90, +9 refined, PER-CLASS gear matching the bot's own level (BotPlayerDataFactory sets
    // Level=90) - verified against this repo's actual src/Executables/Single/data/item_proto, including
    // each item's AntiFlags (which classes are forbidden from wearing it) and its Limits[Type=1] (minimum
    // wearable level, confirmed <= 90 for every id below - a level-90 bot can equip lower-level-requirement
    // gear just fine, an item's level Limit is a floor, not an exact match). An earlier version used
    // level-0 starter gear (19/11209/12209 etc.) left over from before bots were bumped to level 90 for
    // their skill SP pools (see BotPlayerDataFactory.Create) - that looked wrong once bots started
    // wandering a real map at level 90. EquipWithoutPersisting never checks AntiFlags itself, so picking
    // per-class ids here (not one fixed set) still matters - see the original starter-gear comment history
    // for the NINJA-wearing-WARRIOR-armor bug that caused.
    // Weapon set note: Warrior's "Triton Sword+9"(279) is a universal blade (AntiFlags only forbids
    // Shaman) rather than a Warrior-exclusive id - no Warrior-only blade exists at this tier in this data
    // set, unlike the other three classes which do have an exclusive weapon line at 90 (Soulless
    // Knife/Edge Blade/Dragon Jaw Bell). Helmets top out at level-80 Limits for all four classes at +9 (no
    // level-90 helmet line exists in this data set) - still valid for a level-90 bot per the floor-not-
    // exact-match rule above.
    private const uint StarterShieldId = 13009; // 호신환패+9 - AntiFlags=0, level 0 floor, fine for every class

    /// <summary>Weapon/body/head proto ids (all +9) that <paramref name="playerClass"/> is actually
    /// allowed to wear at level 90, verified via each item's AntiFlags and level Limit. Falls back to the
    /// warrior set for a class this hasn't been filled in for.</summary>
    private static (uint Weapon, uint Body, uint Head) GetGearFor(EPlayerClass playerClass) =>
        playerClass switch
        {
            // Triton Sword+9 (lvl 90, universal blade) / Blue Steel Armour+9 (lvl 90, Warrior-only) /
            // War Master Helmet+9 (lvl 80, Warrior-only)
            EPlayerClass.WARRIOR => (279, 12019, 12289),
            // Soulless Knife+9 (lvl 90, Ninja-only dagger) / Blue Dragon Suit+9 (lvl 90, Ninja-only) /
            // Spider Hood+9 (lvl 80, Ninja-only)
            EPlayerClass.NINJA => (4049, 12029, 12409),
            // Edge Blade+9 (lvl 90, Sura-only blade) / Aura Plate Armour+9 (lvl 90, Sura-only) /
            // Magic Helmet+9 (lvl 80, Sura-only)
            EPlayerClass.SURA => (209, 12039, 12549),
            // Dragon Jaw Bell+9 (lvl 90, Shaman-only bell) / Dragon Clothing+9 (lvl 90, Shaman-only) /
            // Soul Shard Hat+9 (lvl 80, Shaman-only)
            EPlayerClass.SHAMAN => (5339, 12049, 12689),
            _ => (279, 12019, 12289)
        };

    private readonly IItemManager _itemManager;
    private readonly int _spawnX;
    private readonly int _spawnY;
    private TimeSpan _nextWanderIn;
    private MonsterEntity? _target;
    private TimeSpan _attackCooldownRemaining;
    private TimeSpan _skillAttemptCooldownRemaining;
    private byte _comboIndex; // 0 = next hit starts a fresh FUNC_ATTACK instead of continuing a FUNC_COMBO

    public BotPlayerEntity(PlayerData player, IGameConnection connection, IItemManager itemManager,
        IJobManager jobManager, IExperienceManager experienceManager, IAnimationManager animationManager,
        IQuestManager questManager, ICacheManager cacheManager, IWorld world, ILogger<PlayerEntity> logger,
        IServiceProvider serviceProvider)
        : base(player, connection, itemManager, jobManager, experienceManager, animationManager, questManager,
            cacheManager, world, logger, serviceProvider)
    {
        _itemManager = itemManager;
        _spawnX = player.PositionX;
        _spawnY = player.PositionY;
        CalculateNextWander();
    }

    /// <summary>
    /// Bots are never backed by a database row (see <see cref="BotPlayerDataFactory.BotAccountId"/> and
    /// <see cref="PlayerData.Id"/> staying 0), so <see cref="PlayerEntity.OnDespawnAsync"/>'s normal
    /// persistence (in particular <see cref="QuantumCore.Game.PlayerUtils.QuickSlotBar.PersistAsync"/>,
    /// which does a hard <c>FirstAsync</c> lookup by Id and throws for a nonexistent row) must be skipped
    /// entirely for a bot - but the quest event cleanup is NOT persistence and must still run: every bot
    /// spawn calls LoadAsync() -> QuestManager.InitializePlayer(), which registers this Vid into
    /// GameEventManager's static dictionaries. Without this, repeated /botspawn+/botdespawn cycles (as
    /// happened a lot this session) leak one stale registration per quest per spawn forever, eventually
    /// turning a single NPC click into a bogus multi-choice menu of accumulated duplicates - this was the
    /// actual root cause behind the Horse Keeper dialogue lockup, not the quest script/parsing itself.
    /// </summary>
    public override Task OnDespawnAsync()
    {
        GameEventManager.UnregisterPlayer(Vid);
        return Task.CompletedTask;
    }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        EquipGear();
        // Deliberately NOT auto-mounted (was Mount(HorseVnums.ForLevel(Player.HorseLevel)) here) - a
        // wandering bot on horseback looks wrong for what a "populated map" should feel like; horses stay
        // available via /mount for anyone who wants to test riding specifically, bots just don't default
        // to it anymore. Player.HorseLevel is still set (BotPlayerDataFactory), so nothing about the horse
        // *system* changed, only bots' own default state.
        MasterAllUsableSkills();
    }

    /// <summary>
    /// Maxes every active skill this bot's class+branch (BRANCH_A - see BotPlayerDataFactory.Create's own
    /// comment on why bots get a branch pre-assigned) actually has, same as GM command
    /// Commands/AllSkillsMasterCommand.cs does for a real player - not gated by skill points/character
    /// level like a real player's own progression would be, since a bot has no such history to simulate.
    /// Gives UpdateCombat's periodic skill cast (see BroadcastAttack's own doc comment on why bots have to
    /// fake everything a real client would normally drive) something real to use.
    /// </summary>
    private void MasterAllUsableSkills()
    {
        foreach (var skillId in Enum.GetValues<ESkill>())
        {
            if (Skills.CanUse(skillId))
            {
                Skills.SetLevel(skillId, PlayerSkills.SKILL_MAX_LEVEL);
            }
        }

        Skills.Send();
    }

    /// <summary>
    /// Equips a level-90-appropriate, +9 refined set matching the bot's actual class (see
    /// <see cref="GetGearFor"/>) via <see cref="PlayerEntity.EquipWithoutPersisting"/> - purely for
    /// visual/testing purposes, not tied to feasibility.
    /// </summary>
    private void EquipGear()
    {
        var (weaponId, bodyId, headId) = GetGearFor(Player.PlayerClass.GetClass());

        foreach (var itemId in (ReadOnlySpan<uint>)[weaponId, bodyId, headId, StarterShieldId])
        {
            var proto = _itemManager.GetItem(itemId);
            if (proto is null) continue;

            var item = _itemManager.CreateItem(proto);
            EquipWithoutPersisting(item);
        }
    }

    private void CalculateNextWander()
    {
        _nextWanderIn = TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(WanderMinDelayMs,
            WanderMaxDelayMs + 1));
    }

    public override void Update(TickContext ctx)
    {
        base.Update(ctx);

        if (Map is null) return; // not spawned yet
        if (Dead)
        {
            _target = null;
            return;
        }

        UpdateCombat(ctx);
        if (_target is not null) return; // stay put/engaged instead of wandering off mid-fight

        if (State != EEntityState.IDLE) return; // already moving

        _nextWanderIn -= ctx.Delta;
        if (_nextWanderIn > TimeSpan.Zero) return;

        CalculateNextWander();
        TryWander(ctx);
    }

    private void UpdateCombat(TickContext ctx)
    {
        if (_attackCooldownRemaining > TimeSpan.Zero)
        {
            _attackCooldownRemaining -= ctx.Delta;
        }

        if (_skillAttemptCooldownRemaining > TimeSpan.Zero)
        {
            _skillAttemptCooldownRemaining -= ctx.Delta;
        }

        if (_target is not null && (_target.Dead || _target.Map != Map || this.DistanceTo(_target) > GiveUpRange))
        {
            _target = null;
            _comboIndex = 0; // a fresh engagement later starts with a plain attack, not mid-combo
        }

        _target ??= FindTarget();
        if (_target is null) return;

        var distance = this.DistanceTo(_target);
        if (distance > MeleeAttackRange)
        {
            if (State != EEntityState.MOVING)
            {
                Rotation = (float)MathUtils.Rotation(_target.PositionX - PositionX, _target.PositionY - PositionY);
                Goto(_target.PositionX, _target.PositionY, ctx.Timestamp);
                BroadcastMove(ctx);
            }

            return;
        }

        if (_attackCooldownRemaining > TimeSpan.Zero) return;

        Rotation = (float)MathUtils.Rotation(_target.PositionX - PositionX, _target.PositionY - PositionY);

        // Root cause of the earlier "no visible swing, kills land 5-10m away" artifact, found by reading
        // the real client's own attack-packet handling (InstanceBase.cpp's ProcessAffect/FUNC_ATTACK
        // case): on receiving an ATTACK/COMBO CharacterMoveOut, the client compares the packet's position
        // against where IT currently believes the attacker is; only if that gap is under ~50 units does it
        // play the swing animation immediately (RunNormalAttack/RunComboAttack) - otherwise it walks the
        // attacker to that position first and plays the swing only once it arrives. A real player never
        // hits this path (their own attack is decided and rendered locally, no network round-trip). A bot
        // does: UpdateCombat can stop chasing and start attacking mid-stride, whenever the live-interpolated
        // distance to the target first drops to melee range - server-side that's fine (Entity.Update()
        // recomputes PositionX/Y every tick), but the OBSERVING CLIENT was never told the walk stopped
        // early - it was still interpolating toward the ORIGINAL Goto() destination for the rest of that
        // move's Duration, so its believed position could easily be >50 units from where the bot actually
        // stopped, which is exactly the gap the client uses to decide "walk first, animate later" (and by
        // the time it arrives, the fight has moved on - hence hits registering meters away with no visible
        // swing). Fix: explicitly stop the walk and correct the client's position belief before attacking.
        if (State == EEntityState.MOVING)
        {
            Stop();
            BroadcastStop(ctx);
        }

        // BroadcastAttack() DISABLED AGAIN - live-tested on n_flame_01 (the new volcano map) and the bot
        // hard-froze in place from its very first attack onward (no idle/walk animation either, not just a
        // missing swing) - the exact original symptom this project hit long before the Stop()/
        // BroadcastStop() position-correction fix above existed (see bot-feature-overview memory: "the
        // hard freeze is gone" was the conclusion of the SECOND round of testing, on the starter town maps
        // only). Never re-isolated why it's back now (new map's terrain/height data is the prime suspect -
        // untested on a1/b1/c1 with this exact bot setup - but the level-90 gear and no-longer-auto-mounted
        // default changed too, all at once, so nothing here is confirmed). Given a frozen statue is a worse
        // outcome for "wandering, lively bots" than simply not showing a swing (which was already the case
        // either way - no version of this has ever produced a visible swing), reverting to the safe,
        // previously-validated baseline: no ATTACK/COMBO packet at all. Damage still applies normally via
        // Entity.Damage()/DamageInfo below, only the animation packet is skipped.
        // BroadcastAttack(ctx);

        // A real player's sword swing hits everyone in a forward cone, not just the locked target - the
        // client computes that cone and sends one Attack packet per hit; since a bot has no client we
        // have to do the same cone check ourselves and call Entity.Attack once per monster caught in it.
        foreach (var monster in FindTargetsInAttackCone())
        {
            Attack(monster); // same Entity.Attack AttackHandler calls for a real client-driven attack
        }

        _attackCooldownRemaining = AttackCooldown;

        TryUseSkill();
    }

    /// <summary>
    /// Occasionally casts one of the bot's own mastered skills (see MasterAllUsableSkills) on its current
    /// target - the same real PlayerSkills.Use() a real player's own UseSkillHandler calls, so it's a real
    /// formula-driven effect (damage, buff, weapon aura), not a cosmetic extra. Picks a random usable skill
    /// each attempt rather than cycling through them in order, so a bot doesn't look like it's running a
    /// fixed rotation.
    /// </summary>
    private void TryUseSkill()
    {
        if (_skillAttemptCooldownRemaining > TimeSpan.Zero || _target is null) return;

        var options = Skills.GetUsableActiveSkillIds().ToList();
        if (options.Count == 0) return;

        var skillId = options[RandomNumberGenerator.GetInt32(options.Count)];
        Skills.Use(skillId, _target.Vid);

        _skillAttemptCooldownRemaining = SkillAttemptCooldown;
    }

    private MonsterEntity? FindTarget()
    {
        MonsterEntity? closest = null;
        var closestDistance = AggroRange;

        foreach (var entity in NearbyEntities)
        {
            if (entity is not MonsterEntity monster || monster.Dead) continue;

            var distance = this.DistanceTo(monster);
            if (distance > closestDistance) continue;

            closest = monster;
            closestDistance = distance;
        }

        return closest;
    }

    /// <summary>
    /// Every non-dead monster within melee range AND within <see cref="AttackConeHalfAngle"/> degrees of
    /// the direction the bot is currently facing (<see cref="Entity.Rotation"/>, set right before this is
    /// called) - i.e. a sword-swing arc in front of the bot, not a single point.
    /// </summary>
    private List<MonsterEntity> FindTargetsInAttackCone()
    {
        var results = new List<MonsterEntity>();

        foreach (var entity in NearbyEntities)
        {
            if (entity is not MonsterEntity monster || monster.Dead) continue;
            if (this.DistanceTo(monster) > MeleeAttackRange) continue;

            var angleToMonster = MathUtils.Rotation(monster.PositionX - PositionX, monster.PositionY - PositionY);
            if (Math.Abs(NormalizeAngleDelta(Rotation - angleToMonster)) > AttackConeHalfAngle) continue;

            results.Add(monster);
        }

        return results;
    }

    /// <summary>Wraps a degree difference into (-180, 180] so e.g. 350 vs 10 reads as 20, not 340.</summary>
    private static double NormalizeAngleDelta(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    private void TryWander(TickContext ctx)
    {
        if (Map is not Map localMap) return;

        for (var attempt = 0; attempt < MaxPositionAttempts; attempt++)
        {
            var distance = RandomNumberGenerator.GetInt32(WanderMinDistance, WanderMaxDistance + 1);
            var (deltaX, deltaY) = MathUtils.GetDeltaByDegree(RandomNumberGenerator.GetInt32(0, 360));
            var targetX = _spawnX + (int)(deltaX * distance);
            var targetY = _spawnY + (int)(deltaY * distance);

            if (!localMap.IsPositionInside(targetX, targetY)) continue;
            if (localMap.IsAttr(new Coordinates((uint)targetX, (uint)targetY),
                    EMapAttributes.BLOCK | EMapAttributes.OBJECT)) continue;

            Rotation = (float)MathUtils.Rotation(targetX - PositionX, targetY - PositionY);
            Goto(targetX, targetY, ctx.Timestamp);
            BroadcastMove(ctx);
            return;
        }
    }

    /// <summary>
    /// Same packet CharacterMoveHandler sends to nearby players for a real client-driven move
    /// (src/Libraries/Game.Server/PacketHandlers/Game/CharacterMoveHandler.cs) - nothing centralizes
    /// this broadcast in Entity/PlayerEntity itself, so server-driven movement has to replicate it.
    /// </summary>
    private void BroadcastMove(TickContext ctx)
    {
        Broadcast(new CharacterMoveOut
        {
            MovementType = CharacterMovementType.MOVE,
            Rotation = (byte)(Rotation / 5),
            Vid = Vid,
            PositionX = TargetPositionX,
            PositionY = TargetPositionY,
            Time = (uint)ctx.TotalElapsed.TotalMilliseconds,
            Duration = MovementDuration
        });
    }

    /// <summary>
    /// Corrects an observing client's belief about where this bot actually is, after the bot's own
    /// UpdateCombat stopped a walk early (mid-way through an earlier Goto's Duration) to start attacking.
    /// Unlike <see cref="BroadcastMove"/> (which reports <see cref="Entity.TargetPositionX"/>/Y, the
    /// destination of an in-progress walk), this reports the bot's actual current, already-arrived
    /// PositionX/Y with Duration=0 - an instant snap, not a new walk - so a subsequent
    /// <see cref="BroadcastAttack"/> lands within the ~50-unit gap the client requires to play the swing
    /// animation immediately instead of walking the bot to the (stale) old destination first. See
    /// BroadcastAttack's own doc comment for the full client-side mechanics this was reverse-engineered
    /// from.
    /// </summary>
    private void BroadcastStop(TickContext ctx)
    {
        Broadcast(new CharacterMoveOut
        {
            MovementType = CharacterMovementType.MOVE,
            Rotation = (byte)(Rotation / 5),
            Vid = Vid,
            PositionX = PositionX,
            PositionY = PositionY,
            Time = (uint)ctx.TotalElapsed.TotalMilliseconds,
            Duration = 0
        });
    }

    /// <summary>
    /// Sends the same FUNC_ATTACK/FUNC_COMBO move real players send for a melee swing (verified against
    /// ClientVS22/source/UserInterface/InstanceBase.cpp, which is what makes an OBSERVING client actually
    /// play the attacker's own weapon/combo animation - each client renders it using its own copy of the
    /// attacker's class+weapon motion data, keyed by this Argument byte, so the bot doesn't need to know
    /// which exact animation that is). Purely cosmetic - the actual damage comes from Attack() below via
    /// Entity.Damage(), which already sends DamageInfo to both sides itself.
    /// </summary>
    private void BroadcastAttack(TickContext ctx)
    {
        CharacterMovementType movementType;
        byte argument;

        if (_comboIndex == 0)
        {
            // First hit of a fresh engagement - a real client always starts with a plain attack, never a
            // combo step.
            movementType = CharacterMovementType.ATTACK;
            argument = 0;
            _comboIndex = 2; // the next hit continues the combo at step 2
        }
        else
        {
            movementType = CharacterMovementType.COMBO;
            argument = _comboIndex;
            _comboIndex++;
            if (_comboIndex > ComboMaxIndex)
            {
                _comboIndex = 0; // combo finished - next hit starts a fresh attack, like a real client does
            }
        }

        Broadcast(new CharacterMoveOut
        {
            MovementType = movementType,
            Argument = argument,
            Rotation = (byte)(Rotation / 5),
            Vid = Vid,
            PositionX = PositionX,
            PositionY = PositionY,
            Time = (uint)ctx.TotalElapsed.TotalMilliseconds
        });
    }

    private void Broadcast(CharacterMoveOut packet)
    {
        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity player)
            {
                player.Connection.Send(packet);
            }
        }
    }
}
