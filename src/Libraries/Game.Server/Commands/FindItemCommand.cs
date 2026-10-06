using CommandLine;
using QuantumCore.API;
using QuantumCore.API.Extensions;
using QuantumCore.API.Game;

namespace QuantumCore.Game.Commands;

// Temporary lookup tool for building the Esoteric Leader's Box loot table (see the box-opening feature
// work) - finds item_proto ids by (partial, case-insensitive) name so real item ids can be plugged into
// the reward table instead of guessed. Cheap to keep around afterwards as a general debug command.
[Command("finditem", "Searches item_proto by name substring")]
internal class FindItemCommand : ICommandHandler<FindItemCommandOptions>
{
    private readonly IItemManager _itemManager;

    public FindItemCommand(IItemManager itemManager)
    {
        _itemManager = itemManager;
    }

    public Task ExecuteAsync(CommandContext<FindItemCommandOptions> context)
    {
        var needle = context.Arguments.Query;
        var matches = _itemManager.GetItems()
            .Where(i => i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        i.TranslatedName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToArray();

        if (matches.Length == 0)
        {
            context.Player.SendChatInfo($"No item matching '{needle}'");
            return Task.CompletedTask;
        }

        foreach (var item in matches)
        {
            context.Player.SendChatInfo($"{item.Id}: {item.Name} / {item.TranslatedName}");
        }

        return Task.CompletedTask;
    }
}

public class FindItemCommandOptions
{
    [Value(0)] public string Query { get; set; } = "";
}
