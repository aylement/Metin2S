using System.Collections.Concurrent;
using System.Security.Cryptography;
using EnumsNET;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.World;
using QuantumCore.Core.Event;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Services;
using QuantumCore.Game.World.Entities;

// using QuantumCore.Core.API;

namespace QuantumCore.Game.World;

public class Map : IMap
{
    public const uint MAP_UNIT = 25600;
    private const int SPAWN_BASE_OFFSET = 5;
    public const int SPAWN_POSITION_MULTIPLIER = 100;
    private const int SPAWN_ROTATION_SLICE_DEGREES = 45;
    public string Name { get; private set; }
    public Coordinates Position { get; private set; }
    public uint UnitX => Position.X / MAP_UNIT;
    public uint UnitY => Position.Y / MAP_UNIT;
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public TownCoordinates? TownCoordinates { get; private set; }
    public ServerClock Clock => _server.Clock;

    public IWorld World => _world;
    public IReadOnlyCollection<IEntity> Entities => _entities;

    private readonly List<IEntity> _entities = new();
    private readonly QuadTree _quadTree;
    private readonly List<SpawnPoint> _spawnPoints = new();

    private readonly List<IEntity> _nearby = new();
    private readonly List<IEntity> _remove = new();

    // Time-based nearby-rescan throttle (replaces an earlier sector-crossing-based version - see the
    // Update() loop below for the full reasoning/history). User spec, live-confirmed as the actual
    // desired behaviour: every mob within the exact VIEW_DISTANCE circle around the player should be
    // visible, refreshing at most every 1-2 real seconds as the player moves - both revealing what's
    // newly in range AND removing what's fallen out of range on the trailing side, symmetrically and
    // continuously (not a periodic "wave" tied to a coarse grid, which a sector crossing every ~6400
    // units could delay by up to ~1 minute across a wide dense zone at normal walking speed).
    private static readonly TimeSpan NEARBY_RESCAN_INTERVAL = TimeSpan.FromSeconds(1);
    private readonly Dictionary<IEntity, ServerTimestamp> _lastNearbyScanTime = new();

    private readonly ConcurrentQueue<IEntity> _pendingRemovals = new();
    private readonly ConcurrentQueue<IEntity> _pendingSpawns = new();
    private readonly IMonsterManager _monsterManager;
    private readonly IAnimationManager _animationManager;
    private readonly ICacheManager _cacheManager;
    private readonly IWorld _world;
    private readonly ILogger _logger;
    private readonly ISpawnPointProvider _spawnPointProvider;
    private readonly IMapAttributeProvider _attributeProvider;
    private readonly IDropProvider _dropProvider;
    private readonly IServerBase _server;
    private readonly IServiceProvider _serviceProvider;
    private IMapAttributeSet? _attributes;

    public Map(IMonsterManager monsterManager, IAnimationManager animationManager, ICacheManager cacheManager,
        IWorld world, ILogger logger, ISpawnPointProvider spawnPointProvider,
        IMapAttributeProvider attributeProvider, IDropProvider dropProvider,
        IServerBase server, string name, Coordinates position, uint width, uint height,
        TownCoordinates? townCoordinates, IServiceProvider serviceProvider)
    {
        _monsterManager = monsterManager;
        _animationManager = animationManager;
        _cacheManager = cacheManager;
        _world = world;
        _logger = logger;
        _spawnPointProvider = spawnPointProvider;
        _attributeProvider = attributeProvider;
        _dropProvider = dropProvider;
        _server = server;
        _serviceProvider = serviceProvider;
        Name = name;
        Position = position;
        Width = width;
        Height = height;

        TownCoordinates = townCoordinates is not null
            ? new TownCoordinates
            {
                Jinno = Position + townCoordinates.Jinno * SPAWN_POSITION_MULTIPLIER,
                Chunjo = Position + townCoordinates.Chunjo * SPAWN_POSITION_MULTIPLIER,
                Shinsoo = Position + townCoordinates.Shinsoo * SPAWN_POSITION_MULTIPLIER,
                Common = Position + townCoordinates.Common * SPAWN_POSITION_MULTIPLIER
            }
            : null;

        _quadTree = new QuadTree((int)position.X, (int)position.Y, (int)(width * MAP_UNIT),
            (int)(height * MAP_UNIT), 20);
        GameServer.Meter.CreateObservableGauge($"Map:{name}:EntityCount", () => Entities.Count);
    }

