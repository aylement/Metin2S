using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Extensions;
using QuantumCore.API.Game.Guild;
using QuantumCore.API.Game.Skills;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Combat;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Items;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.API.Game.World;
using QuantumCore.API.Packets;
using QuantumCore.API.Packets.Guild;
using QuantumCore.API.Packets.Skills;
using QuantumCore.Extensions;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Persistence;
using QuantumCore.Game.PlayerUtils;
using QuantumCore.Game.Skills;

namespace QuantumCore.Game.World.Entities;

public class PlayerEntity : Entity, IPlayerEntity, IDisposable
{
    public override EEntityType Type => EEntityType.PLAYER;

    // See Entity.FallbackMovementUnitsPerSecond and the long comment in Entity.Goto(): monsters keep the
    // conservative 250 default, but a player's OWN tracked position needs to track their true real-time
    // speed as closely as possible - it's both the center of their view circle and what gets persisted on
    // disconnect. 250 was live-confirmed too slow (reconnecting after a long run always landed BEHIND
    // where the player visually was). 400 is a first empirical estimate, not measured against the real
    // client's actual run speed constant (no source for that in this repo) - retune based on live
    // feedback: if reconnect-after-a-long-run still lands behind, raise it further; if it now overshoots
    // ahead of where the player actually was, lower it.
    protected override double FallbackMovementUnitsPerSecond => 1200.0;

    public string Name => Player.Name;
    public IGameConnection Connection { get; }
    public PlayerData Player { get; private set; }
    public GuildData? Guild { get; private set; }
    public IInventory Inventory { get; private set; }
    public IList<Guid> Groups { get; private set; }
    public IShop? Shop { get; set; }
    public IQuickSlotBar QuickSlotBar { get; }
    public IPlayerSkills Skills { get; private set; }
    public IQuest? CurrentQuest { get; set; }
    public Dictionary<string, IQuest> Quests { get; } = new();
    public uint MountVnum { get; private set; }

    /// <summary>
    /// Off by default - gates the per-hit chat breakdown <see cref="Entity.Damage"/>'s internal
    /// SendDebugDamage sends on every single attack this player is involved in (attacker or victim). That
    /// breakdown used to be unconditional, which spammed ordinary combat chat with "Base Attack value"/
    /// "Melee damage"/etc. lines - real diagnostic value (this is exactly what let a real damage-formula
    /// bug get root-caused overnight), but not something to see by default. Toggled via
    /// <c>Commands/DebugCommand.cs</c>'s <c>/debug_damage</c>.
    /// </summary>
    public bool DebugDamageEnabled { get; set; }

    public override byte HealthPercentage
    {
        get
        {
            return 100; // todo
        }
    }

    public EAntiFlags AntiFlagClass
    {
        get
        {
            switch (Player.PlayerClass.GetClass())
            {
                case EPlayerClass.WARRIOR:
                    return EAntiFlags.WARRIOR;
                case EPlayerClass.NINJA:
                    return EAntiFlags.ASSASSIN;
                case EPlayerClass.SURA:
                    return EAntiFlags.SURA;
                case EPlayerClass.SHAMAN:
                    return EAntiFlags.SHAMAN;
                default:
                    return 0;
            }
        }
    }

    public EAntiFlags AntiFlagGender
    {
        get
        {
            switch (Player.PlayerClass.GetGender())
            {
                case EPlayerGender.MALE:
                    return EAntiFlags.MALE;
                case EPlayerGender.FEMALE:
                    return EAntiFlags.FEMALE;
                default:
                    return 0;
            }
        }
    }

    private uint _defence;

    private readonly record struct SkillBuffState(double Amount, DateTime ExpiresAt, ESkill SkillId, EAffectFlags AffectFlag);

    private readonly Dictionary<EPoint, SkillBuffState> _skillBuffs = new();

    /// <summary>Bitmask of every still-active buff's <see cref="EAffectFlags"/>, ORed together - see
    /// <see cref="IPlayerEntity.ActiveAffects"/>'s own doc comment for what actually reads this.</summary>
    public ulong ActiveAffects { get; private set; }

    /// <summary>
    /// Applies a temporary, additive bonus to one of the handful of EPoint cases GetPoint() actually
    /// layers active skill buffs onto (ATTACK_SPEED, MOVE_SPEED, ATTACK_GRADE, DEFENCE_GRADE) - used by
    /// PlayerSkills when casting a self-buff active skill (e.g. Berserker Fury, Aura of the Sword, Strong
    /// Body), whose skilltable.txt formula's PointOn/PointOn2 targets one of these. Also sends the two
    /// client-visible signals the raw stat change alone wouldn't produce: an AffectAdd packet (the buff
    /// icon in the top-left bar - self-only, the real protocol's TPacketGCAffectAdd carries no target vid
    /// at all) and an ActiveAffects-bearing CharacterUpdate broadcast (the on-model visual, e.g. a weapon
    /// aura, seen by everyone nearby).
    /// </summary>
    public void ApplySkillBuff(EPoint point, double amount, TimeSpan duration, ESkill skillId,
        EAffectFlags affectFlag, double spCost)
    {
        _skillBuffs[point] = new SkillBuffState(amount, DateTime.UtcNow + duration, skillId, affectFlag);
        ActiveAffects |= (ulong)affectFlag;

        Connection.Send(new AffectAdd
        {
            Type = (uint)skillId,
            PointIdxApplyOn = (byte)point,
            ApplyValue = (int)Math.Round(amount),
            Flag = (uint)affectFlag,
            Duration = (int)duration.TotalSeconds,
            SpCost = (int)Math.Round(spCost)
        });

        // Recompute unconditionally (not just for ATTACK_SPEED/MOVE_SPEED): the raw AttackSpeed/
        // MovementSpeed fields fold any active buff in themselves (see CalculateAttackSpeed/
        // CalculateMovement's own comments), and ActiveAffects always needs (re-)broadcasting the moment a
        // buff is added, regardless of which EPoint it targets.
        CalculateAttackSpeed();
        CalculateMovement();
        this.SendCharacterUpdate();
    }

    /// <summary>Drops every buff that's expired since the last check: clears its ActiveAffects bit, sends
    /// the matching AffectRemove, and refreshes AttackSpeed/MovementSpeed/ActiveAffects for everyone
    /// nearby - called periodically from Update() since nothing else would ever prompt this on its own.
    /// </summary>
    private void ExpireSkillBuffs()
    {
        List<(EPoint Point, SkillBuffState State)>? expired = null;
        foreach (var (point, state) in _skillBuffs)
        {
            if (state.ExpiresAt > DateTime.UtcNow) continue;
            expired ??= [];
            expired.Add((point, state));
        }

        if (expired is null) return;

        foreach (var (point, state) in expired)
        {
            _skillBuffs.Remove(point);
            ActiveAffects &= ~(ulong)state.AffectFlag;
            Connection.Send(new AffectRemove { Type = (uint)state.SkillId, ApplyOn = (byte)point });
        }

        CalculateAttackSpeed();
        CalculateMovement();
        this.SendCharacterUpdate();
    }

    private double GetSkillBuff(EPoint point) =>
        _skillBuffs.TryGetValue(point, out var buff) && buff.ExpiresAt > DateTime.UtcNow ? buff.Amount : 0;

    private const int HEALTH_REGEN_INTERVAL = 3 * 1000;
    private const int MANA_REGEN_INTERVAL = 3 * 1000;
    private ServerTimestamp? _lastHealthRegenTime;
    private ServerTimestamp? _lastManaRegenTime;
    private ServerTimestamp? _lastSpeedBuffCheckTime;
    private const int SPEED_BUFF_CHECK_INTERVAL = 1000;
    private readonly IItemManager _itemManager;
    private readonly IJobManager _jobManager;
    private readonly IExperienceManager _experienceManager;
    private readonly IQuestManager _questManager;
    private readonly ICacheManager _cacheManager;
    private readonly IWorld _world;
    private readonly ILogger<PlayerEntity> _logger;
    private readonly IServiceScope _scope;
    private readonly IItemRepository _itemRepository;
    private bool _isDisposed;

