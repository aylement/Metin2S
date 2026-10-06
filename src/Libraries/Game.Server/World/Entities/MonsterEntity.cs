using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Game.Types.Combat;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.World;
using QuantumCore.API.Game.World.AI;
using QuantumCore.API.Packets;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Services;
using QuantumCore.Game.World.AI;

namespace QuantumCore.Game.World.Entities;

public class MonsterEntity : Entity
{
    private readonly IDropProvider _dropProvider;
    private readonly ILogger _logger;
    public override EEntityType Type => EEntityType.MONSTER;
    public bool IsStone => Proto.Type == EEntityType.METIN_STONE;
    public EMonsterLevel Rank => (EMonsterLevel)Proto.Rank;

    public override IEntity? Target
    {
        get { return (_behaviour as SimpleBehaviour)?.Target; }
        set
        {
            if (_behaviour is SimpleBehaviour sb)
            {
                sb.Target = value;
            }
        }
    }

    public IBehaviour? Behaviour
    {
        get { return _behaviour; }
        set
        {
            _behaviour = value;
            _behaviourInitialized = false;
        }
    }

    public override byte HealthPercentage
    {
        get { return (byte)(Math.Min(Math.Max(Health / (double)Proto.Hp, 0), 1) * 100); }
    }

    public MonsterData Proto { get; private set; }

    public MonsterGroup? Group { get; set; }

    private IBehaviour? _behaviour;
    private bool _behaviourInitialized;
    private ServerTimestamp? _diedAt;
    private readonly IMap _map;

    // Kept only so HandleDevilTowerFloorTrigger can spawn wave 2 of floor 5 in code (there's no
    // regen.txt-driven way to spawn on a "kill trigger", the same reason WarpAllPlayersOnMap etc. live
    // inline in this class already) - same construction dependencies SpawnCommand uses for `/spawn`.
    private readonly IMonsterManager _monsterManager;
    private readonly IAnimationManager _animationManager;
    private readonly IServiceProvider _serviceProvider;

    public MonsterEntity(IMonsterManager monsterManager, IDropProvider dropProvider,
        IAnimationManager animationManager,
        IServiceProvider serviceProvider,
        IMap map, ILogger logger, uint id, int x, int y, float rotation = 0)
#pragma warning disable CA1062 // validate parameters - impossible here
        : base(animationManager, map.World.GenerateVid())
#pragma warning restore CA1062
    {
        ArgumentNullException.ThrowIfNull(monsterManager);
        Proto = monsterManager.GetMonster(id)
                ?? throw new InvalidOperationException(
                    $"Could not find mob proto for ID {id}. Cannot create mob entity");
        _map = map;
        _dropProvider = dropProvider;
        _logger = logger;
        _monsterManager = monsterManager;
        _animationManager = animationManager;
        _serviceProvider = serviceProvider;
        PositionX = x;
        PositionY = y;
        Rotation = rotation;

        MovementSpeed = (byte)Proto.MoveSpeed;

        Health = Proto.Hp;
        EntityClass = id;

        if (Proto.Type == (byte)EEntityType.MONSTER)
        {
            // it's a monster
            _behaviour = new SimpleBehaviour(monsterManager);
        }
        else if (Proto.Type == EEntityType.NPC)
        {
            // npc
        }
        else if (Proto.Type == EEntityType.METIN_STONE)
        {
            _behaviour = ActivatorUtilities.CreateInstance<StoneBehaviour>(serviceProvider);
        }
    }

    public override void Update(TickContext ctx)
    {
        if (Map is null) return;
        if (Dead)
        {
            if (!_diedAt.HasValue)
            {
                _diedAt = ctx.Timestamp;
            }
            else if (ctx.ElapsedSince(_diedAt.Value) >= TimeSpan.FromSeconds(5))
            {
                Map.DespawnEntity(this);
            }
        }

        if (!_behaviourInitialized)
        {
            _behaviour?.Init(this);
            _behaviourInitialized = true;
        }

        if (!Dead)
        {
            _behaviour?.Update(ctx);
        }

        base.Update(ctx);
    }

