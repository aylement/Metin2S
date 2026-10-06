using QuantumCore.API;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Core.Utils;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Combat;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.World;
using QuantumCore.API.Packets;
using QuantumCore.Core.Constants;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Extensions;

namespace QuantumCore.Game.World.Entities;

public abstract class Entity : IEntity
{
    private readonly IAnimationManager _animationManager;
    public uint Vid { get; }
    public EEmpire Empire { get; private protected set; }
    public abstract EEntityType Type { get; }
    public uint EntityClass { get; protected set; }
    public EEntityState State { get; protected set; }
    public virtual IEntity? Target { get; set; }

    public int PositionX
    {
        get => _positionX;
        set
        {
            _positionChanged = _positionChanged || _positionX != value;
            _positionX = value;
        }
    }

    public int PositionY
    {
        get => _positionY;
        set
        {
            _positionChanged = _positionChanged || _positionY != value;
            _positionY = value;
        }
    }

    public float Rotation
    {
        get => _rotation;
        set => _rotation = value;
    }

    public bool PositionChanged
    {
        get => _positionChanged;
        set => _positionChanged = value;
    }

    public long Health { get; set; }
    public long Mana { get; set; }
    public abstract byte HealthPercentage { get; }
    public bool Dead { get; protected set; }

    public IMap? Map { get; set; }

    // QuadTree cache
    public int LastPositionX { get; set; }
    public int LastPositionY { get; set; }
    public IQuadTree? LastQuadTree { get; set; }

    // Movement related
    public ServerTimestamp MovementStart { get; private set; }
    public int TargetPositionX { get; private set; }
    public int StartPositionX { get; private set; }
    public int TargetPositionY { get; private set; }
    public int StartPositionY { get; private set; }
    public uint MovementDuration { get; private set; }
    public byte MovementSpeed { get; set; }
    public byte AttackSpeed { get; set; }

    // See the long comment in Goto() - this is the assumed real-world speed (units/second) used to time
    // movement when no real per-model animation data is available (currently: always, for every entity
    // type - no *.msa files are shipped in this repo). Monsters want this conservative/slow (avoids
    // landing hits before they've visually arrived); the player needs it as close to their true speed as
    // possible (this position is both the view-circle center AND what gets persisted on disconnect).
    protected virtual double FallbackMovementUnitsPerSecond => 250.0;

    public IReadOnlyCollection<IEntity> NearbyEntities => _nearbyEntities;
    private readonly List<IEntity> _nearbyEntities = new();
    public List<IPlayerEntity> TargetedBy { get; } = new();
    // Was briefly dropped to 7500 - at the time, mobs seemed visible from further away than they should
    // be, and the culprit turned out to be the real bug this whole investigation was chasing (the
    // player's own position lagging behind their true position - see FallbackMovementUnitsPerSecond).
    // 10000 (the real official value) was live-confirmed correct after that fix. Bumped again to 15000
    // per user preference while building/testing the Devil Tower content (wanted more visibility inside
    // the tower) - no longer strictly "the real official value", a deliberate live tuning choice.
    public const int VIEW_DISTANCE = 15000;

    private int _positionX;
    private int _positionY;
    private float _rotation;
    private bool _positionChanged;
    protected PlayerEntity? LastAttacker { get; private set; }

    protected Entity(IAnimationManager animationManager, uint vid)
    {
        _animationManager = animationManager;
        Vid = vid;
    }

    protected abstract void OnNewNearbyEntity(IEntity entity);
    protected abstract void OnRemoveNearbyEntity(IEntity entity);
    public abstract void OnDespawn();
    public abstract void ShowEntity(IConnection connection);
    public abstract void HideEntity(IConnection connection);

    public virtual void Update(TickContext ctx)
    {
        if (State == EEntityState.MOVING)
        {
            var elapsed = ctx.ElapsedSince(MovementStart);
            var rate = MovementDuration == 0 ? 1 : elapsed.TotalMilliseconds / (float)MovementDuration;
            if (rate > 1) rate = 1;

            var x = (int)((TargetPositionX - StartPositionX) * rate + StartPositionX);
            var y = (int)((TargetPositionY - StartPositionY) * rate + StartPositionY);

            PositionX = x;
            PositionY = y;

            if (rate >= 1)
            {
                State = EEntityState.IDLE;
            }
        }
    }