    public PlayerEntity(PlayerData player, IGameConnection connection, IItemManager itemManager,
        IJobManager jobManager,
        IExperienceManager experienceManager, IAnimationManager animationManager,
        IQuestManager questManager, ICacheManager cacheManager, IWorld world, ILogger<PlayerEntity> logger,
        IServiceProvider serviceProvider)
#pragma warning disable CA1062 // validate parameter before use - impossible here
        : base(animationManager, world.GenerateVid())
#pragma warning restore CA1062
    {
        ArgumentNullException.ThrowIfNull(player);
        Connection = connection;
        _itemManager = itemManager;
        _jobManager = jobManager;
        _experienceManager = experienceManager;
        _questManager = questManager;
        _cacheManager = cacheManager;
        _world = world;
        _logger = logger;
        _scope = serviceProvider.CreateScope();
        _itemRepository = _scope.ServiceProvider.GetRequiredService<IItemRepository>();
        Inventory = new Inventory(itemManager, _cacheManager, _itemRepository, player.Id,
            WindowType.INVENTORY, InventoryConstants.DEFAULT_INVENTORY_WIDTH,
            InventoryConstants.DEFAULT_INVENTORY_HEIGHT, InventoryConstants.DEFAULT_INVENTORY_PAGES);
        Inventory.OnSlotChanged += Inventory_OnSlotChanged;
        Player = player;
        Empire = player.Empire;
        PositionX = player.PositionX;
        PositionY = player.PositionY;
        QuickSlotBar = ActivatorUtilities.CreateInstance<QuickSlotBar>(_scope.ServiceProvider, this);
        Skills = ActivatorUtilities.CreateInstance<PlayerSkills>(_scope.ServiceProvider, this);

        MovementSpeed = PlayerConstants.DEFAULT_MOVEMENT_SPEED;
        AttackSpeed = PlayerConstants.DEFAULT_ATTACK_SPEED;
        EntityClass = (uint)player.PlayerClass;

        Groups = new List<Guid>();
    }

    private static uint GetMaxSp(IJobManager jobManager, EPlayerClassGendered playerClass, byte level, uint point)
    {
        var info = jobManager.Get(playerClass);
        if (info is null)
        {
            return 0;
        }

        return info.StartSp + info.SpPerIq * point + info.SpPerLevel * level;
    }

    private static uint GetMaxHp(IJobManager jobManager, EPlayerClassGendered playerClass, byte level, uint point)
    {
        var info = jobManager.Get(playerClass);
        if (info is null)
        {
            return 0;
        }

        return info.StartHp + info.HpPerHt * point + info.HpPerLevel * level;
    }

    public virtual async Task LoadAsync()
    {
        await Inventory.LoadAsync();
        await QuickSlotBar.LoadAsync();
        Player.MaxHp = GetMaxHp(_jobManager, Player.PlayerClass, Player.Level, Player.Ht);
        Player.MaxSp = GetMaxSp(_jobManager, Player.PlayerClass, Player.Level, Player.Iq);
        Health = (int)GetPoint(EPoint.MAX_HP); // todo: cache hp of player
        Mana = (int)GetPoint(EPoint.MAX_SP);
        await LoadPermGroupsAsync();
        await Skills.LoadAsync();
        var guildManager = _scope.ServiceProvider.GetRequiredService<IGuildManager>();
        Guild = await guildManager.GetGuildForPlayerAsync(Player.Id);
        Player.GuildId = Guild?.Id;
        _questManager.InitializePlayer(this);

        CalculateDefence();
        CalculateMovement();
        CalculateAttackSpeed();
    }

    public async Task ReloadPermissionsAsync()
    {
        Groups.Clear();
        await LoadPermGroupsAsync();
    }

    private async Task LoadPermGroupsAsync()
    {
        var commandPermissionRepository = _scope.ServiceProvider.GetRequiredService<ICommandPermissionRepository>();
        var playerId = Player.Id;

        var groups = await commandPermissionRepository.GetGroupsForPlayerAsync(playerId);

        foreach (var group in groups)
        {
            Groups.Add(group);
        }
    }

    public T? GetQuestInstance<T>() where T : class, IQuest
    {
        var id = typeof(T).FullName;
        if (id is null)
        {
            return default;
        }

        return (T)Quests[id];
    }

    private void Warp(Coordinates position) => Warp((int)position.X, (int)position.Y);

    private void Warp(int x, int y)
    {
        PositionX = x;
        PositionY = y;

        // Persist the new position before anything else below: the Warp packet sent at the end of this
        // method tells the CLIENT to disconnect and reconnect at the target server/port - that reconnect
        // is a deliberate, client-initiated close, so GameConnection.OnCloseAsync sees `expected: true`
        // and takes the same non-persisting World.DespawnEntity path we call a few lines down (only an
        // *unexpected* disconnect goes through World.DespawnPlayerAsync -> OnDespawnAsync -> PersistAsync).
        // Without this explicit call, a warp across map boundaries (e.g. GotoCommand's `/goto -m <map>`
        // jumping somewhere outside the current map's bounds) never reaches the database, so the
        // reconnecting client re-enters at the last position that WAS persisted (typically wherever the
        // player last logged in) instead of where they just warped to - confirmed live: `/goto -m
        // n_flame_01` "reloaded" the client right back onto metin2_map_c1.
        //
        // Deliberately BLOCKING here (.GetAwaiter().GetResult(), not the fire-and-forget `_ =` this started
        // as) rather than making Warp/Move async and rippling that through every IEntity.Move caller: a
        // fire-and-forget write here can race a LATER, unrelated PersistAsync() call for the same player
        // (e.g. from a subsequent /shutdown or disconnect a few seconds later) with no ordering guarantee
        // between the two Tasks - whichever completes last wins, so the slower (older, stale-by-then)
        // write can clobber the newer one. Confirmed live: reconnecting landed on a position from BEFORE
        // the last /goto, not the actual last position. A cross-map warp is rare/one-off, not a hot path,
        // so a brief block here is an acceptable trade for guaranteed write ordering.
#pragma warning disable VSTHRD002 // use await - TODO, same pattern as World.cs's SpawnEntity/DespawnEntity
        PersistAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        _world.DespawnEntity(this);

        var host = _world.GetMapHost(PositionX, PositionY);

        _logger.LogInformation("Warp!");
        var packet = new Warp
        {
            PositionX = PositionX,
            PositionY = PositionY,
            ServerAddress = BitConverter.ToInt32(host.Ip.GetAddressBytes()),
            ServerPort = host.Port
        };
        Connection.Send(packet);
    }

    public void Move(Coordinates position) => Move((int)position.X, (int)position.Y);

    public override void Move(int x, int y)
    {
        if (Map is null) return;
        if (PositionX == x && PositionY == y) return;

        if (!Map.IsPositionInside(x, y))
        {
            Warp(x, y);
            return;
        }

        if (Map is Map localMap &&
            localMap.IsAttr(new Coordinates((uint)x, (uint)y), EMapAttributes.BLOCK | EMapAttributes.OBJECT))
        {
            _logger.LogDebug(
                "Not allowed to move character {Name} to map position ({X}, {Y}) with attributes Block or Object", Name,
                x, y);
            return;
        }

        PositionX = x;
        PositionY = y;

        // Reset movement info
        Stop();
    }