    public override void Goto(int x, int y, ServerTimestamp startAt)
    {
        Rotation = (float)MathUtils.Rotation(x - PositionX, y - PositionY);

        base.Goto(x, y, startAt);
        // Send movement to nearby players
        var startTime = (Map as Map)!.Clock.ElapsedAt(startAt);
        var movement = new CharacterMoveOut
        {
            Vid = Vid,
            Rotation = (byte)(Rotation / 5),
            Argument = (byte)CharacterMovementType.WAIT,
            PositionX = TargetPositionX,
            PositionY = TargetPositionY,
            Time = (uint)startTime.TotalMilliseconds,
            Duration = MovementDuration
        };

        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity player)
            {
                player.Connection.Send(movement);
            }
        }
    }

    public override EBattleType GetBattleType()
    {
        return Proto.BattleType;
    }

    public override int GetMinDamage()
    {
        return (int)Proto.DamageRange[0];
    }

    public override int GetMaxDamage()
    {
        return (int)Proto.DamageRange[1];
    }

    public override int GetBonusDamage()
    {
        return 0; // monster don't have bonus damage as players have from their weapon
    }

    public override int Damage(IEntity attacker, EDamageType damageType, int damage)
    {
        damage = base.Damage(attacker, damageType, damage);

        if (damage >= 0)
        {
            Behaviour?.TookDamage(attacker, (uint)damage);
            Group?.TriggerAll(attacker, this);
        }

        return damage;
    }

    public void Trigger(IEntity attacker)
    {
        Behaviour?.TookDamage(attacker, 0);
    }

    public override void AddPoint(EPoint point, int value)
    {
    }

    public override void SetPoint(EPoint point, uint value)
    {
    }

    public override uint GetPoint(EPoint point)
    {
        switch (point)
        {
            case EPoint.LEVEL:
                return Proto.Level;
            case EPoint.DX:
                return Proto.Dx;
            case EPoint.ATTACK_GRADE:
                return (uint)(Proto.Level * 2 + Proto.St * 2);
            case EPoint.DEFENCE_GRADE:
                return (uint)(Proto.Level + Proto.Ht + Proto.Defence);
            case EPoint.DEFENCE_BONUS:
                return 0;
            case EPoint.EXPERIENCE:
                return Proto.Experience;
            case EPoint.MAX_HP:
                return Proto.Hp;
            // Player-only bonus/resist stats (gear, skills) that monsters simply don't have - correctly 0,
            // not "unimplemented". These used to fall through to the LogWarning below, called on every
            // single combat calculation involving a monster (attacker or defender) - with several monsters
            // actively fighting, that's many warnings PER TICK, each a real (if individually small) log
            // I/O cost, and live-confirmed as the actual cause of severe tick delay (up to ~10s) once
            // there was enough simultaneous combat to make the flood add up - not a coincidence tied to any
            // particular content, just never having had enough concurrent monster combat before to notice.
            case EPoint.ATTACK_BONUS:
            case EPoint.MAGIC_ATTACK_BONUS:
            case EPoint.CRITICAL_PERCENTAGE:
            case EPoint.PENETRATE_PERCENTAGE:
            case EPoint.RESIST_CRITICAL:
            case EPoint.RESIST_PENETRATE:
                return 0;
        }

        _logger.LogWarning("Point {Point} is not implemented on monster", point);
        return 0;
    }

    public override void Die()
    {
        if (Dead)
        {
            return;
        }

        CalculateDrops();

        base.Die();

        var dead = new CharacterDead { Vid = Vid };
        foreach (var entity in NearbyEntities)
        {
            if (entity is PlayerEntity player)
            {
                player.Connection.Send(dead);
            }
        }

        HandleDevilTowerFloorTrigger();
    }

    // TEMP/narrow special case, not a general content-scripting hook (this server has no event-driven
    // quest engine like the real game's `when X.kill begin ... end` - see Quest.cs, it's dialogue-only).
    // Floor-progression triggers for the Devil Tower content being built incrementally. All floors live in
    // the SAME map file, distinguished only by a local Y-position band (see the real client's
    // __GetDevilTowerFloor() in uimapnameshower.py) - every transition below is a same-map WarpTo, not a
    // cross-map one. Expect more of these as more floors get built - if this grows past 2-3 floors, it
    // should become its own small service (tracking floor state properly) instead of living inline here.
    // internal (not private): SimpleBehaviour also needs this + FLOOR7_LOCAL_* below, for floor 7's
    // "always aggressive while a player is within view range" mechanic - see SimpleBehaviour.Init().
    internal const string DEVIL_TOWER_MAP_NAME = "metin2_map_deviltower1";
    private const uint DEVIL_TOWER_METIN_OF_TOUGHNESS_VNUM = 8010;
    private const uint DEVIL_TOWER_DEMON_KING_VNUM = 1091;
    // "Metin des Sturzes" (Metin of the Fall) in the real client's German locale, level 60 - confirmed
    // via the real mob_proto.txt, same way as the level-50 floor-1 stone.
    private const uint DEVIL_TOWER_FLOOR4_METIN_VNUM = 8012;
    // "Metin des Mordes" (Metin of Murder), level 70 - the real floor-7 target Metin per
    // docs/Demon Tower infos.txt (breaking it there normally grants the "Unknown Old Chest" ->
    // Elite Demon Tower Map item, which is what actually lets you proceed - no item-drag/right-click
    // interaction chain exists on this server, so breaking it warps directly instead, same
    // simplification as floor 5).
    private const uint DEVIL_TOWER_FLOOR7_METIN_VNUM = 8014;

    // Which of the 10 identical floor-4 Metins is secretly the real one, chosen the first time any of
    // them dies (see below). Static/shared, not per-dungeon-instance - this whole feature currently
    // assumes one single shared tower map, not per-group instances, same as everything else here. Reset
    // only by a server restart, so replaying the tower without restarting keeps the same "real" one -
    // an accepted limitation at this dev-testing fidelity level.
    private static uint? _floor4RealMetinVid;

    // Local (map-relative) bounds of floor 2's zone, per __GetDevilTowerFloor(): x 10000-25000,
    // y 35000-50000. Originally only Y was checked (there's no real wall between zones - no
    // server_attr collision data for this map yet - so this is an approximation that holds as long as
    // monsters don't wander far from where they spawned, SimpleBehaviour's wander range is only
    // 300-700/cycle) - X is now checked too, since floor 5's own Y band (38000-48000) turned out to sit
    // entirely INSIDE floor 2's Y band, and a Y-only check silently misrouted every floor-5 kill into
    // floor 2's "last one standing" logic instead (live-confirmed: floor 5 wave 1 clearing warped back
    // to floor 3's landing point instead of spawning wave 2).
    private const int FLOOR2_LOCAL_X_MIN = 10000;
    private const int FLOOR2_LOCAL_X_MAX = 25000;
    private const int FLOOR2_LOCAL_Y_MIN = 35000;
    private const int FLOOR2_LOCAL_Y_MAX = 50000;

    // Local zone bounds of floor 5, per __GetDevilTowerFloor(): x 35000-43500, y 38000-48000 - same X
    // band as floor 4 (only Y differs, no overlap). Y band sits INSIDE floor 2's Y band (see the comment
    // above) - X is what actually disambiguates the two, so both are checked for floor 5 too.
    private const int FLOOR5_LOCAL_X_MIN = 35000;
    private const int FLOOR5_LOCAL_X_MAX = 43500;
    private const int FLOOR5_LOCAL_Y_MIN = 38000;
    private const int FLOOR5_LOCAL_Y_MAX = 48000;

    // Local zone bounds of floor 7, per __GetDevilTowerFloor(): x 56000-68000, y 60000-73000 - a
    // genuinely different part of the map than floors 1-6 (x 10000-43500), no overlap possible.
    // HandleDevilTowerFloorTrigger() itself doesn't need these (floor 7's trigger is a plain
    // EntityClass check on the Level 70 Metin, not a "last one standing" zone check) - they're internal
    // (not private) purely so SimpleBehaviour.Init() can tag floor-7-spawned monsters for the
    // "always aggressive while a player is within view range" mechanic, user's explicit request for
    // this floor specifically.
    internal const int FLOOR7_LOCAL_X_MIN = 56000;
    internal const int FLOOR7_LOCAL_X_MAX = 68000;
    internal const int FLOOR7_LOCAL_Y_MIN = 60000;
    internal const int FLOOR7_LOCAL_Y_MAX = 73000;

    // Local zone bounds of floor 6. Real client zone (per __GetDevilTowerFloor()) is x 14000-43500,
    // y 14000-24500 - a much wider X span than floors 1-5. X_MIN is DELIBERATELY tightened to 30000
    // here (not the real 14000) - live-confirmed bug: floor 3's real zone (x 10000-25000, y 10000-25000)
    // sits entirely inside the real floor-6 zone, and floor 3's own Demons are left ALIVE after its boss
    // dies (only the boss gates that floor's progression) - the "any floor-6 monster still alive" scan
    // below was counting those leftover floor-3 Demons forever, so floor 6 could never appear cleared no
    // matter how thoroughly it was actually cleared. Our real floor-6 spawns (see regen.txt) all sit at
    // local x 35700-42700, floor 3's Demons at x 14000-21000 - 30000 cleanly separates the two with
    // margin on both sides, same X-based-disambiguation fix already applied for floors 2/5.
    private const int FLOOR6_LOCAL_X_MIN = 30000;
    private const int FLOOR6_LOCAL_X_MAX = 43500;
    private const int FLOOR6_LOCAL_Y_MIN = 14000;
    private const int FLOOR6_LOCAL_Y_MAX = 24500;

    // Real floor 5 (docs/Demon Tower infos.txt, floor 5): kill monsters for "Unlock Stones", drag 5 onto
    // pillars to break 5 Seals over ~20 minutes of waves. No pillar/drag-item interaction system exists
    // yet (see Known limitations in docs/docs/Guides/devil-tower-content.md) - simplified, at the user's
    // explicit request, to 2 "kill them all" waves (same finite-population idea as floor 2), a starting
    // point rather than the full seal grind. Wave 1 lives in regen.txt like every other floor; wave 2 has
    // no regen.txt trigger to hook (there's no "spawn on kill" primitive), so it's spawned here in code
    // once wave 1's last monster dies. Static for the same single-shared-tower reason as
    // _floor4RealMetinVid above - resets only on server restart.
    private static bool _floor5Wave2Spawned;

    private void HandleDevilTowerFloorTrigger()
    {
        if (Map is not { Name: DEVIL_TOWER_MAP_NAME } map) return;

        if (EntityClass == DEVIL_TOWER_METIN_OF_TOUGHNESS_VNUM)
        {
            // Floor 1 -> 2: breaking the Metin of Toughness.
            WarpAllPlayersOnMap(map, localX: 17500, localY: 42500);
            return;
        }

        if (EntityClass == DEVIL_TOWER_DEMON_KING_VNUM)
        {
            // Floor 3 -> 4: unlike floor 2, this floor advances on the BOSS's death specifically, not on
            // clearing every monster (user's explicit call - floor 3 keeps a few plain Demons around, but
            // they don't gate progression).
            WarpAllPlayersOnMap(map, localX: 39250, localY: 65750);
            return;
        }

        if (EntityClass == DEVIL_TOWER_FLOOR4_METIN_VNUM)
        {
            // Floor 4: 10 identical level-60 Metins in a ring, one random one advances to floor 5, the
            // other 9 behave like any ordinary Metin (normal drops, nothing special) - user's explicit
            // spec. The "real" one is picked lazily on the first floor-4 Metin death of the run, from
            // whichever of the 10 are still (map-)present at that moment - includes the one currently
            // dying, so killing the real one on the very first try correctly counts.
            // Snapshot map.Entities to a plain array first - the map's own tick loop can concurrently
            // Add/Remove from that same live list on another thread (it isn't synchronized with packet
            // handling), and live-confirmed crashing an in-progress LINQ query over it directly with
            // "Collection was modified" (same root shape as the WarpAllPlayersOnMap bug fixed earlier,
            // just a different call site - .ToArray() up front narrows the race window to one fast copy
            // instead of a whole query pipeline).
            _floor4RealMetinVid ??= map.Entities.ToArray()
                .OfType<MonsterEntity>()
                .Where(m => m.EntityClass == DEVIL_TOWER_FLOOR4_METIN_VNUM)
                .Select(m => (uint?)m.Vid)
                .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
                .FirstOrDefault();

            if (_floor4RealMetinVid == Vid)
            {
                // Floor 4 -> 5.
                WarpAllPlayersOnMap(map, localX: 39250, localY: 43000);
            }

            return;
        }

        if (EntityClass == DEVIL_TOWER_FLOOR7_METIN_VNUM)
        {
            // Floor 7 -> 8: breaking the single Level 70 Metin. Real design also requires 4 Level 65
            // Metins broken first, THEN this + a monster wave spawn - simplified, same as floor 5, to
            // "everyone present from the start, break this one to advance" at the user's explicit
            // request. Floor 8's landing point is a guess (center-ish of its real zone, x 56000-68000,
            // y 38000-49000 per __GetDevilTowerFloor()) - UNVERIFIED, same caveat as every hand-placed
            // position on these maps, see Known limitations.
            WarpAllPlayersOnMap(map, localX: 62000, localY: 43500);
            return;
        }

        if (!IsStone)
        {
            var localX = (int)PositionX - (int)map.Position.X;
            var localY = (int)PositionY - (int)map.Position.Y;

            if (localX >= FLOOR2_LOCAL_X_MIN && localX <= FLOOR2_LOCAL_X_MAX &&
                localY >= FLOOR2_LOCAL_Y_MIN && localY <= FLOOR2_LOCAL_Y_MAX)
            {
                // This death was a floor-2 monster - check if it was the last one standing. Snapshot
                // first - see the matching comment on the floor-4 check above for why.
                var anyFloor2MonsterAlive = map.Entities.ToArray().OfType<MonsterEntity>().Any(m =>
                {
                    if (m.IsStone || m.Dead) return false;
                    var x = (int)m.PositionX - (int)map.Position.X;
                    var y = (int)m.PositionY - (int)map.Position.Y;
                    return x >= FLOOR2_LOCAL_X_MIN && x <= FLOOR2_LOCAL_X_MAX &&
                           y >= FLOOR2_LOCAL_Y_MIN && y <= FLOOR2_LOCAL_Y_MAX;
                });

                if (!anyFloor2MonsterAlive)
                {
                    // Floor 2 -> 3: floor cleared.
                    WarpAllPlayersOnMap(map, localX: 17500, localY: 17500);
                }

                return;
            }

            if (localX >= FLOOR5_LOCAL_X_MIN && localX <= FLOOR5_LOCAL_X_MAX &&
                localY >= FLOOR5_LOCAL_Y_MIN && localY <= FLOOR5_LOCAL_Y_MAX)
            {
                var anyFloor5MonsterAlive = map.Entities.ToArray().OfType<MonsterEntity>().Any(m =>
                {
                    if (m.IsStone || m.Dead) return false;
                    var x = (int)m.PositionX - (int)map.Position.X;
                    var y = (int)m.PositionY - (int)map.Position.Y;
                    return x >= FLOOR5_LOCAL_X_MIN && x <= FLOOR5_LOCAL_X_MAX &&
                           y >= FLOOR5_LOCAL_Y_MIN && y <= FLOOR5_LOCAL_Y_MAX;
                });

                if (!anyFloor5MonsterAlive)
                {
                    if (!_floor5Wave2Spawned)
                    {
                        // Wave 1 cleared -> spawn wave 2 (tougher: more Vile Demons).
                        _floor5Wave2Spawned = true;
                        SpawnFloor5Wave2(map);
                    }
                    else
                    {
                        // Wave 2 cleared -> floor 5 -> 6. This landing point IS verified walkable (user
                        // tested via /goto): floor 6's real zone (x 14000-43500, y 14000-24500 per
                        // __GetDevilTowerFloor()) is oddly wide - its geometric center (x~28750) turned
                        // out to be an actual void, no floor there. Same X column as floors 4/5
                        // (35000-43500), just floor 6's own Y - confirmed solid ground.
                        WarpAllPlayersOnMap(map, localX: 39200, localY: 19200);
                    }
                }

                return;
            }

            if (localX >= FLOOR6_LOCAL_X_MIN && localX <= FLOOR6_LOCAL_X_MAX &&
                localY >= FLOOR6_LOCAL_Y_MIN && localY <= FLOOR6_LOCAL_Y_MAX)
            {
                var anyFloor6MonsterAlive = map.Entities.ToArray().OfType<MonsterEntity>().Any(m =>
                {
                    if (m.IsStone || m.Dead) return false;
                    var x = (int)m.PositionX - (int)map.Position.X;
                    var y = (int)m.PositionY - (int)map.Position.Y;
                    return x >= FLOOR6_LOCAL_X_MIN && x <= FLOOR6_LOCAL_X_MAX &&
                           y >= FLOOR6_LOCAL_Y_MIN && y <= FLOOR6_LOCAL_Y_MAX;
                });

                // TEMP diagnostic (not yet confirmed working live) - remove once floor 6's trigger has
                // been seen to actually fire once.
                _logger.LogInformation(
                    "Floor 6 death check: killed vnum {Vnum} at local ({X},{Y}), anyFloor6MonsterAlive={Alive}",
                    EntityClass, localX, localY, anyFloor6MonsterAlive);

                if (!anyFloor6MonsterAlive)
                {
                    // Floor 6 -> 7: unlike floor 3, the boss here (Proud Demon King, vnum 1092) does
                    // NOT gate progression on his own - user's explicit call for this floor was "kill
                    // everyone, including the king". Floor 7 lives in a DIFFERENT part of the map than
                    // floors 1-6 (x 56000-68000 vs 10000-43500 per __GetDevilTowerFloor()) - landing
                    // point is an UNVERIFIED guess (center-ish of floor 7's real zone, y 60000-73000).
                    WarpAllPlayersOnMap(map, localX: 62000, localY: 66500);
                }
            }
        }
    }

    // Floor 5 wave 2: spawned in code (no "spawn on kill" primitive exists for regen.txt - see the
    // comment on _floor5Wave2Spawned above), heavier on Vile Demons than wave 1 for an escalating feel.
    // Positions are a small ring around the floor 5 landing point (localX 39250, localY 43000).
    private void SpawnFloor5Wave2(IMap map)
    {
        var offsets = new (int dx, int dy, uint vnum)[]
        {
            (-1800, -1800, 1031), (1800, -1800, 1032), (1800, 1800, 1034), (-1800, 1800, 1037),
            (0, -2600, 1031), (0, 2600, 1032),
            (-2600, 0, 1002), (2600, 0, 1002),
            (-1300, 0, 1001), (1300, 0, 1004),
        };

        foreach (var (dx, dy, vnum) in offsets)
        {
            var x = (int)map.Position.X + 39250 + dx;
            var y = (int)map.Position.Y + 43000 + dy;
            var monster = new MonsterEntity(_monsterManager, _dropProvider, _animationManager,
                _serviceProvider, map, _logger, vnum, x, y);
            map.World.SpawnEntity(monster);
        }
    }

    private static void WarpAllPlayersOnMap(IMap map, int localX, int localY)
    {
        var targetX = (uint)((int)map.Position.X + localX);
        var targetY = (uint)((int)map.Position.Y + localY);

        // Snapshot map.Entities to a plain array FIRST, before any LINQ over it: WarpTo() below removes
        // the just-warped player from that same live list, AND the map's own tick loop can concurrently
        // Add/Remove on another thread regardless (confirmed live: "Collection was modified; enumeration
        // operation may not execute", crashing the packet handler that triggered this and cascading into
        // a connection-teardown race). .OfType<T>().ToList() alone still lazily enumerates the live
        // source across the whole call - .ToArray() up front is what actually stops that.
        var playersOnMap = map.Entities.ToArray().OfType<PlayerEntity>().ToList();
        foreach (var player in playersOnMap)
        {
            player.WarpTo(new Coordinates(targetX, targetY));
        }
    }

    private void CalculateDrops()
    {
        // no drops if no killer
        if (LastAttacker is null) return;

        var drops = new List<ItemInstance>();

        var (delta, range) = _dropProvider.CalculateDropPercentages(LastAttacker, this);

        // Common drops (common_drop_item.txt)
        drops.AddRange(_dropProvider.CalculateCommonDropItems(LastAttacker, this, delta, range));

        // Drop Item Group (mob_drop_item.txt)
        drops.AddRange(_dropProvider.CalculateDropItemGroupItems(this, delta, range));

        // Mob Drop Item Group (mob_drop_item.txt)
        drops.AddRange(_dropProvider.CalculateMobDropItemGroupItems(LastAttacker, this, delta, range));

        // Level drops (mob_drop_item.txt)
        drops.AddRange(_dropProvider.CalculateLevelDropItems(LastAttacker, this, delta, range));

        // Etc item drops (etc_drop_item.txt)
        drops.AddRange(_dropProvider.CalculateEtcDropItems(this, delta, range));

        if (IsStone)
        {
            // Spirit stone drops
            drops.AddRange(_dropProvider.CalculateMetinDropItems(this, delta, range));
        }

        // todo:
        // - horse riding skill drops
        // - quest item drops
        // - event item drops

        // Finally, drop the items
        foreach (var drop in drops)
        {
            // todo: if drop is yang, adjust the amount in function below instead of '1'
            _map.AddGroundItem(drop, PositionX, PositionY, 1, LastAttacker.Name);
        }
    }

    protected override void OnNewNearbyEntity(IEntity entity)
    {
        _behaviour?.OnNewNearbyEntity(entity);
    }

    protected override void OnRemoveNearbyEntity(IEntity entity)
    {
    }

    public override void OnDespawn()
    {
        if (Group is not null)
        {
            Group.Monsters.Remove(this);
            if (Group.Monsters.Count == 0)
            {
                (Map as Map)?.EnqueueGroupRespawn(Group);
            }
        }
    }

    public override void ShowEntity(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Dead)
        {
            return; // no need to send dead entities to new players
        }

        connection.Send(new SpawnCharacter
        {
            Vid = Vid,
            CharacterType = (EEntityType)Proto.Type,
            Angle = Rotation,
            PositionX = PositionX,
            PositionY = PositionY,
            Class = (ushort)Proto.Id,
            MoveSpeed = (byte)Proto.MoveSpeed,
            AttackSpeed = (byte)Proto.AttackSpeed
        });

        if (Proto.Type == EEntityType.NPC)
        {
            // NPCs need additional information too to show up for some reason
            connection.Send(new CharacterInfo
            {
                Vid = Vid, Empire = Proto.Empire, Level = 0, Name = Proto.TranslatedName
            });
        }
    }

    public override void HideEntity(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Send(new RemoveCharacter { Vid = Vid });
    }


    public override string ToString()
    {
        return $"{Proto.TranslatedName.Trim((char)0x00)} ({Proto.Id})";
    }
}