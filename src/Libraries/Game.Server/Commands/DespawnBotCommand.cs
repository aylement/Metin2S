using CommandLine;
using Microsoft.Extensions.Logging;
using QuantumCore.API.Game;
using QuantumCore.API.Game.World;
using QuantumCore.Game.Bots;

namespace QuantumCore.Game.Commands;

/// <summary>
/// Removes a bot previously created with <c>/botspawn</c> from the world and disposes its entity.
/// </summary>
[Command("botdespawn", "Removes an internal bot player from the world")]
public class DespawnBotCommand : ICommandHandler<DespawnBotCommandOptions>
{
    private readonly ILogger<DespawnBotCommand> _logger;
    private readonly IWorld _world;
    private readonly BotRegistry _botRegistry;

    public DespawnBotCommand(ILogger<DespawnBotCommand> logger, IWorld world, BotRegistry botRegistry)
    {
        _logger = logger;
        _world = world;
        _botRegistry = botRegistry;
    }

    public async Task ExecuteAsync(CommandContext<DespawnBotCommandOptions> context)
    {
        var name = context.Arguments.Name;
        var bot = _botRegistry.Get(name);
        if (bot is null)
        {
            context.Player.SendChatInfo($"No bot named '{name}' is currently tracked");
            return;
        }

        await _world.DespawnPlayerAsync(bot);
        _botRegistry.Remove(name);

        _logger.LogInformation("Despawned bot player entity '{Name}'", name);
        context.Player.SendChatInfo($"Bot '{name}' despawned");
    }
}

public class DespawnBotCommandOptions
{
    [Value(0, Required = true)] public string Name { get; set; } = "";
}
