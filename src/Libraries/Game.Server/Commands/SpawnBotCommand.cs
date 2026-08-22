using System.Security.Cryptography;
using CommandLine;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Game;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.World;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Bots;

namespace QuantumCore.Game.Commands;

/// <summary>
/// Creates a <see cref="BotPlayerEntity"/> via <see cref="BotPlayerFactory"/> (same
/// ActivatorUtilities.CreateInstance + LoadAsync() pattern as a real player, see
/// <see cref="QuantumCore.Game.PlayerFactory"/>), backed by a <see cref="NullGameConnection"/> instead of
/// a real network connection, then inserts it into the world exactly like <c>EnterGameHandler</c> does for
/// a real player - same <see cref="IWorld.SpawnEntity"/> call used for monsters. From the next tick onward
/// the map's QuadTree indexes the bot and computes its neighbours, so nearby real players will see it
/// appear, and its own <see cref="BotPlayerEntity.Update"/> makes it wander on its own.
/// </summary>
[Command("botspawn", "Creates and spawns an internal bot player")]
public class SpawnBotCommand : ICommandHandler<SpawnBotCommandOptions>
{
    private readonly ILogger<SpawnBotCommand> _logger;
    private readonly BotPlayerFactory _botPlayerFactory;
    private readonly BotPlayerDataFactory _botPlayerDataFactory;
    private readonly IServerBase _server;
    private readonly IWorld _world;
    private readonly BotRegistry _botRegistry;

    public SpawnBotCommand(ILogger<SpawnBotCommand> logger, BotPlayerFactory botPlayerFactory,
        BotPlayerDataFactory botPlayerDataFactory, IServerBase server, IWorld world, BotRegistry botRegistry)
    {
        _logger = logger;
        _botPlayerFactory = botPlayerFactory;
        _botPlayerDataFactory = botPlayerDataFactory;
        _server = server;
        _world = world;
        _botRegistry = botRegistry;
    }

    public async Task ExecuteAsync(CommandContext<SpawnBotCommandOptions> context)
    {
        var name = context.Arguments.Name;

        if (_botRegistry.Contains(name) || _world.GetPlayer(name) is not null)
        {
            context.Player.SendChatInfo($"A player or bot named '{name}' already exists");
            return;
        }

        var playerData = _botPlayerDataFactory.Create(name, context.Player.Player.Empire,
            context.Arguments.PlayerClass);

        // Spawn a few meters from the GM instead of right on top of them or at the empire's town -
        // purely for convenience while testing, has no bearing on the feasibility question itself.
        // 100 units ~= 1 meter (same convention as the mob wander distances in SimpleBehaviour).
        const int MinOffset = 300;
        const int MaxOffset = 500;
        var distance = RandomNumberGenerator.GetInt32(MinOffset, MaxOffset + 1);
        var (dx, dy) = MathUtils.GetDeltaByDegree(RandomNumberGenerator.GetInt32(0, 360));
        playerData.PositionX = context.Player.PositionX + (int)(dx * distance);
        playerData.PositionY = context.Player.PositionY + (int)(dy * distance);

        var connection = new NullGameConnection(_server)
        {
            AccountId = BotPlayerDataFactory.BotAccountId,
            Username = $"bot:{name}"
        };

        var bot = await _botPlayerFactory.CreateBotAsync(connection, playerData);
        connection.Player = bot;

        _botRegistry.Add(bot);
        _world.SpawnEntity(bot);

        _logger.LogInformation("Spawned bot player entity '{Name}' (Vid {Vid}) at ({X};{Y})",
            bot.Name, bot.Vid, bot.PositionX, bot.PositionY);
        context.Player.SendChatInfo(
            $"Bot '{bot.Name}' spawned (Vid {bot.Vid}, HP {bot.Health}/{bot.Player.MaxHp}).");
    }
}

public class SpawnBotCommandOptions
{
    [Value(0, Required = true)] public string Name { get; set; } = "";

    [Value(1)] public EPlayerClassGendered PlayerClass { get; set; } = EPlayerClassGendered.WARRIOR_MALE;
}