    public virtual void Move(int x, int y)
    {
        if (PositionX == x && PositionY == y) return;

        PositionX = x;
        PositionY = y;
        PositionChanged = true;
    }

    public void Goto(Coordinates position, ServerTimestamp startAt) =>
        Goto((int)position.X, (int)position.Y, startAt);

    public virtual void Goto(int x, int y, ServerTimestamp startAt)
    {
        if (PositionX == x && PositionY == y) return;
        if (TargetPositionX == x && TargetPositionY == y) return;

        var animation =
            _animationManager.GetAnimation(EntityClass, AnimationType.RUN, AnimationSubType.GENERAL);

        State = EEntityState.MOVING;
        TargetPositionX = x;
        TargetPositionY = y;
        StartPositionX = PositionX;
        StartPositionY = PositionY;
        MovementStart = startAt;

        var distance = MathUtils.Distance(StartPositionX, StartPositionY, TargetPositionX, TargetPositionY);

        // Real per-model animation data (walk.msa/run.msa, driving `animationSpeed` below) isn't shipped in
        // this repo's data set for every entity class (most monster races have no data/monster/<folder>
        // motion files at all, AND player classes have none either - confirmed live, no *.msa file exists
        // anywhere in this repo - never extracted from the real client packs, see AnimationManager). This
        // used to fall back to MovementDuration = 0 whenever that lookup failed, which made Update() snap
        // PositionX/Y straight to the destination on the very next tick regardless of real distance/elapsed
        // time - i.e. the entity's SERVER-SIDE position teleported to its Goto() target instantly, while an
        // observing client (which does have real motion assets) kept walking it there realistically over
        // several more seconds. Since gameplay decisions (e.g. "is this monster within melee range yet?")
        // read the server's position, this let monsters land real, server-validated hits from tens of
        // meters away, well before they visually arrived on anyone's screen - reported as "mobs hit me from
        // range / while still running towards me". Falling back to a conservative (slow) flat speed instead
        // keeps movement taking a believable amount of time even without real per-model timing data.
        //
        // That same shared fallback, applied to the PLAYER's own movement, turned out to cause a SEPARATE,
        // opposite bug: the server's dead-reckoned PositionX/Y (used as the center of the nearby-entity
        // view circle, and what gets persisted on disconnect) fell further and further behind the client's
        // true on-screen position over a long sustained run, since 250u/s underestimates real run speed -
        // live-confirmed via reconnecting mid-run: the character always reappeared BEHIND where it visually
        // was, exactly at the position the reveal/hide circle had actually been centered on the whole time.
        // "Conservative/slow" is the right default for monsters (avoids the early-hit bug above) but wrong
        // for the player's own movement (needs to track the true position as closely as possible) - split
        // via FallbackMovementUnitsPerSecond so each entity type can pick what it actually needs.
        var animationSpeed = animation is null
            ? FallbackMovementUnitsPerSecond
            : -animation.AccumulationY / animation.MotionDuration;

        var i = 100 - MovementSpeed;
        if (i > 0)
        {
            i = 100 + i;
        }
        else if (i < 0)
        {
            i = 10000 / (100 - i);
        }
        else
        {
            i = 100;
        }

        var duration = (int)((distance / animationSpeed) * 1000) * i / 100;
        MovementDuration = (uint)duration;
    }

    public virtual void Wait(int x, int y)
    {
        // todo: Verify position possibility
        PositionX = x;
        PositionY = y;
    }

    public void Stop()
    {
        State = EEntityState.IDLE;
        MovementDuration = 0;
    }

    public abstract EBattleType GetBattleType();
    public abstract int GetMinDamage();
    public abstract int GetMaxDamage();
    public abstract int GetBonusDamage();
    public abstract void AddPoint(EPoint point, int value);
    public abstract void SetPoint(EPoint point, uint value);
    public abstract uint GetPoint(EPoint point);

    public void Attack(IEntity victim)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (this.PositionIsAttr(EMapAttributes.NON_PVP))
        {
            return;
        }