    public async Task InitializeAsync()
    {
        _logger.LogDebug("Load map {Name} at {Position} (size {Width}x{Height})", Name, Position, Width, Height);

        await _cacheManager.SetAsync($"maps:{Name}", $"{_server.IpAddress}:{_server.Port}");
        await _cacheManager.PublishAsync("maps", $"{Name} {_server.IpAddress}:{_server.Port}");

        var loadAttributesTask = _attributeProvider.GetAttributesAsync(Name, Position, Width, Height);
        var loadSpawnPointsTask = _spawnPointProvider.GetSpawnPointsForMapAsync(Name);

        await Task.WhenAll(loadAttributesTask, loadSpawnPointsTask);

        _attributes = await loadAttributesTask;
        _spawnPoints.AddRange(await loadSpawnPointsTask);

        _logger.LogDebug("Loaded {SpawnPointsCount} spawn points for map {MapName}", _spawnPoints.Count, Name);

        // Populate map
        foreach (var spawnPoint in _spawnPoints)
        {
            var monsterGroup = new MonsterGroup { SpawnPoint = spawnPoint };
            SpawnGroup(monsterGroup);
        }
    }

    public void Update(TickContext ctx)
    {
        // HookManager.Instance.CallHook<IHookMapUpdate>(this, ctx);

        while (_pendingSpawns.TryDequeue(out var entity))
        {
            if (!_quadTree.Insert(entity)) continue;

            // Add this entity to all entities nearby
            var nearby = new List<IEntity>();
            EEntityType? filter = null;
            if (entity.Type != EEntityType.PLAYER)
            {
                // if we aren't a player only players are relevant for nearby
                filter = EEntityType.PLAYER;
            }

            _quadTree.QueryAround(nearby, entity.PositionX, entity.PositionY, Entity.VIEW_DISTANCE, filter);
            foreach (var e in nearby)
            {
                if (e == entity) continue;
                entity.AddNearbyEntity(e);
                e.AddNearbyEntity(entity);
            }

            // Record that this initial scan already happened just now, so the regular per-tick loop
            // below doesn't immediately redo a rescan this same tick.
            _lastNearbyScanTime[entity] = ctx.Timestamp;

            _entities.Add(entity);
            entity.Map = this;
        }

        while (_pendingRemovals.TryDequeue(out var entity))
        {
            _entities.Remove(entity);

            entity.OnDespawn();

            // Remove this entity from all nearby entities first - this is what tells nearby real
            // players' clients to remove the character. Must happen even if disposing the entity
            // below throws, otherwise the entity leaks in the QuadTree and in NearbyEntities forever,
            // staying visible to everyone as an unresponsive "ghost" despite no longer being ticked.
            foreach (var e in entity.NearbyEntities)
            {
                e.RemoveNearbyEntity(entity);
            }

            // Remove map from the entity
            entity.Map = null;

            // Remove entity from the quad tree
            _quadTree.Remove(entity);

            // Drop its rescan-timing bookkeeping too, otherwise this dictionary leaks one entry per
            // monster death/respawn cycle forever.
            _lastNearbyScanTime.Remove(entity);

            if (entity is IDisposable dis)
            {
                try
                {
                    dis.Dispose();
                }
                catch (Exception e)
                {
                    _logger.LogError(e,
                        "Failed to dispose entity {Entity} during despawn - it was still removed from the world",
                        entity);
                }
            }
        }

        foreach (var entity in _entities)
        {
            entity.Update(ctx);

            var justMoved = entity.PositionChanged;
            if (justMoved)
            {
                entity.PositionChanged = false;

                // Update position in our quad tree (used for faster nearby look up)
                _quadTree.UpdatePosition(entity);
            }

            // Bug found live-testing this exact build: gating the ENTIRE rescan behind "did I just
            // move" meant a player who STOPPED moving never rescanned again - full stop. A monster that
            // wandered out of true view range near where the player started stayed visible until, by
            // chance, THAT monster's own movement-triggered rescan happened to notice the player was now
            // too far (i.e. only fixed opportunistically whenever some other entity happened to move) -
            // matching exactly what got reported: "eventually disappears, but can take 10+ seconds"
            // after stopping, with no reliable bound. A stationary player still needs their OWN nearby
            // list refreshed periodically, because the entities AROUND them keep moving even when they
            // don't. Non-player entities don't need this same treatment - if a stationary monster's set
            // of nearby players goes stale, the affected player's own periodic sweep (below) corrects it
            // from their side, so skipping monsters here is a real perf win, not a correctness gap.
            var isPlayer = entity.Type == EEntityType.PLAYER;
            if (!justMoved && !isPlayer)
            {
                continue;
            }

            // History of this rescan gate, in order:
                // 1) rescan on every real position change, unconditionally - correct (live-confirmed
                //    <100ms server-reveal to client-create latency, nothing genuinely missing), but user
                //    live-testing this build still called it "the same" as before.
                // 2) a naive "only rescan every N units moved" throttle (NEARBY_RESCAN_DISTANCE=500) -
                //    tried and REMOVED. Reduced rescan frequency without widening the query radius to
                //    match, so entities that entered true view range between two rescans genuinely
                //    weren't revealed until the next one - a real, measured 20-30 rescans/~25-35s delay.
                // 3) sector-crossing batching (SECTOR_SIZE=6400, matching the real game's sectree grid) -
                //    tried and REMOVED. User live-tested and clarified the actual desired spec: a plain,
                //    frequent (1-2s) refresh of the exact VIEW_DISTANCE circle around the live position -
                //    entities should appear/disappear continuously as the circle follows the player (e.g.
                //    mobs to the south leaving view as you walk north, mobs to the north entering it), not
                //    in occasional large "waves" gated on crossing a coarse fixed grid - a wide dense zone
                //    could still take close to a MINUTE to fully populate that way at ~15s/sector.
                // 4) time-based throttle (this version): rescan any single entity at most once per
                //    NEARBY_RESCAN_INTERVAL, but always with the exact VIEW_DISTANCE radius against its
                //    CURRENT live position - no margin needed, since each rescan is a fresh ground-truth
                //    query rather than an extrapolation from a stale reference point. This bounds the
                //    worst-case reveal/hide delay to exactly NEARBY_RESCAN_INTERVAL, both directions.
                // Recompute nearby entities whenever ANYTHING moves, not just players (unchanged from the
                // original fix - see git history/[[mob-visibility-delay-investigation]] for why).
                ServerTimestamp? lastScan =
                    _lastNearbyScanTime.TryGetValue(entity, out var t) ? t : null;
                if (ctx.ElapsedSince(lastScan) < NEARBY_RESCAN_INTERVAL)
                {
                    // Rescanned this entity recently enough already - nothing to do yet.
                    continue;
                }

                _lastNearbyScanTime[entity] = ctx.Timestamp;

                EEntityType? filter = null;
                if (entity.Type != EEntityType.PLAYER)
                {
                    // if we aren't a player only players are relevant for nearby
                    filter = EEntityType.PLAYER;
                }

                // Update entities nearby
                _quadTree.QueryAround(_nearby, entity.PositionX, entity.PositionY, Entity.VIEW_DISTANCE,
                    filter);

                // Check nearby entities and mark all entities which are too far away now
                foreach (var e in entity.NearbyEntities)
                {
                    // Remove this entity from our temporary list as they are already in it
                    if (!_nearby.Remove(e))
                    {
                        // If it wasn't in our temporary list it is no longer in view
                        _remove.Add(e);
                    }
                }

                // Remove previously marked entities on both sides
                foreach (var e in _remove)
                {
                    e.RemoveNearbyEntity(entity);
                    entity.RemoveNearbyEntity(e);
                }

                // Add new nearby entities on both sides
                foreach (var e in _nearby)
                {
                    if (e == entity)
                    {
                        continue; // do not add ourself!
                    }

                    e.AddNearbyEntity(entity);
                    entity.AddNearbyEntity(e);
                }

            // Clear our temporary lists
            _nearby.Clear();
            _remove.Clear();
        }
    }