    private void CalculateDefence()
    {
        _defence = GetPoint(EPoint.LEVEL) + (uint)Math.Floor(0.8 * GetPoint(EPoint.HT));

        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            var item = Inventory.EquipmentWindow.GetItem(slot);
            if (item is null) continue;
            var proto = _itemManager.GetItem(item.ItemId);
            if (proto is null || !proto.IsType(EItemType.ARMOR)) continue;

            _defence += (uint)proto.Values[1] + (uint)proto.Values[5] * 2;
        }

        // Same floor (not additive) treatment as ST/DX/HT/IQ in GetPoint() above - matches the original
        // server's `if (iArmor < GetHorseArmor()) iArmor = GetHorseArmor();`.
        if (MountVnum != 0)
        {
            _defence = Math.Max(_defence, HorseStats.Armor(Player.HorseLevel));
        }

        _logger.LogDebug("Calculate defence value for {Name}, result: {Defence}", Name, _defence);

        // todo add defence bonus from quests
    }

    /// <summary>
    /// The base (unbuffed) ATTACK_GRADE value - confirmed real via the original server's
    /// <c>CHARACTER::ComputePoints()</c> (char.cpp): <c>iAtk = level*2 + iStatAtk</c>, where
    /// <c>iStatAtk</c> is a job-specific stat blend (Warrior/Sura: pure STR; Ninja: STR+DX; Shaman:
    /// STR+IQ - Sura mirrors Warrior despite being a hybrid caster class in the real formula too, not a
    /// simplification here). Live-tested and confirmed this repo's own <see cref="GetPoint"/> case for
    /// ATTACK_GRADE returned ONLY an active skill buff before this - a level-90 character's own STR/DX/IQ
    /// contributed NOTHING to their base attack power at all (real damage numbers came out far below what
    /// the stats would suggest - MeleeAttack's "attack" variable starts from this same ATTACK_GRADE point).
    /// This repo's own <see cref="CalculateAttackDamage"/>/<c>Job.AttackStatus</c> were an earlier, related
    /// attempt at modeling the job-specific secondary stat, but <c>AttackStatus</c> is never actually
    /// configured anywhere (no job.txt/config entry sets it, confirmed), and the two formulas don't match
    /// on shape anyway - hardcoded directly here against the real source instead of reusing either.
    /// Item ATT_GRADE_BONUS terms from the real formula are still deliberately not modeled (no equivalent
    /// data source wired up in this codebase yet, same scope limitation as CalculateDefence() above), but
    /// the MOUNT bonus below now is - user-reported live: mounting didn't seem to change damage at all,
    /// which is real (see below).
    /// </summary>
    private uint GetBaseAttackGrade()
    {
        var st = GetPoint(EPoint.ST);
        var dx = GetPoint(EPoint.DX);
        var iq = GetPoint(EPoint.IQ);

        var statAttack = Player.PlayerClass.GetClass() switch
        {
            EPlayerClass.NINJA => (4 * st + 2 * dx) / 3,
            EPlayerClass.SHAMAN => (4 * st + 2 * iq) / 3,
            _ => 2 * st // WARRIOR, SURA
        };

        var baseAttack = GetPoint(EPoint.LEVEL) * 2 + statAttack;

        // Real formula (same ComputePoints() block, right after iAtk += iStatAtk above): mounted combat
        // adds a PERCENTAGE of the current total, scaling with horse level - `iAtk += iAtk*horseLevel/30`
        // for everyone, HALVED (`/60`) specifically for a Sura on the sword branch (BRANCH_A) - a
        // deliberate real-game nerf (source comment: "검수라 데미지 감소", "sword-sura damage reduction")
        // since that specific combination is already strong; Sura's other branch and every other
        // class/branch get the full /30. At the max horse tier (level 30) this is up to +100% (full
        // doubling) for non-sword-Sura riders - a real, substantial bonus, not a minor tweak.
        if (MountVnum != 0)
        {
            var isSwordSura = Player.PlayerClass.GetClass() == EPlayerClass.SURA &&
                               Player.SkillGroup == ESkillGroup.BRANCH_A;
            var divisor = isSwordSura ? 60 : 30;
            baseAttack += (uint)(baseAttack * Player.HorseLevel / divisor);
        }

        return baseAttack;
    }

    private void CalculateMovement()
    {
        MovementSpeed = PlayerConstants.DEFAULT_MOVEMENT_SPEED;
        float modifier = 0;
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            var item = Inventory.EquipmentWindow.GetItem(slot);
            if (item is null) continue;
            var proto = _itemManager.GetItem(item.ItemId);
            if (proto is null || !proto.IsType(EItemType.ARMOR)) continue;

            modifier += proto.GetApplyValue(EApplyType.MOV_SPEED);
        }

        var calculatedSpeed = MovementSpeed * (1 + modifier / 100);

        MovementSpeed = (byte)Math.Min(calculatedSpeed, byte.MaxValue);

        // See the matching comment in CalculateAttackSpeed() - CharacterUpdate/SpawnCharacter send this
        // raw field directly, bypassing GetPoint, so a skill buff has to be baked in here to actually be
        // felt client-side.
        MovementSpeed = (byte)Math.Clamp(MovementSpeed + GetSkillBuff(EPoint.MOVE_SPEED), 0, byte.MaxValue);

        _logger.LogDebug("Calculate Movement value for {Name}, result: {MovementSpeed}", Name, MovementSpeed);
    }

    private void CalculateAttackSpeed()
    {
        AttackSpeed = PlayerConstants.DEFAULT_ATTACK_SPEED;
        float modifier = 0;
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            var item = Inventory.EquipmentWindow.GetItem(slot);
            if (item is null) continue;
            var proto = _itemManager.GetItem(item.ItemId);
            if (proto is null) continue;

            modifier += proto.GetApplyValue(EApplyType.ATTACK_SPEED);
        }

        AttackSpeed = (byte)Math.Min(AttackSpeed * (1 + modifier / 100), byte.MaxValue);

        // Active skill buffs (e.g. Berserker Fury) fold in here, not just in GetPoint(): CharacterUpdate/
        // SpawnCharacter (see PlayerExtensions.cs) send this raw AttackSpeed field directly to the client,
        // bypassing GetPoint entirely - a buff that only lived in GetPoint's ATTACK_SPEED case would be
        // correct for combat math but invisible to the client's own felt attack speed. Found live: a cast
        // Berserker Fury changed nothing the player could feel, because this exact gap.
        AttackSpeed = (byte)Math.Clamp(AttackSpeed + GetSkillBuff(EPoint.ATTACK_SPEED), 0, byte.MaxValue);
    }

    /// <summary>
    /// Sums a fixed item-proto Apply value (e.g. CRITICAL_PCT) across every currently equipped item - same
    /// aggregation CalculateMovement()/CalculateAttackSpeed() already do for MOV_SPEED/ATTACK_SPEED, just
    /// not cached in a field since callers here (GetPoint's CRITICAL_PERCENTAGE/RESIST_CRITICAL cases) are
    /// only evaluated once per hit, not every tick.
    /// </summary>
    private int GetEquipmentApplySum(EApplyType type)
    {
        var sum = 0;
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            var item = Inventory.EquipmentWindow.GetItem(slot);
            if (item is null) continue;
            var proto = _itemManager.GetItem(item.ItemId);
            if (proto is null) continue;

            sum += proto.GetApplyValue(type);
        }

        return sum;
    }

    public override void Die()
    {
        if (Dead)
        {
            return;
        }

        base.Die();

        var dead = new CharacterDead { Vid = Vid };
        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity player)
            {
                player.Connection.Send(dead);
            }
        }

        Connection.Send(dead);
    }

    private void SendGuildInfo()
    {
        if (Guild is not null)
        {
            var onlineMemberIds = _world.GetGuildMembers(Guild.Id).Select(x => x.Player.Id).ToArray();
            Connection.SendGuildMembers(Guild.Members, onlineMemberIds);
            Connection.SendGuildRanks(Guild.Ranks);
            Connection.SendGuildInfo(Guild);
            Connection.Send(new GuildName { Id = Guild.Id, Name = Guild.Name });
        }
    }

    public async Task RefreshGuildAsync()
    {
        var guildManager = _scope.ServiceProvider.GetRequiredService<IGuildManager>();
        Guild = await guildManager.GetGuildForPlayerAsync(Player.Id);
        Player.GuildId = Guild?.Id;
        SendGuildInfo();
        this.SendCharacterUpdate();
    }

    public void Respawn(bool town)
    {
        if (!Dead)
        {
            return;
        }

        Shop?.Close(this);

        Dead = false;

        if (town && TryGetTownCoordinates(Player.Empire, Map!, out var coordinates))
        {
            Move(coordinates.Value);
        }

        // todo spawn with invisible affect

        this.SendChatCommand("CloseRestartWindow");
        Connection.SetPhase(EPhase.GAME);

        var remove = new RemoveCharacter { Vid = Vid };

        Connection.Send(remove);
        ShowEntity(Connection);

        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity pe)
            {
                ShowEntity(pe.Connection);
            }

            entity.ShowEntity(Connection);
        }

        Health = PlayerConstants.RESPAWN_HEALTH;
        Mana = PlayerConstants.RESPAWN_MANA;
        this.SendPoints();
    }

    private static bool TryGetTownCoordinates(EEmpire playerEmpire, IMap map,
        [NotNullWhen(true)] out Coordinates? coordinates)
    {
        coordinates = null;
        var townCoordinates = map.TownCoordinates;
        if (townCoordinates is not null)
        {
            coordinates = playerEmpire switch
            {
                EEmpire.CHUNJO => townCoordinates.Chunjo,
                EEmpire.JINNO => townCoordinates.Jinno,
                EEmpire.SHINSOO => townCoordinates.Shinsoo,
                _ => throw new ArgumentOutOfRangeException(nameof(playerEmpire),
                    $"Can't get empire coordinates for empire {playerEmpire}")
            };
            return true;
        }

        return false;
    }

    public void RecalculateStatusPoints()
    {
        var shouldHavePoints = (uint)((Player.Level - 1) * 3);
        var steps = (byte)Math.Floor(
            GetPoint(EPoint.EXPERIENCE) / (double)GetPoint(EPoint.NEEDED_EXPERIENCE) * 4);
        shouldHavePoints += steps;

        if (shouldHavePoints <= Player.GivenStatusPoints)
        {
            // Remove available points if possible
            var tooMuch = Player.GivenStatusPoints - shouldHavePoints;
            if (Player.AvailableStatusPoints < tooMuch)
            {
                tooMuch = Player.AvailableStatusPoints;
            }

            Player.AvailableStatusPoints -= tooMuch;
            Player.GivenStatusPoints -= tooMuch;

            return;
        }

        Player.AvailableStatusPoints += shouldHavePoints - Player.GivenStatusPoints;
        Player.GivenStatusPoints = shouldHavePoints;
    }

    private bool CheckLevelUp()
    {
        var exp = GetPoint(EPoint.EXPERIENCE);
        var needed = GetPoint(EPoint.NEEDED_EXPERIENCE);

        if (needed > 0 && exp >= needed)
        {
            SetPoint(EPoint.EXPERIENCE, exp - needed);
            LevelUp();

            if (!CheckLevelUp())
            {
                this.SendPoints();
            }

            return true;
        }

        RecalculateStatusPoints();
        return false;
    }

    private void LevelUp(int level = 1)
    {
        if (Player.Level + level > _experienceManager.MaxLevel)
        {
            return;
        }

        AddPoint(EPoint.SKILL, level);
        AddPoint(EPoint.SUB_SKILL, level < 10 ? 0 : level - Math.Max((int)Player.Level, 9));

        Player.Level = (byte)(Player.Level + level);

        // Same gap as EPoint.HT/IQ in AddPoint() above: both GetMaxHp/GetMaxSp AND CalculateDefence() also
        // read the character's Level directly (HpPerLevel/SpPerLevel, and the LEVEL term in
        // CalculateDefence), but this method never recomputed either before now - a level-up bumped
        // NEEDED_EXPERIENCE etc. immediately but left Max HP/SP/Defence stale until next relog, exactly
        // like the stat-point case.
        Player.MaxHp = GetMaxHp(_jobManager, Player.PlayerClass, Player.Level, Player.Ht);
        Player.MaxSp = GetMaxSp(_jobManager, Player.PlayerClass, Player.Level, Player.Iq);
        CalculateDefence();

        // todo: animation (I think this actually is a quest sent by the server on character login and not an actual packet at this stage)

        foreach (var entity in NearbyEntities)
        {
            if (entity is not IPlayerEntity other) continue;
            this.SendCharacterAdditional(other.Connection);
        }

        RecalculateStatusPoints();
        this.SendPoints();
    }

    public uint CalculateAttackDamage(uint baseDamage)
    {
        var attackStatus = _jobManager.Get(Player.PlayerClass)?.AttackStatus;

        if (attackStatus is null) return 0;

        var levelBonus = GetPoint(EPoint.LEVEL) * 2;
        var statusBonus = (
            4 * GetPoint(EPoint.ST) +
            2 * GetPoint(attackStatus.Value)
        ) / 3;
        var weaponDamage = baseDamage * 2;

        return levelBonus + (statusBonus + weaponDamage) * GetHitRate() / 100;
    }

    public uint GetHitRate()
    {
        var b = (GetPoint(EPoint.DX) * 4 + GetPoint(EPoint.LEVEL) * 2) / 6;
        return 100 * ((b > 90 ? 90 : b) + 210) / 300;
    }

    public override void Update(TickContext ctx)
    {
        if (Map is null) return; // We don't have a map yet so we aren't spawned

        base.Update(ctx);

        var hpOrSpChanged = false;

        var maxHp = GetPoint(EPoint.MAX_HP);
        if (Health < maxHp && !Dead)
        {
            if (!_lastHealthRegenTime.HasValue)
            {
                // start counting interval only from first viable reset
                _lastHealthRegenTime = ctx.Timestamp;
            }
            else if (ctx.ElapsedSince(_lastHealthRegenTime.Value) > TimeSpan.FromMilliseconds(HEALTH_REGEN_INTERVAL))
            {
                var factor = State == EEntityState.IDLE ? 0.05 : 0.01;
                Health = Math.Min((int)maxHp, Health + 15 + (int)(maxHp * factor));
                hpOrSpChanged = true;

                _lastHealthRegenTime = ctx.Timestamp;
            }
        }

        var maxSp = GetPoint(EPoint.MAX_SP);
        if (Mana < maxSp && !Dead)
        {
            if (!_lastManaRegenTime.HasValue)
            {
                // start counting interval only from first viable reset
                _lastManaRegenTime = ctx.Timestamp;
            }
            else if (ctx.ElapsedSince(_lastManaRegenTime.Value) > TimeSpan.FromMilliseconds(MANA_REGEN_INTERVAL))
            {
                var factor = State == EEntityState.IDLE ? 0.05 : 0.01;
                Mana = Math.Min((int)maxSp, Mana + 15 + (int)(maxSp * factor));
                hpOrSpChanged = true;

                _lastManaRegenTime = ctx.Timestamp;
            }
        }

        if (hpOrSpChanged)
        {
            this.SendPoints();
        }

        // Notices any active skill buff expiring - nothing else prompts this on its own (ApplySkillBuff
        // only reacts when a buff is newly applied), so without this a buff (and its buff-icon/weapon-aura
        // visuals) would appear to last forever once cast. See ExpireSkillBuffs' own comment.
        if (!_lastSpeedBuffCheckTime.HasValue ||
            ctx.ElapsedSince(_lastSpeedBuffCheckTime.Value) > TimeSpan.FromMilliseconds(SPEED_BUFF_CHECK_INTERVAL))
        {
            ExpireSkillBuffs();
            _lastSpeedBuffCheckTime = ctx.Timestamp;
        }

        // Checked every tick (not throttled like the buff-expiry check above) so a Dash-style "primed
        // strike" (see PlayerSkills.TryTriggerPendingSplash's own comment) lands the instant a target comes
        // within range, not up to a second late - cheap no-op in the overwhelmingly common case of nothing
        // pending.
        Skills.TryTriggerPendingSplash();
    }

    public override EBattleType GetBattleType()
    {
        return EBattleType.MELEE;
    }

    public override int GetMinDamage()
    {
        var weapon = Inventory.EquipmentWindow.Weapon;
        if (weapon is null) return 0;
        var item = _itemManager.GetItem(weapon.ItemId);
        if (item is null) return 0;
        return item.Values[3];
    }

    public override int GetMaxDamage()
    {
        var weapon = Inventory.EquipmentWindow.Weapon;
        if (weapon is null) return 0;
        var item = _itemManager.GetItem(weapon.ItemId);
        if (item is null) return 0;
        return item.Values[4];
    }

    public override int GetBonusDamage()
    {
        var weapon = Inventory.EquipmentWindow.Weapon;
        if (weapon is null) return 0;
        var item = _itemManager.GetItem(weapon.ItemId);
        if (item is null) return 0;
        return item.Values[5];
    }

    public override void AddPoint(EPoint point, int value)
    {
        if (value == 0)
        {
            return;
        }

        switch (point)
        {
            case EPoint.LEVEL:
                LevelUp(value);
                break;
            case EPoint.EXPERIENCE:
                if (_experienceManager.GetNeededExperience((byte)GetPoint(EPoint.LEVEL)) == 0)
                {
                    // we cannot add experience if no level up is possible
                    return;
                }

                var before = Player.Experience;
                if (value < 0 && Player.Experience <= -value)
                {
                    Player.Experience = 0;
                }
                else
                {
                    Player.Experience = (uint)(Player.Experience + value);
                }

                if (value > 0)
                {
                    var partialLevelUps = CalcPartialLevelUps(before, GetPoint(EPoint.EXPERIENCE),
                        GetPoint(EPoint.NEEDED_EXPERIENCE));
                    if (partialLevelUps > 0)
                    {
                        Health = Player.MaxHp;
                        Mana = Player.MaxSp;
                        for (var i = 0; i < partialLevelUps; i++)
                        {
                            RecalculateStatusPoints();
                        }
                    }

                    CheckLevelUp();
                }

                break;
            case EPoint.GOLD:
                var gold = Player.Gold + value;
                Player.Gold = (uint)Math.Min(uint.MaxValue, Math.Max(0, gold));
                break;
            case EPoint.ST:
                Player.St += (byte)value;
                break;
            case EPoint.DX:
                Player.Dx += (byte)value;
                break;
            case EPoint.HT:
                // MaxHp and CalculateDefence() both read Player.Ht/GetPoint(EPoint.HT) (see GetMaxHp and
                // CalculateDefence's own bodies), but neither is recomputed anywhere except LoadAsync() -
                // confirmed live: spending a stat point on Vitality via /stat HT changed the raw stat but
                // left displayed Max HP/Defence completely unchanged until the next relog. Recompute both
                // right here so a spent point takes effect immediately, matching LevelUp() below (which had
                // the exact same gap - Level feeds both formulas too).
                Player.Ht += (byte)value;
                Player.MaxHp = GetMaxHp(_jobManager, Player.PlayerClass, Player.Level, Player.Ht);
                CalculateDefence();
                break;
            case EPoint.IQ:
                // Same gap as HT above, for MaxSp (GetMaxSp reads Player.Iq).
                Player.Iq += (byte)value;
                Player.MaxSp = GetMaxSp(_jobManager, Player.PlayerClass, Player.Level, Player.Iq);
                break;
            case EPoint.HP:
                if (value <= 0)
                {
                    // 0 gets ignored by client
                    // Setting the Hp to 0 does not register as killing the player
                }
                else if (value > GetPoint(EPoint.MAX_HP))
                {
                    Health = GetPoint(EPoint.MAX_HP);
                }
                else
                {
                    Health = value;
                }

                break;
            case EPoint.SP:
                if (value <= 0)
                {
                    // 0 gets ignored by client
                }
                else if (value > GetPoint(EPoint.MAX_SP))
                {
                    Mana = GetPoint(EPoint.MAX_SP);
                }
                else
                {
                    Mana = value;
                }

                break;
            case EPoint.STATUS_POINTS:
                Player.AvailableStatusPoints += (uint)value;
                break;
            case EPoint.SKILL:
                Player.AvailableSkillPoints += (uint)value;
                break;
            case EPoint.PLAY_TIME:
                Player.PlayTime += (uint)value;
                break;
            default:
                _logger.LogError("Failed to add point to {Point}, unsupported", point);
                break;
        }
    }

    internal static int CalcPartialLevelUps(uint before, uint after, uint requiredForNextLevel)
    {
        if (after >= requiredForNextLevel) return 0;

        const int CHUNK_AMOUNT = 4;
        var chunk = requiredForNextLevel / CHUNK_AMOUNT;
        var beforeChunk = (int)(before / (float)chunk);
        var afterChunk = (int)(after / (float)chunk);

        return afterChunk - beforeChunk;
    }

    public override void SetPoint(EPoint point, uint value)
    {
        switch (point)
        {
            case EPoint.LEVEL:
                var currentLevel = GetPoint(EPoint.LEVEL);
                LevelUp((int)(value - currentLevel));
                break;
            case EPoint.EXPERIENCE:
                Player.Experience = value;
                CheckLevelUp();
                break;
            case EPoint.GOLD:
                Player.Gold = value;
                break;
            case EPoint.PLAY_TIME:
                Player.PlayTime = value;
                break;
            case EPoint.SKILL:
                Player.AvailableSkillPoints = (byte)value;
                break;
            default:
                _logger.LogError("Failed to set point to {Point}, unsupported", point);
                break;
        }
    }

    private void Inventory_OnSlotChanged(object? sender, SlotChangedEventArgs args)
    {
        switch (args.Slot)
        {
            case EquipmentSlot.WEAPON:
                if (args.ItemInstance is not null)
                {
                    var item = _itemManager.GetItem(args.ItemInstance.ItemId);
                    Player.MinWeaponDamage = item?.MinWeaponDamage ?? 0;
                    Player.MaxWeaponDamage = item?.MaxWeaponDamage ?? 0;
                }
                else
                {
                    Player.MinWeaponDamage = 0;
                    Player.MaxWeaponDamage = 0;
                }

                break;
            case EquipmentSlot.BODY:
                if (args.ItemInstance is not null)
                {
                    Player.BodyPart = args.ItemInstance.ItemId;
                }
                else
                {
                    Player.BodyPart = 0;
                }

                break;
            case EquipmentSlot.HAIR:
                if (args.ItemInstance is not null)
                {
                    Player.HairPart = args.ItemInstance.GetHairPartOffsetForClient(Player.PlayerClass.GetClass());
                }
                else
                {
                    Player.HairPart = 0;
                }

                break;
        }
    }

    public override uint GetPoint(EPoint point)
    {
        switch (point)
        {
            case EPoint.LEVEL:
                return Player.Level;
            case EPoint.EXPERIENCE:
                return Player.Experience;
            case EPoint.NEEDED_EXPERIENCE:
                return _experienceManager.GetNeededExperience(Player.Level);
            case EPoint.HP:
                return (uint)Health;
            case EPoint.SP:
                return (uint)Mana;
            case EPoint.MAX_HP:
                return Player.MaxHp;
            case EPoint.MAX_SP:
                return Player.MaxSp;
            // While mounted, ST/DX/HT/IQ become at least the horse's own stat at the current level - a
            // floor, not an additive bonus, matching CHARACTER::ComputePoints() in the original server
            // (see HorseStats.cs's doc comment). Below that floor the horse contributes nothing, so a
            // high-stat character riding a low-tier horse sees no change.
            case EPoint.ST:
                return Math.Max(Player.St, MountVnum != 0 ? HorseStats.St(Player.HorseLevel) : (byte)0);
            case EPoint.HT:
                return Math.Max(Player.Ht, MountVnum != 0 ? HorseStats.Ht(Player.HorseLevel) : (byte)0);
            case EPoint.DX:
                return Math.Max(Player.Dx, MountVnum != 0 ? HorseStats.Dx(Player.HorseLevel) : (byte)0);
            case EPoint.IQ:
                return Math.Max(Player.Iq, MountVnum != 0 ? HorseStats.Iq(Player.HorseLevel) : (byte)0);
            // ATTACK_SPEED/MOVE_SPEED already have any active skill buff baked in by CalculateAttackSpeed()/
            // CalculateMovement() themselves (see those methods' own comments for why - CharacterUpdate/
            // SpawnCharacter read the raw AttackSpeed/MovementSpeed fields directly, bypassing GetPoint, so
            // the buff has to live in the field itself here rather than being added on top of it, or a
            // client-visible speed buff wouldn't actually be visible).
            case EPoint.ATTACK_SPEED:
                return AttackSpeed;
            case EPoint.MOVE_SPEED:
                return MovementSpeed;
            // ATTACK_GRADE has no such dedicated packet field, so it's the simpler case: just add any
            // active buff on top of the base value from GetBaseAttackGrade().
            case EPoint.ATTACK_GRADE:
                return (uint)Math.Max(0, GetBaseAttackGrade() + GetSkillBuff(EPoint.ATTACK_GRADE));
            // The real client (CPythonPlayer::__RunCoolTime, PythonPlayerSkill.cpp) scales every skill's
            // displayed/enforced cooldown by a client-local multiplier derived from this exact point:
            // iSpd = 100 - CASTING_SPEED; if(iSpd>0) iSpd=100+iSpd; ... ; cooldown *= iSpd/100. A baseline
            // of 100 (this repo's convention for a neutral 100%-scale point, matching ATTACK_SPEED/
            // MOVE_SPEED) makes iSpd==0 -> "else" branch -> iSpd=100 -> multiplier 1.0 (no penalty). Before
            // this case existed, GetPoint fell through to the 0 default, so the client always computed
            // iSpd=100-0=100 -> 100+100=200 -> every skill's real cooldown was silently DOUBLED client-side,
            // regardless of what the server's own skilltable.txt/cooldown enforcement said - this is why
            // fixing the skilltable data alone (see rapport-bots.txt) never fully closed the gap to the
            // user's remembered real cooldowns.
            case EPoint.CASTING_SPEED:
                return (uint)Math.Max(0, 100 + GetSkillBuff(EPoint.CASTING_SPEED));
            case EPoint.GOLD:
                return Player.Gold;
            case EPoint.MIN_WEAPON_DAMAGE:
                return Player.MinWeaponDamage;
            case EPoint.MAX_WEAPON_DAMAGE:
                return Player.MaxWeaponDamage;
            // Player.MinAttackDamage/MaxAttackDamage were never set anywhere in this codebase (always 0) -
            // computed for real here instead (weapon range + the same ATTACK_GRADE/level terms
            // CalculateAttackPower's own formula uses), a genuine correctness improvement regardless of the
            // paragraph below - a real value beats a hardcoded 0 for anything else that ever reads these
            // (e.g. inspecting another player, a future gear-compare tooltip).
            //
            // Live-tested as a candidate for making an ATTACK_GRADE buff (e.g. Aura of the Sword) show up
            // in the status window, same as a DEFENCE_GRADE buff already visibly does - it did NOT help,
            // ruling this out same as the plain GetPoint(ATTACK_GRADE) case above already was. Between the
            // two failed attempts, the status window's displayed attack range is almost certainly computed
            // entirely client-side from data it already has locally (weapon + stats), never actually
            // reading either of these fields - unlike DEFENCE(_GRADE), which the client evidently does
            // read directly. This is very likely accurate to the real game too (attack grade/penetration
            // has traditionally been more of a background combat modifier than a displayed stat), not a
            // bug to keep chasing here.
            case EPoint.MIN_ATTACK_DAMAGE:
                return (uint)Math.Max(0, GetMinDamage() + GetPoint(EPoint.ATTACK_GRADE) + GetPoint(EPoint.LEVEL) * 2);
            case EPoint.MAX_ATTACK_DAMAGE:
                return (uint)Math.Max(0, GetMaxDamage() + GetPoint(EPoint.ATTACK_GRADE) + GetPoint(EPoint.LEVEL) * 2);
            case EPoint.DEFENCE:
            case EPoint.DEFENCE_GRADE:
                return (uint)Math.Max(0, _defence + GetSkillBuff(EPoint.DEFENCE_GRADE));
            // Was missing entirely (fell through to the generic `default: return 0`) - Entity.Damage()'s
            // `if (criticalPercentage > 0)` gate meant crits could never proc for ANY player regardless of
            // gear, even with an item-granted CRITICAL_PCT apply (e.g. "+15% Critical Hit"). That apply
            // value is real, fixed item-proto data (ItemExtensions.GetApplyValue), not a random per-instance
            // roll this repo lacks - same class of fix already done for MOV_SPEED/ATTACK_SPEED above.
            case EPoint.CRITICAL_PERCENTAGE:
                return (uint)Math.Max(0, GetEquipmentApplySum(EApplyType.CRITICAL_PCT));
            case EPoint.RESIST_CRITICAL:
                return (uint)Math.Max(0, GetEquipmentApplySum(EApplyType.ANTI_CRITICAL_PCT));
            // Same bug, same fix as CRITICAL_PERCENTAGE/RESIST_CRITICAL above - was falling through to the
            // generic `default: return 0`, so Entity.Damage()'s `if (penetratePercentage > 0)` gate meant
            // penetrate could never proc regardless of an item's PENETRATE_PCT apply. Confirmed live via the
            // repeated "Point PENETRATE_PERCENTAGE is not implemented on monster" log spam (that specific
            // warning is harmless/expected - it's MonsterEntity.GetPoint, not this one - but its constant
            // presence is what prompted checking this player-side case, which really was missing).
            case EPoint.PENETRATE_PERCENTAGE:
                return (uint)Math.Max(0, GetEquipmentApplySum(EApplyType.PENETRATE_PCT));
            case EPoint.RESIST_PENETRATE:
                return (uint)Math.Max(0, GetEquipmentApplySum(EApplyType.ANTI_PENETRATE_PCT));
            case EPoint.STATUS_POINTS:
                return Player.AvailableStatusPoints;
            case EPoint.PLAY_TIME:
                return (uint)TimeSpan.FromMilliseconds(Player.PlayTime).TotalMinutes;
            case EPoint.SKILL:
                return Player.AvailableSkillPoints;
            case EPoint.SUB_SKILL:
                return 1;
            default:
                return 0;
        }
    }

    /// <inheritdoc />
    public Task SaveAsync() => PersistAsync();

    private async Task PersistAsync()
    {
        await QuickSlotBar.PersistAsync();

        Player.PositionX = PositionX;
        Player.PositionY = PositionY;

        await Skills.PersistAsync();

        var playerManager = _scope.ServiceProvider.GetRequiredService<IPlayerManager>();
        await playerManager.SetPlayerAsync(Player);
    }

    // A closest-first-sorted "pending reveals" queue used to live here, added to spread out the burst of
    // ShowEntity calls from walking through a dense area (this repo's maps can have 1000+ monsters,
    // matching real official server density - not itself a bug). It caused a real, worse bug of its own:
    // sorting by CURRENT distance every flush meant a continuously-moving player kept having freshly
    // nearby (and so momentarily "closest") entities cut in front of older ones discovered earlier in the
    // walk - those older entries could be starved indefinitely as long as movement kept surfacing new,
    // closer candidates, only draining once the player actually stopped. Live-reported as "monsters spawn
    // in front of me, then I outrun them, then once I stop everything catches up from the direction I
    // came" - an exact match for FIFO-violating starvation, not a pacing/burst problem at all. Removed
    // entirely: the real fix for the client's rendering burst turned out to belong client-side (a
    // wall-clock-paced creation budget in NetworkActorManager.cpp's __OLD_Update(), confirmed live -
    // CInstanceBase::Create() itself only averages ~2ms, so no server-side throttling is needed once the
    // client paces its own instantiation correctly). Back to the simple, immediate, starvation-free
    // behaviour: reveal/hide exactly when Map.cs's nearby-scan says so, no queue in between.
    protected override void OnNewNearbyEntity(IEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // TEMP DIAGNOSTIC - "new mobs only appear closer and closer over time" investigation: added
        // dist= (true distance from the player at reveal time) to directly check whether new reveals
        // trend toward shorter distances over the course of a session, instead of guessing from vids.
        try
        {
            var dist = (int)Math.Sqrt(Math.Pow(entity.PositionX - PositionX, 2) +
                                       Math.Pow(entity.PositionY - PositionY, 2));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "qcx_reveal_timing.log"),
                $"[{DateTime.UtcNow:HH:mm:ss.fff}] player={Vid} pos=({PositionX},{PositionY}) reveal vid={entity.Vid} type={entity.Type} dist={dist}\n");
        }
        catch
        {
            // best effort
        }

        entity.ShowEntity(Connection);
    }

    protected override void OnRemoveNearbyEntity(IEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // TEMP DIAGNOSTIC - matches the reveal-timing log, but for the remove/hide side, which was
        // never actually measured before - "mobs from way behind me still visible" could be either the
        // server never deciding to remove them, or deciding to but the client not acting on it.
        try
        {
            var dist = (int)Math.Sqrt(Math.Pow(entity.PositionX - PositionX, 2) +
                                       Math.Pow(entity.PositionY - PositionY, 2));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "qcx_reveal_timing.log"),
                $"[{DateTime.UtcNow:HH:mm:ss.fff}] player={Vid} pos=({PositionX},{PositionY}) HIDE vid={entity.Vid} type={entity.Type} dist={dist}\n");
        }
        catch
        {
            // best effort
        }

        entity.HideEntity(Connection);
    }

    public async Task DropItemAsync(ItemInstance item, byte count)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (count > item.Count)
        {
            return;
        }

        if (item.Count == count)
        {
            RemoveItem(item);
            this.SendRemoveItem(item.Window, (ushort)item.Position);
            await _itemRepository.DeletePlayerItemAsync(_cacheManager, item.PlayerId, item.ItemId);
        }
        else
        {
            item.Count -= count;
            await item.PersistAsync(_itemRepository);

            this.SendItem(item);

            var proto = _itemManager.GetItem(item.ItemId);
            if (proto is null)
            {
                _logger.LogCritical("Failed to find proto {ProtoId} for instanced item {ItemId}",
                    item.ItemId, item.Id);
                return;
            }

            item = _itemManager.CreateItem(proto, count);
        }

        (Map as Map)?.AddGroundItem(item, PositionX, PositionY);
    }

    public async Task PickupAsync(IGroundItem groundItem)
    {
        ArgumentNullException.ThrowIfNull(groundItem);
        if (Map is null) return;

        var item = groundItem.Item;
        if (item.ItemId == 1)
        {
            AddPoint(EPoint.GOLD, (int)groundItem.Amount);
            this.SendPoints();
            Map.DespawnEntity(groundItem);

            return;
        }

        if (groundItem.OwnerName is not null && !string.Equals(groundItem.OwnerName, Name))
        {
            this.SendChatInfo("This item is not yours");
            return;
        }

        if (!await Inventory.PlaceItemAsync(item)) // TODO
        {
            this.SendChatInfo("No inventory space left");
            return;
        }

        var itemName = _itemManager.GetItem(item.ItemId)?.TranslatedName ?? "Unknown";
        this.SendChatInfo($"You picked up {groundItem.Amount}x {itemName}");

        this.SendItem(item);
        Map.DespawnEntity(groundItem);
    }

    public void DropGold(uint amount)
    {
        var proto = _itemManager.GetItem(1);

        if (proto is null)
        {
            _logger.LogCritical("Cannot find proto for gold. This must never happen");
            return;
        }

        // todo prevent crashing the server with dropping gold too often ;)

        if (amount > GetPoint(EPoint.GOLD))
        {
            return; // We can't drop more gold than we have ^^
        }

        AddPoint(EPoint.GOLD, -(int)amount);
        this.SendPoints();

        var item = _itemManager.CreateItem(proto); // count will be overwritten as it's gold
        (Map as Map)?.AddGroundItem(item, PositionX, PositionY,
            amount); // todo add method to IMap interface when we have an item interface...
    }

    /// <summary>
    /// Does nothing - if you want to persist the player use <see cref="OnDespawnAsync"/>
    /// </summary>
    public override void OnDespawn()
    {
    }

    public virtual async Task OnDespawnAsync()
    {
        // Quest.Init() registered this Vid into GameEventManager's static dictionaries on login - drop
        // those now so they don't linger as stale closures once this player/connection goes away. See
        // GameEventManager.UnregisterPlayer's doc comment for why this matters.
        GameEventManager.UnregisterPlayer(Vid);
        await PersistAsync();
    }

    public int GetMobItemRate()
    {
        // todo: implement configurable server-wide item drop rates, and premium server rates
        // This used to return 100_000_000 (a million times the intended neutral 100) for every player
        // without an active premium item bonus - i.e. always, since GetPremiumRemainSeconds is itself
        // a stub that always returns 0. DropProvider.CalculateDropPercentages multiplies deltaPercentage
        // by this value / 100, so every single drop roll across every category (common/group/kill/limit/
        // etc/metin) was inflated a millionfold - confirmed live via debug logging showing individual drop
        // chances of several hundred to tens of thousands of percent (guaranteed drops) instead of the
        // intended sub-1% values. 100 is the neutral "normal rate" baseline until real configurable
        // server/premium rates are implemented.
        return 100;
    }

    public int GetPremiumRemainSeconds(EPremiumType type)
    {
        _logger.LogTrace("GetPremiumRemainSeconds not implemented yet");
        return 0; // todo: implement premium system
    }

    public bool IsUsableSkillMotion(ESkill motion)
    {
        // todo: check if riding, mining or fishing
        return true;
    }

    public bool HasUniqueGroupItemEquipped(uint itemProtoId)
    {
        _logger.LogTrace("HasUniqueGroupItemEquipped not implemented yet");
        return false; // todo: implement unique group item system
    }

    public bool HasUniqueItemEquipped(uint itemProtoId)
    {
        {
            var item = Inventory.EquipmentWindow.GetItem(EquipmentSlot.UNIQUE1);
            if (item is not null && item.ItemId == itemProtoId)
            {
                return true;
            }
        }
        {
            var item = Inventory.EquipmentWindow.GetItem(EquipmentSlot.UNIQUE2);
            if (item is not null && item.ItemId == itemProtoId)
            {
                return true;
            }
        }

        return false;
    }

    public async Task CalculatePlayedTimeAsync()
    {
        var key = $"player:{Player.Id}:loggedInTime";
        var startSessionElapsed = TimeSpan.FromMilliseconds(
            await _cacheManager.Server.GetAsync<long>(key)
        );
        var currentElapsed = Connection.Server.Clock.Elapsed;
        var totalSessionTime = currentElapsed - startSessionElapsed;
        if (totalSessionTime <= TimeSpan.Zero) return;

        AddPoint(EPoint.PLAY_TIME, (int)totalSessionTime.TotalMilliseconds);
    }

    public ItemInstance? GetItem(WindowType window, ushort position)
    {
        switch (window)
        {
            case WindowType.INVENTORY:
                if (position >= Inventory.Size)
                {
                    // Equipment
                    return Inventory.EquipmentWindow.GetItem(position);
                }
                else
                {
                    // Inventory
                    return Inventory.GetItem(position);
                }
        }

        return null;
    }

    public bool IsSpaceAvailable(ItemInstance item, WindowType window, ushort position)
    {
        switch (window)
        {
            case WindowType.INVENTORY:
                if (position >= Inventory.Size)
                {
                    // Equipment
                    // Make sure item fits in equipment window
                    if (IsEquippable(item) && Inventory.EquipmentWindow.IsSuitable(_itemManager, item, position))
                    {
                        return Inventory.EquipmentWindow.GetItem(position) is null;
                    }
                }

                // Inventory
                return Inventory.IsSpaceAvailable(item, position);
        }

        return false;
    }

    public bool IsEquippable(ItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var proto = _itemManager.GetItem(item.ItemId);
        if (proto is null)
        {
            // Proto for item not found
            return false;
        }

        if (proto.WearFlags == 0 && !proto.IsType(EItemType.COSTUME))
        {
            // No wear flags -> not wearable
            return false;
        }

        // Check anti flags
        var antiFlags = (EAntiFlags)proto.AntiFlags;
        if (antiFlags.HasFlag(AntiFlagClass))
        {
            return false;
        }

        if (antiFlags.HasFlag(AntiFlagGender))
        {
            return false;
        }

        // Check limits (level)
        foreach (var limit in proto.Limits)
        {
            if (limit.Type == (byte)ELimitType.LEVEL)
            {
                if (Player.Level < limit.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public async Task<bool> DestroyItemAsync(ItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RemoveItem(item);
        if (!await item.DestroyAsync(_cacheManager))
        {
            return false;
        }

        this.SendRemoveItem(item.Window, (ushort)item.Position);
        return true;
    }

    public void RemoveItem(ItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Window)
        {
            case WindowType.INVENTORY:
                if (item.Position >= Inventory.Size)
                {
                    // Equipment
                    Inventory.RemoveEquipment(item);
                    CalculateDefence();
                    CalculateMovement();
                    CalculateAttackSpeed();
                    this.SendCharacterUpdate();
                    this.SendPoints();
                }
                else
                {
                    // Inventory
                    Inventory.RemoveItem(item);
                }

                break;
        }
    }

    /// <summary>
    /// Equips an item directly into its wear slot, computed automatically from the item's proto via
    /// <see cref="IItemManager.GetWearSlot"/>. Unlike <see cref="SetItemAsync"/> this never touches the
    /// cache or the database - intended for entities that aren't backed by a database row (see
    /// <see cref="QuantumCore.Game.Bots.BotPlayerEntity"/>). Does nothing if the item isn't wearable, its
    /// proto can't be resolved, or the slot is already occupied.
    /// </summary>
    protected void EquipWithoutPersisting(ItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var wearSlot = _itemManager.GetWearSlot(item.ItemId);
        if (wearSlot is null) return;
        if (Inventory.EquipmentWindow.GetItem(wearSlot.Value) is not null) return;

        var position = (ushort)Inventory.EquipmentWindow.GetWearPosition(_itemManager, item.ItemId);
        Inventory.SetEquipment(item, position);

        CalculateDefence();
        CalculateMovement();
        CalculateAttackSpeed();
    }

    /// <summary>
    /// Mounts the given mob proto (e.g. 20030 = "Horse", verified against this repo's actual
    /// src/Executables/Single/data/mob_proto and matching the original game server's default horse vnum
    /// in char_horse.cpp/CHARACTER::GetMyHorseVnum). Purely a client-rendering signal - the client swaps
    /// this character's motion set for the mount's own once MountVnum is broadcast (see
    /// PlayerExtensions.SendCharacterAdditional/SendCharacterUpdate); nothing server-side needs the mount
    /// to be a real spawned entity for this to work.
    /// </summary>
    public void Mount(uint vnum)
    {
        if (MountVnum == vnum) return;
        MountVnum = vnum;

        // Re-floor ST/DX/HT/IQ (via GetPoint) and defence against the horse's own stats now that
        // MountVnum changed - see HorseStats.cs's doc comment for why this is a floor, not a bonus.
        CalculateDefence();
        this.SendPoints();

        // NOT SendCharacterUpdate(): confirmed against the original CHARACTER::MountVnum() (char.cpp),
        // a plain CharacterUpdate broadcast is not enough to make an ALREADY-VISIBLE client rebuild the
        // character's model/motion set for the new mount. The original re-sends the full "insert"
        // packet sequence (EncodeInsertPacket) to self and every nearby observer instead - that is
        // exactly what ShowEntity does here (SpawnCharacter + CharacterInfo, both carrying MountVnum).
        // This is also why a bot mounted before ever being shown "just worked": its first-ever
        // ShowEntity already carried the mount, no live transition was needed.
        ShowEntity(Connection);
        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity player)
            {
                ShowEntity(player.Connection);
            }
        }
    }

    public void Unmount()
    {
        Mount(0);
    }

    public async Task SetItemAsync(ItemInstance item, WindowType window, ushort position)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (window)
        {
            case WindowType.INVENTORY:
                if (position >= Inventory.Size)
                {
                    // Equipment
                    if (Inventory.EquipmentWindow.GetItem(position) is null)
                    {
                        Inventory.SetEquipment(item, position);
                        await item.SetAsync(_cacheManager, Player.Id, window, position, _itemRepository);
                        CalculateDefence();
                        CalculateMovement();
                        CalculateAttackSpeed();
                        this.SendCharacterUpdate();
                        this.SendPoints();
                    }
                }
                else
                {
                    // Inventory
                    await Inventory.PlaceItemAsync(item, position);
                }

                break;
        }
    }

    public override void ShowEntity(IConnection connection)
    {
        SendGuildInfo();
        this.SendCharacter(connection);
        this.SendCharacterAdditional(connection);
    }

    public override void HideEntity(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Send(new RemoveCharacter { Vid = Vid });
        SendOfflineNotice(connection);
    }

    private void SendOfflineNotice(IConnection connection)
    {
        var guildId = Player.GuildId;
        if (guildId is not null && connection is IGameConnection gameConnection &&
            gameConnection.Player!.Player.GuildId == guildId)
        {
            connection.Send(new GuildMemberOfflinePacket { PlayerId = Player.Id });
        }
    }

    public void Disconnect()
    {
        Inventory.OnSlotChanged -= Inventory_OnSlotChanged;
        Connection.Close();
    }

    public override string ToString()
    {
        return Player.Name + "(Player)";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _scope.Dispose();
        }

        _isDisposed = true;
    }
}