        if (victim.PositionIsAttr(EMapAttributes.NON_PVP))
        {
            return;
        }

        switch (GetBattleType())
        {
            case EBattleType.MELEE:
            case EBattleType.POWER:
            case EBattleType.TANKER:
            case EBattleType.SUPER_POWER:
            case EBattleType.SUPER_TANKER:
                // melee sort attack
                MeleeAttack(victim);
                break;
            case EBattleType.RANGE:
            case EBattleType.MAGIC:
                // The real server (char_battle.cpp CHARACTER::Attack) sends both of these through the
                // same underlying Shoot() routine - a plain ranged shot for RANGE (Shoot(0)) vs. a magic
                // bolt for MAGIC (Shoot(1)) - not two different damage formulas. RangeAttack here already
                // factors in MAGIC_ATTACK_BONUS on top of ATTACK_BONUS, so it already covers both; this
                // case used to be a bare `// todo magic attack` no-op, making every MAGIC-type monster
                // (50 in this repo's mob_proto, e.g. White Oath General/Commander, Mi-Jung, Eun-Jung)
                // completely harmless in melee/ranged combat.
                RangeAttack(victim);
                break;
        }
    }

    private void MeleeAttack(IEntity victim)
    {
        // todo verify victim is in range

        var attackerRating = Math.Min(90, (GetPoint(EPoint.DX) * 4 + GetPoint(EPoint.LEVEL) * 2) / 6);
        var victimRating = Math.Min(90, (victim.GetPoint(EPoint.DX) * 4 + victim.GetPoint(EPoint.LEVEL) * 2) / 6);
        var attackRating = (attackerRating + 210.0) / 300.0 -
                           (victimRating * 2 + 5f) / (victimRating + 95) * 3.0 / 10.0;

        var minDamage = GetMinDamage();
        var maxDamage = GetMaxDamage();

        var damage = CoreRandom.GenerateInt32(minDamage, maxDamage + 1) * 2;
        SendDebugDamage(victim, $"{this}->{victim} Base Attack value: {damage}");
        var attack = (int)(GetPoint(EPoint.ATTACK_GRADE) + damage - GetPoint(EPoint.LEVEL) * 2);
        attack = (int)Math.Floor(attack * attackRating);
        attack += (int)GetPoint(EPoint.LEVEL) * 2 + GetBonusDamage() * 2;
        attack *= (int)((100 + GetPoint(EPoint.ATTACK_BONUS) + GetPoint(EPoint.MAGIC_ATTACK_BONUS)) / 100);
        attack = CalculateAttackBonus(victim, attack);
        SendDebugDamage(victim, $"{this}->{victim} With bonus and level {attack}");

        var defence = (int)(victim.GetPoint(EPoint.DEFENCE_GRADE) * (100 + victim.GetPoint(EPoint.DEFENCE_BONUS)) /
                            100);
        SendDebugDamage(victim, $"{this}->{victim} Base defence: {defence}");
        if (this is MonsterEntity thisMonster)
        {
            attack = (int)Math.Floor(attack * thisMonster.Proto.DamageMultiply);
        }

        damage = Math.Max(0, attack - defence);
        SendDebugDamage(victim, $"{this}->{victim} Melee damage: {damage}");
        if (damage < 3)
        {
            damage = CoreRandom.GenerateInt32(1, 6);
        }

        // todo reduce damage by weapon type resist

        victim.Damage(this, EDamageType.NORMAL, damage);
    }

    /// <summary>
    /// The same pre-defense "attack power" MeleeAttack computes internally (attacker/victim rating,
    /// weapon roll, ATTACK_GRADE, level and bonus%), exposed as its own method for skill casting to reuse
    /// as the "atk" variable in skilltable.txt's formulas - matching the original server's own
    /// <c>CalcMeleeDamage()</c>, which every active skill's formula is built to expect as "atk" (see
    /// char_skill.cpp's <c>SetPointVar("atk", CalcMeleeDamage(...))</c>). Deliberately NOT used by
    /// MeleeAttack itself (left untouched, already working) to avoid any risk of regressing normal combat
    /// while wiring up skills - a small amount of duplication traded for zero blast radius.
    /// </summary>
    public int CalculateAttackPower(IEntity victim)
    {
        ArgumentNullException.ThrowIfNull(victim);

        var attackerRating = Math.Min(90, (GetPoint(EPoint.DX) * 4 + GetPoint(EPoint.LEVEL) * 2) / 6);
        var victimRating = Math.Min(90, (victim.GetPoint(EPoint.DX) * 4 + victim.GetPoint(EPoint.LEVEL) * 2) / 6);
        var attackRating = (attackerRating + 210.0) / 300.0 -
                           (victimRating * 2 + 5f) / (victimRating + 95) * 3.0 / 10.0;

        var minDamage = GetMinDamage();
        var maxDamage = GetMaxDamage();

        var damage = CoreRandom.GenerateInt32(minDamage, maxDamage + 1) * 2;
        var attack = (int)(GetPoint(EPoint.ATTACK_GRADE) + damage - GetPoint(EPoint.LEVEL) * 2);
        attack = (int)Math.Floor(attack * attackRating);
        attack += (int)GetPoint(EPoint.LEVEL) * 2 + GetBonusDamage() * 2;
        attack *= (int)((100 + GetPoint(EPoint.ATTACK_BONUS) + GetPoint(EPoint.MAGIC_ATTACK_BONUS)) / 100);
        attack = CalculateAttackBonus(victim, attack);

        if (this is MonsterEntity thisMonster)
        {
            attack = (int)Math.Floor(attack * thisMonster.Proto.DamageMultiply);
        }

        return attack;
    }

    private void RangeAttack(IEntity victim)
    {
        // todo verify victim is in range

        var attackerRating = Math.Min(90, (GetPoint(EPoint.DX) * 4 + GetPoint(EPoint.LEVEL) * 2) / 6);
        var victimRating = Math.Min(90, (victim.GetPoint(EPoint.DX) * 4 + victim.GetPoint(EPoint.LEVEL) * 2) / 6);
        var attackRating = (attackerRating + 210.0) / 300.0 -
                           (victimRating * 2 + 5f) / (victimRating + 95) * 3.0 / 10.0;

        var minDamage = GetMinDamage();
        var maxDamage = GetMaxDamage();

        var damage = CoreRandom.GenerateInt32(minDamage, maxDamage + 1) * 2;
        var attack = (int)(GetPoint(EPoint.ATTACK_GRADE) + damage - GetPoint(EPoint.LEVEL) * 2);
        attack = (int)Math.Floor(attack * attackRating);
        attack += (int)GetPoint(EPoint.LEVEL) * 2 + GetBonusDamage() * 2;
        attack *= (int)((100 + GetPoint(EPoint.ATTACK_BONUS) + GetPoint(EPoint.MAGIC_ATTACK_BONUS)) / 100);
        attack = CalculateAttackBonus(victim, attack);

        var defence = (int)(victim.GetPoint(EPoint.DEFENCE_GRADE) * (100 + victim.GetPoint(EPoint.DEFENCE_BONUS)) /
                            100);
        if (this is MonsterEntity thisMonster)
        {
            attack = (int)Math.Floor(attack * thisMonster.Proto.DamageMultiply);
        }

        damage = Math.Max(0, attack - defence);
        if (damage < 3)
        {
            damage = CoreRandom.GenerateInt32(1, 6);
        }

        // todo reduce damage by weapon type resist

        foreach (var player in NearbyEntities.Where(x => x is IPlayerEntity).Cast<IPlayerEntity>())
        {
            player.Connection.Send(new ProjectilePacket
            {
                TargetX = victim.PositionX, TargetY = victim.PositionY, Target = victim.Vid, Shooter = Vid
            });
        }

        victim.Damage(this, EDamageType.NORMAL_RANGE, damage);
    }

    /// <summary>
    /// Adds bonus to the attack value for race bonus etc
    /// </summary>
    /// <param name="victim">The victim of the damage</param>
    /// <param name="attack">The current attack value</param>
    /// <returns>The new attack value with the bonus</returns>
    // Real formula (CalcAttBonus, battle.cpp): several % damage bonuses get applied here, most notably a
    // general "bonus vs monsters" stat (POINT_ATTBONUS_MONSTER, applies to any non-player target) plus
    // per-race bonuses (vs animal/undead/human/etc.) that mostly matter for PvP or specific monster race
    // flags. In the real game those percentages come from ITEM ATTRIBUTES - random affix rolls on gear -
    // a whole subsystem this repo doesn't have at all (every item here is a static proto, no per-instance
    // rolled stats, confirmed nowhere in this codebase). Rather than add EPoint plumbing with no real data
    // source ever behind it, this is a flat, deliberately-hardcoded placeholder for the general vs-monster
    // case only - NOT real per-item data, just enough to land combat against monsters in a more realistic
    // range for testing (a real, geared/attribute-rolled character would have a nonzero value here; this
    // repo's static-proto items never will without that missing subsystem). Confirmed live: user-observed
    // damage was significantly below a real player's video-referenced numbers on a comparable metin even
    // after the ATTACK_GRADE base-value fix - this was flagged as the most likely remaining gap. Revisit if
    // an item-attribute system is ever built, replacing this constant with a real GetPoint(EPoint.ATTBONUS_MONSTER).
    private const int PlaceholderMonsterDamageBonusPercent = 20;

    private static int CalculateAttackBonus(IEntity victim, int attack)
    {
        // todo implement bonus attack against warriors etc... (PvP-specific, POINT_ATTBONUS_* by job - not
        // relevant to the metin/monster-damage gap this was added for)
        // todo implement resist again warriors etc...
        // todo implement resist against fire etc...

        if (victim.Type is not (EEntityType.PLAYER or EEntityType.POLYMORPH_PLAYER))
        {
            attack += attack * PlaceholderMonsterDamageBonusPercent / 100;
        }

        return attack;
    }

    private int CalculateExperience(uint playerLevel)
    {
        var baseExp = GetPoint(EPoint.EXPERIENCE);
        var entityLevel = GetPoint(EPoint.LEVEL);

        var percentage = ExperienceConstants.GetExperiencePercentageByLevelDifference(playerLevel, entityLevel);

        return (int)(baseExp * percentage);
    }

    private void SendDebugDamage(IEntity other, string text)
    {
        // Off by default (PlayerEntity.DebugDamageEnabled) - this used to fire unconditionally, spamming
        // ordinary combat chat with a breakdown line for every single hit either side of a fight lands.
        // Toggle with /debug_damage.
        if (this is PlayerEntity { DebugDamageEnabled: true } thisPlayer)
        {
            thisPlayer.SendChatInfo(text);
        }

        if (other is PlayerEntity { DebugDamageEnabled: true } otherPlayer)
        {
            otherPlayer.SendChatInfo(text);
        }
    }

    public virtual int Damage(IEntity attacker, EDamageType damageType, int damage)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        if (this.PositionIsAttr(EMapAttributes.NON_PVP))
        {
            SendDebugDamage(attacker,
                $"{attacker}->{this} Ignoring damage inside NoPvP zone -> {damage} (should never happen)");
            return -1;
        }

        if (damageType is not EDamageType.NORMAL and not EDamageType.NORMAL_RANGE)
        {
            throw new NotImplementedException();
        }

        // todo block
        // todo handle berserk, fear, blessing skill
        // todo handle reflect melee

        SendDebugDamage(attacker, $"{attacker}->{this} Base Damage: {damage}");

        var isCritical = false;
        var isPenetrate = false;

        var criticalPercentage = attacker.GetPoint(EPoint.CRITICAL_PERCENTAGE);
        if (criticalPercentage > 0)
        {
            var resist = GetPoint(EPoint.RESIST_CRITICAL);
            criticalPercentage = resist > criticalPercentage ? 0 : criticalPercentage - resist;
            if (CoreRandom.PercentageCheck(criticalPercentage))
            {
                isCritical = true;
                damage *= 2;

                // Real client behaviour (SPacketGCSpecialEffect / SE_CRITICAL, char.cpp::EffectPacket): a
                // dedicated packet, separate from DamageInfo below, attaches a visual (yellow flash) to the
                // VICTIM ("this", not the attacker) and is broadcast to every nearby player, not just the
                // two combatants - so bystanders see it too. This is NOT the same thing as the
                // DamageInfo.DamageFlags.CRITICAL bit sent further down: that flag exists in the real
                // protocol too, but the client's own DAMAGE_CRITICAL branch in ProcessDamage() only ever
                // triggers a minor particle burst next to the floating damage number, not the flash the
                // player actually remembers - this SpecialEffect packet is what triggers that.
                var criticalEffect = new SpecialEffect { Type = ESpecialEffectType.CRITICAL, Vid = Vid };
                foreach (var player in NearbyEntities.Where(x => x is IPlayerEntity).Cast<IPlayerEntity>())
                {
                    player.Connection.Send(criticalEffect);
                }

                SendDebugDamage(attacker,
                    $"{attacker}->{this} Critical hit -> {damage} (percentage was {criticalPercentage})");
            }
        }

        var penetratePercentage = attacker.GetPoint(EPoint.PENETRATE_PERCENTAGE);
        // todo add penetrate chance from passive
        if (penetratePercentage > 0)
        {
            var resist = GetPoint(EPoint.RESIST_PENETRATE);
            penetratePercentage = resist > penetratePercentage ? 0 : penetratePercentage - resist;
            if (CoreRandom.PercentageCheck(penetratePercentage))
            {
                isPenetrate = true;
                damage += (int)(GetPoint(EPoint.DEFENCE_GRADE) * (100 + GetPoint(EPoint.DEFENCE_BONUS)) / 100);
                SendDebugDamage(attacker,
                    $"{attacker}->{this} Penetrate hit -> {damage} (percentage was {penetratePercentage})");
            }
        }

        // todo calculate hp steal, sp steal, hp recovery, sp recovery and mana burn

        var damageFlags = EDamageFlags.NORMAL; // 1 = normal
        if (isCritical)
        {
            damageFlags |= EDamageFlags.CRITICAL;
        }

        if (isPenetrate)
        {
            damageFlags |= EDamageFlags.PIERCING;
        }

        var victimPlayer = this as PlayerEntity;
        var attackerPlayer = attacker as PlayerEntity;
        if (victimPlayer is not null || attackerPlayer is not null)
        {
            var damageInfo = new DamageInfo();
            damageInfo.Vid = Vid;
            damageInfo.Damage = damage;
            damageInfo.DamageFlags = damageFlags;

            if (victimPlayer is not null)
            {
                victimPlayer.Connection.Send(damageInfo);
            }

            if (attackerPlayer is not null)
            {
                attackerPlayer.Connection.Send(damageInfo);
                LastAttacker = attackerPlayer;
            }
        }

        Health -= damage;
        if (victimPlayer is not null)
        {
            victimPlayer.SendPoints();
        }

        foreach (var playerEntity in TargetedBy)
        {
            playerEntity.SendTarget();
        }

        if (Health <= 0)
        {
            Die();
            if (Type != EEntityType.PLAYER && attackerPlayer is not null)
            {
                var exp = CalculateExperience(attackerPlayer.GetPoint(EPoint.LEVEL));
                attackerPlayer.AddPoint(EPoint.EXPERIENCE, exp);
                attackerPlayer.SendPoints();

                // Fire-and-forget, same pattern as Connection.Close()'s `_ = OnCloseAsync(...)` - quests
                // react to this asynchronously and shouldn't block the damage/death pipeline.
                _ = GameEventManager.OnMonsterKillAsync(EntityClass, attackerPlayer);
            }
        }

        return damage;
    }

    public virtual void Die()
    {
        Dead = true;
    }

    public void AddNearbyEntity(IEntity entity)
    {
        _nearbyEntities.Add(entity);
        OnNewNearbyEntity(entity);
    }

    public void RemoveNearbyEntity(IEntity entity)
    {
        if (_nearbyEntities.Remove(entity))
        {
            OnRemoveNearbyEntity(entity);
        }
    }

    public void ForEachNearbyEntity(Action<IEntity> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        foreach (var entity in _nearbyEntities)
        {
            action(entity);
        }
    }
}