    private void SpawnGroup(MonsterGroup groupInstance)
    {
        var spawnPoint = groupInstance.SpawnPoint;

        if (spawnPoint is null) return;

        switch (spawnPoint.Type)
        {
            case ESpawnPointType.GROUP_COLLECTION:
                var groupCollection = _world.GetGroupCollection(spawnPoint.Monster);
                if (groupCollection is not null)
                {
                    var index = RandomNumberGenerator.GetInt32(0, groupCollection.Groups.Length);
                    var collectionGroup = groupCollection.Groups[index];
                    var group = _world.GetGroup(collectionGroup.Id);
                    if (group is not null)
                    {
                        if (collectionGroup.Probability < 1)
                        {
                            var rand = RandomNumberGenerator.GetInt32(1, 100_000_001) / 100_000_000f;
                            if (rand > collectionGroup.Probability)
                            {
                                SpawnGroup(groupInstance, spawnPoint, group);
                            }
                        }
                        else
                        {
                            SpawnGroup(groupInstance, spawnPoint, group);
                        }
                    }
                }

                break;
            case ESpawnPointType.GROUP:
            {
                var group = _world.GetGroup(spawnPoint.Monster);
                if (group is not null)
                {
                    SpawnGroup(groupInstance, spawnPoint, group);
                }

                break;
            }
            case ESpawnPointType.MONSTER:
            {
                if (!TrySpawnMonster(spawnPoint.Monster, spawnPoint, out var monster))
                    break;

                spawnPoint.CurrentGroup = groupInstance;
                groupInstance.Monsters.Add(monster);
                monster.Group = groupInstance;

                break;
            }
            default:
                _logger.LogWarning("Unknown spawn point type: {SpawnPointType}", spawnPoint.Type);
                break;
        }
    }

