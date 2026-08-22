using System.Net;
using Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using QuantumCore;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.World;
using QuantumCore.Extensions;
using QuantumCore.Game;
using QuantumCore.Game.Bots;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Services;
using QuantumCore.Game.World;
using QuantumCore.Game.World.Entities;
using Weikio.PluginFramework.Catalogs;

namespace Game.Tests;

public class WorldTests : IAsyncLifetime
{
    private World _world = null!;
    private PlayerEntity _playerEntity = null!;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ServerClock _clock;
    private readonly GameServer _gameServer;
    private readonly ServiceProvider _services;
    private static readonly string[] returnThis = ["maps:test_map"];

    public WorldTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Hosting:IpAddress", "0.0.0.0" } })
            .Build();
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(_ => config)
            .AddCoreServices(new EmptyPluginCatalog(), config)
            .AddGameServices()
            .AddSingleton(Substitute.For<IServerBase>())
            .AddSingleton(Substitute.For<IGameServer>())
            .AddSingleton(Substitute.For<IHostEnvironment>())
            .Configure<DatabaseOptions>(HostingOptions.MODE_GAME, opts =>
            {
                opts.ConnectionString = "Server:abc;";
                opts.Provider = DatabaseProvider.MYSQL;
            })
            .Replace(new ServiceDescriptor(typeof(IAtlasProvider), provider =>
            {
                var mock = Substitute.For<IAtlasProvider>();
                mock.GetAsync(Arg.Any<IWorld>()).Returns(info =>
                [
                    new Map(provider.GetRequiredService<IMonsterManager>(),
                        provider.GetRequiredService<IAnimationManager>(),
                        provider.GetRequiredService<ICacheManager>(), info.Arg<IWorld>()!,
                        provider.GetRequiredService<ILogger<Map>>(),
                        provider.GetRequiredService<ISpawnPointProvider>(),
                        provider.GetRequiredService<IMapAttributeProvider>(),
                        provider.GetRequiredService<IDropProvider>(),
                        provider.GetRequiredService<IServerBase>(),
                        "test_map", new Coordinates(), 1024, 1024, null, provider)
                ]);
                return mock;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(ICacheManager), _ =>
            {
                var mock = Substitute.For<ICacheManager>();
                mock.KeysAsync("maps:*").Returns(returnThis);
                mock.Subscribe().Returns(Substitute.For<IRedisSubscriber>());
                return mock;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(ICommandManager), null,
                (_, _) => Substitute.For<ICommandManager, ILoadable>(), ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(INpcShopProvider), null, (_, _) =>
            {
                var npcShopProvider = Substitute.For<INpcShopProvider, ILoadable>();
                npcShopProvider.Shops.Returns([]);
                return npcShopProvider;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(ISpawnPointProvider), _ =>
            {
                var mock = Substitute.For<ISpawnPointProvider>();
                mock.GetSpawnPointsForMapAsync("test_map").Returns([
                        .. Enumerable
                            .Range(0, 1)
                            .Select(_ =>
                                new SpawnPoint
                                {
                                    Chance = 100,
                                    Type = ESpawnPointType.MONSTER,
                                    Monster = 42,
                                    X = 1,
                                    Y = 1,
                                    RangeX = 0,
                                    RangeY = 0
                                }
                            )
                    ]
                );
                return mock;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(IJobManager), _ =>
            {
                var mock = Substitute.For<IJobManager, ILoadable>();
                mock.Get(EPlayerClassGendered.NINJA_FEMALE).Returns(new Job());
                return mock;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(IMonsterManager), _ =>
            {
                var mock = Substitute.For<IMonsterManager, ILoadable>();
                mock.GetMonster(42).Returns(new MonsterData { Type = (byte)EEntityType.MONSTER });
                mock.GetMonsters().Returns([
                    new MonsterData { Type = (byte)EEntityType.MONSTER }
                ]);
                return mock;
            }, ServiceLifetime.Singleton))
            .Replace(new ServiceDescriptor(typeof(TimeProvider), _ => _timeProvider, ServiceLifetime.Singleton))
            .AddSingleton(Substitute.For<IFileProvider>())
            .BuildServiceProvider();
        _clock = _services.GetRequiredService<ServerClock>();
        var server = _services.GetRequiredService<IServerBase>();
        server.Clock.Returns(_clock);
        _world = ActivatorUtilities.CreateInstance<World>(_services);
        _gameServer =
            ActivatorUtilities.CreateInstance<GameServer>(_services); // for setting the singleton GameServer.Instance
    }

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_services.GetServices<ILoadable>().Select(x => x.LoadAsync()));
        await _world.InitAsync();

        var conn = Substitute.For<IGameConnection>();
        conn.BoundIpAddress.Returns(IPAddress.Loopback);
        conn.Server.Returns(_gameServer);
        var playerData = new PlayerData
        {
            Name = "TestPlayer", PlayerClass = EPlayerClassGendered.NINJA_FEMALE, PositionX = 1, PositionY = 1
        };
        _playerEntity = ActivatorUtilities.CreateInstance<PlayerEntity>(_services, _world, playerData, conn);
        _world.SpawnEntity(_playerEntity);
        _world.Update(Tick(0.2)); // spawn all entities
    }

    public async ValueTask DisposeAsync()
    {
        _playerEntity.Dispose();
        _gameServer.Dispose();
        await _services.DisposeAsync();
    }

    [Fact]
    public void World_Update()
    {
        _world.Update(Tick(0.2));
        Assert.True(true);
    }

    [Fact]
    public void Player_Update()
    {
        _playerEntity.Update(Tick(1));
        Assert.True(true);
    }

    /// <summary>
    /// Proves the internal-bot feasibility claim end-to-end, without any TcpClient/socket involved:
    /// a PlayerEntity backed by a <see cref="NullGameConnection"/> can be spawned into the world via the
    /// same <see cref="IWorld.SpawnEntity"/> used for monsters, gets indexed by the map's QuadTree on the
    /// next tick, and becomes mutually visible ("nearby") with a normal player entity - exactly what a
    /// real connected client would need in order to actually see the bot appear.
    ///
    /// Deliberately does NOT reuse the <c>_world</c>/<c>_playerEntity</c> fields set up by this class'
    /// constructor/InitializeAsync: that <c>_world</c> instance is built by hand via
    /// ActivatorUtilities.CreateInstance and never goes through LoadAsync(), so its map grid is never
    /// populated and IWorld.SpawnEntity silently no-ops for it (logged as "No Map found for this
    /// coordinate"). The properly initialized IWorld is the one resolved from the DI container, which
    /// does get LoadAsync() called on it as part of the ILoadable sweep in InitializeAsync() above.
    /// </summary>
    [Fact]
    public void Bot_SpawnedNearRealPlayer_BecomesMutuallyVisible()
    {
        var world = _services.GetRequiredService<IWorld>();

        var realPlayerData = new PlayerData
        {
            Name = "RealPlayer", PlayerClass = EPlayerClassGendered.NINJA_FEMALE, PositionX = 1, PositionY = 1
        };
        var realConnection = Substitute.For<IGameConnection>();
        realConnection.Server.Returns(_gameServer);
        var realPlayer = ActivatorUtilities.CreateInstance<PlayerEntity>(_services, world, realPlayerData,
            realConnection);
        world.SpawnEntity(realPlayer);

        var botDataFactory = _services.GetRequiredService<BotPlayerDataFactory>();
        var botData = botDataFactory.Create("TestBot", EEmpire.JINNO, EPlayerClassGendered.NINJA_FEMALE);
        botData.PositionX = realPlayer.PositionX;
        botData.PositionY = realPlayer.PositionY;

        // Same "fake connection" pattern as realPlayer above (Substitute.For<IGameConnection>()), but
        // with the real, non-mocked NullGameConnection this feasibility study produced - proving it
        // behaves correctly under the same conditions, not just that a dynamic proxy can.
        var botConnection = new NullGameConnection(_gameServer);
        var bot = ActivatorUtilities.CreateInstance<PlayerEntity>(_services, world, botData, botConnection);
        botConnection.Player = bot;
        world.SpawnEntity(bot);

        world.Update(Tick(10)); // let the QuadTree index both entities and compute their neighbours

        Assert.Same(bot, world.GetPlayer("TestBot"));
        Assert.Same(realPlayer, world.GetPlayer("RealPlayer"));
        Assert.Contains(bot, realPlayer.NearbyEntities);
        Assert.Contains(realPlayer, bot.NearbyEntities);

        bot.Dispose();
        realPlayer.Dispose();
    }

    private TickContext Tick(double elapsedMilliseconds)
    {
        var delta = TimeSpan.FromMilliseconds(elapsedMilliseconds);
        _timeProvider.Advance(delta);
        var now = _clock.Now;
        return new TickContext(_clock, delta, now);
    }
}