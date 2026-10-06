using System.Collections.Immutable;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Extensions;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.World;
using QuantumCore.Core.Utils;

// SendItem/SendPoints (used below) are extension methods on IPlayerEntity declared in
// CorePluginAPI/Extensions/PlayerExtensions.cs, whose namespace is QuantumCore.Game (not
// QuantumCore.Game.Extensions, despite the file living under an "Extensions" folder) - visible here
// without an explicit `using` because QuantumCore.Game.Items nests under QuantumCore.Game.
namespace QuantumCore.Game.Items;

/// <summary>
/// One possible reward line inside a <see cref="BoxDefinition"/>'s loot table (see
/// <see cref="BoxDefinitions"/>). Every reward line in a box has equal weight - matching how these boxes
/// describe themselves in-game ("a random reward among the list below"), not a weighted rarity table.
/// </summary>
public abstract class BoxReward
{
    /// <summary>
    /// Grants this reward to the player and returns a short human-readable description of what they got,
    /// for the chat message shown after opening the box - or null if granting failed (currently only
    /// possible for <see cref="ItemBoxReward"/>, when the inventory has no free slot). The caller (see
    /// ItemUseHandler) must only consume the box itself once this returns non-null, matching how every
    /// other failure case in that handler (full inventory, etc.) leaves the triggering item untouched
    /// rather than destroying it for nothing.
    /// </summary>
    public abstract Task<string?> GrantAsync(IPlayerEntity player, IItemManager itemManager,
        IItemRepository itemRepository);
}

/// <summary>
/// Grants one item. When <see cref="ItemIdChoices"/> has more than one id (e.g. the same weapon at +1 or
/// +2), one is picked uniformly at random - this is how refine-level ranges ("+2 à +4") are represented
/// here: this server has no separate per-instance refine level yet (see ItemInstance), but each refine
/// level of these specific items already exists as its own item_proto id, so picking among those ids
/// achieves the same result without needing that feature built first.
/// </summary>
public sealed class ItemBoxReward : BoxReward
{
    public required ImmutableArray<uint> ItemIdChoices { get; init; }
    public byte Count { get; init; } = 1;

    public override async Task<string?> GrantAsync(IPlayerEntity player, IItemManager itemManager,
        IItemRepository itemRepository)
    {
        var itemId = ItemIdChoices[CoreRandom.GenerateInt32(0, ItemIdChoices.Length)];
        var proto = itemManager.GetItem(itemId);
        if (proto is null)
        {
            // Shouldn't happen (every id in BoxDefinitions was hand-verified against a live item_proto
            // dump) - but an uncaught exception here would disconnect the player (packet handlers run
            // under ServerBase's catch-and-close, see DropProvider's own note on this), so a
            // misconfigured id fails the same safe way as "no inventory space" rather than that.
            return null;
        }

        var instance = itemManager.CreateItem(proto, Count);
        instance.PlayerId = player.Player.Id;

        if (!await player.Inventory.PlaceItemAsync(instance))
        {
            return null;
        }

        await instance.PersistAsync(itemRepository);
        player.SendItem(instance);

        return Count > 1 ? $"{Count}x {proto.TranslatedName}" : proto.TranslatedName;
    }
}

public sealed class GoldBoxReward : BoxReward
{
    public required uint Amount { get; init; }

    public override Task<string?> GrantAsync(IPlayerEntity player, IItemManager itemManager,
        IItemRepository itemRepository)
    {
        player.AddPoint(EPoint.GOLD, (int)Amount);
        player.SendPoints();
        return Task.FromResult<string?>($"{Amount:N0} Yangs");
    }
}

public sealed class ExperienceBoxReward : BoxReward
{
    public required uint Amount { get; init; }

    public override Task<string?> GrantAsync(IPlayerEntity player, IItemManager itemManager,
        IItemRepository itemRepository)
    {
        player.AddPoint(EPoint.EXPERIENCE, (int)Amount);
        player.SendPoints();
        return Task.FromResult<string?>($"{Amount:N0} EXP");
    }
}