    private void SpawnGroup(MonsterGroup groupInstance, SpawnPoint spawnPoint, SpawnGroup group)
    {
        spawnPoint.CurrentGroup = groupInstance;

        if (!TrySpawnMonster(group.Leader, spawnPoint, out var leader))
            return;
        groupInstance.Monsters.Add(leader);
        leader.Group = groupInstance;

        foreach (var member in group.Members)
        {
            if (!TrySpawnMonster(member.Id, spawnPoint, out var monster))
                continue;

            groupInstance.Monsters.Add(monster);
            monster.Group = groupInstance;
        }
    }

    private bool TrySpawnMonster(uint id, SpawnPoint spawnPoint, out MonsterEntity monster)
    {
        monster = new MonsterEntity(_monsterManager, _dropProvider, _animationManager, _serviceProvider, this,
            _logger,
            id,
            0,
            0
        );

        var ignoreAttrCheck =
            (EEntityType)monster.Proto.Type is EEntityType.NPC or EEntityType.WARP
            or EEntityType.GOTO; // TODO: mining ore

        var foundValidPositionAttr = ignoreAttrCheck;

        const int MAX_SPAWN_ATTEMPTS = 16;
        for (var attempt = 0; attempt < MAX_SPAWN_ATTEMPTS; attempt++)
        {
            var baseX = RandomizeWithinRange(spawnPoint.X, spawnPoint.RangeX);
            var baseY = RandomizeWithinRange(spawnPoint.Y, spawnPoint.RangeY);

            if (!monster.Proto.AiFlag.HasFlag(EAiFlags.NO_MOVE))
            {
                baseX = RandomizeWithinRange(baseX, SPAWN_BASE_OFFSET);
                baseY = RandomizeWithinRange(baseY, SPAWN_BASE_OFFSET);
            }

            monster.PositionX = (int)Position.X + baseX * SPAWN_POSITION_MULTIPLIER;
            monster.PositionY = (int)Position.Y + baseY * SPAWN_POSITION_MULTIPLIER;

            if (ignoreAttrCheck ||
                !monster.PositionIsAttr(EMapAttributes.BLOCK | EMapAttributes.OBJECT | EMapAttributes.NON_PVP))
            {
                foundValidPositionAttr = true;
                break;
            }
        }

        if (!foundValidPositionAttr)
        {
            _logger.LogWarning(
                "Cannot spawn mob on {Map}: failed to find spawn position with valid attr for {MonsterName} (id={MonsterId} {SpawnPointSummary})",
                Name, monster.Proto.TranslatedName, id,
                $"x={spawnPoint.X} y={spawnPoint.Y}, rangeX={spawnPoint.RangeX} rangeY={spawnPoint.RangeY}");
            return false;
        }

        if (monster.Proto.AiFlag.HasFlag(EAiFlags.NO_MOVE))
        {
            var compassDirection = (int)spawnPoint.Direction - 1;
            if (compassDirection < 0 || compassDirection > (int)Enum.GetValues<ESpawnPointDirection>().Last())
            {
                compassDirection = (int)ESpawnPointDirection.RANDOM;
            }

            monster.Rotation = SPAWN_ROTATION_SLICE_DEGREES * compassDirection;
        }

        if (monster.Rotation == 0)
        {
            monster.Rotation = RandomNumberGenerator.GetInt32(0, 360);
        }

        _world.SpawnEntity(monster);
        return true;

        int RandomizeWithinRange(int value, int range)
        {
            return range == 0 ? value : value + RandomNumberGenerator.GetInt32(-range, range);
        }
    }

