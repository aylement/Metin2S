using CommandLine;
using QuantumCore.API.Game;

namespace QuantumCore.Game.Commands;

/// <summary>
/// Sets the caller's horse level (0 = no horse, 1-30 = HorseVnums.MaxLevel), unlocking /mount at that
/// tier. See HorseVnums.cs for the (simplified) level-to-mob mapping and PlayerData.HorseLevel for why
/// this isn't persisted yet.
/// </summary>
[Command("sethorselevel", "Sets your horse level (0-30), unlocking /mount")]
public class SetHorseLevelCommand : ICommandHandler<SetHorseLevelCommandOptions>
{
    public Task ExecuteAsync(CommandContext<SetHorseLevelCommandOptions> context)
    {
        if (context.Arguments.Level > HorseVnums.MaxLevel)
        {
            context.Player.SendChatInfo($"Horse level must be between 0 and {HorseVnums.MaxLevel}");
            return Task.CompletedTask;
        }

        context.Player.Player.HorseLevel = context.Arguments.Level;
        context.Player.SendChatInfo(context.Arguments.Level == 0
            ? "Horse level cleared - /mount is locked again"
            : $"Horse level set to {context.Arguments.Level} - try /mount");
        return Task.CompletedTask;
    }
}

public class SetHorseLevelCommandOptions
{
    [Value(0, Required = true)] public byte Level { get; set; }
}