    public void EnqueueGroupRespawn(MonsterGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.SpawnPoint is null) return;

        EventSystem.EnqueueEvent(() =>
        {
            // TODO
            SpawnGroup(group);
            return TimeSpan.Zero;
        }, TimeSpan.FromSeconds(group.SpawnPoint.RespawnTime));
    }

    public bool IsPositionInside(int x, int y)
    {
        return x >= Position.X && x < Position.X + Width * MAP_UNIT && y >= Position.Y &&
               y < Position.Y + Height * MAP_UNIT;
    }

    internal bool IsAttr(Coordinates coords, EMapAttributes flags)
    {
        return _attributes?.GetAttributesAt(coords).HasAnyFlags(flags) ?? false;
    }

    public void SpawnEntity(IEntity entity)
    {
        _pendingSpawns.Enqueue(entity);
    }

    // Real client/server default (CItem::StartDestroyEvent(int iSec=300) in the original item.cpp) - ground
    // items that are never picked up disappear after 5 minutes. Was never implemented here at all: nothing
    // ever called Map.DespawnEntity for a dropped item, so ground items piled up forever for the lifetime of
    // the server process, dragging down client FPS the more kills accumulated. Picking the item up still
    // despawns it immediately (PlayerEntity.PickupAsync) - this timer only covers the "never picked up" case.
    private static readonly TimeSpan GroundItemLifetime = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Add a ground item which will automatically get destroyed after configured time
    /// </summary>
    /// <param name="item">Item to add on the ground, should not have any owner!</param>
    /// <param name="x">Position X</param>
    /// <param name="y">Position Y</param>
    /// <param name="amount">Only used for gold as we have a higher limit here</param>
    /// <param name="ownerName"></param>
    public void AddGroundItem(ItemInstance item, int x, int y, uint amount = 0, string? ownerName = null)
    {
        var groundItem = new GroundItem(_animationManager, _world.GenerateVid(), item, amount, ownerName)
        {
            PositionX = x, PositionY = y
        };

        SpawnEntity(groundItem);

        EventSystem.EnqueueEvent(() =>
        {
            // Already picked up (or otherwise removed) in the meantime - _entities.Remove is a no-op then,
            // but skip the whole despawn dance if we can tell up front.
            if (_entities.Contains(groundItem))
            {
                DespawnEntity(groundItem);
            }

            return TimeSpan.Zero;
        }, GroundItemLifetime);
    }

    /// <summary>
    /// Should only be called by World
    /// </summary>
    /// <param name="entity"></param>
    public void DespawnEntity(IEntity entity)
    {
        _logger.LogDebug("Despawn {Entity}", entity);

        // Remove entity from entities list in the next update
        _pendingRemovals.Enqueue(entity);
    }

    public IEntity? GetEntity(uint vid)
    {
        return _entities.Find(e => e.Vid == vid);
    }
}