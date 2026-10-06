using System.Collections.Immutable;

namespace QuantumCore.Game.Items;

/// <summary>
/// Hand-authored loot tables for single-use "open for a random reward" box items (see
/// <see cref="BoxReward"/> and ItemUseHandler, the only place this is read from). Deliberately not
/// data/file-driven like the monster drop tables in DropProvider - there's exactly one box so far and
/// keeping it as plain C# keeps the id-picking reasoning (see the comments below) attached to the data
/// itself instead of split across a loot-table file and a separate design doc.
/// </summary>
public static class BoxDefinitions
{
    /// <summary>
    /// Esoteric Leader's Box (item_proto id 50071, confirmed via a live item_proto dump - see
    /// item_dump.txt / the `finditem` GM command). Every entry below has equal weight, matching the
    /// real box's own description ("a random reward among the list below"), not a weighted rarity
    /// table.
    ///
    /// v1 scope (2026-08-24): about a third of the originally requested reward list has no matching
    /// item_proto entry in this server's current data at all (Costume of Murderous Wind, Ghost Face
    /// Armor, Kobold Armor, Heaven and Earth Gong, Lightning Dagger, Predator Claw, Cape of the Evader,
    /// Detection Glass) and was dropped rather than guessed at - add them here once those items exist.
    /// Refine-level ranges (e.g. "+2 to +4") are NOT a real per-instance mechanic yet (ItemInstance has
    /// no refine field at all) - instead, each refine level of these specific equipment items already
    /// exists as its own item_proto id (e.g. Nymph Sword+1 and +2 are different ids), so
    /// <see cref="ItemBoxReward.ItemIdChoices"/> picks among those directly. A few reward lines had
    /// several same-named candidate ids in item_proto with no visible way to tell them apart from the
    /// dump alone (duration variants like "Experience Ring (1h)"/"Thief's Gloves (2h)", or which of
    /// several near-identical "Blessing Scroll"/"Language Ring" entries is the intended one) - one
    /// plausible id was picked for each; worth double-checking in-game once this ships.
    /// </summary>
    public static readonly ImmutableArray<BoxReward> EsotericLeaderBoxRewards =
    [
        // Armor (refine range represented as separate ids - see class doc comment above)
        new ItemBoxReward { ItemIdChoices = [11872, 11873, 11874] }, // Orange Cat Dress +2/+3/+4

        // Weapons (+1 or +2)
        new ItemBoxReward { ItemIdChoices = [3141, 3142] }, // Electromagnetic Blade
        new ItemBoxReward { ItemIdChoices = [161, 162] }, // Nymph Sword
        new ItemBoxReward { ItemIdChoices = [241, 242] }, // Exorcism Sword
        new ItemBoxReward { ItemIdChoices = [2131, 2132] }, // Divine Apricot Bow
        new ItemBoxReward { ItemIdChoices = [7141, 7142] }, // Salvation Fan

        // Jewelry (+1 to +3)
        new ItemBoxReward { ItemIdChoices = [16181, 16182, 16183] }, // Amethyst Necklace
        new ItemBoxReward { ItemIdChoices = [17181, 17182, 17183] }, // Amethyst Earrings
        new ItemBoxReward { ItemIdChoices = [14181, 14182, 14183] }, // Amethyst Bracelet

        // Other
        new ItemBoxReward { ItemIdChoices = [25040] }, // Blessing Scroll
        new ItemBoxReward { ItemIdChoices = [39006], Count = 20 }, // Bravery Cape x20
        new ItemBoxReward { ItemIdChoices = [70014] }, // Blood Pill ("Dragée de sang")
        new ItemBoxReward { ItemIdChoices = [70006] }, // Language Ring
        new ItemBoxReward { ItemIdChoices = [71049] }, // Silk Bundle ("Tissu fin")
        new ItemBoxReward { ItemIdChoices = [71015] }, // Experience Ring (1h)
        new ItemBoxReward { ItemIdChoices = [70043] }, // Thief's Glove (2h)
        new ItemBoxReward { ItemIdChoices = [27112], Count = 15 }, // Green Potion (L) x15
        new ItemBoxReward { ItemIdChoices = [70037] }, // Book of Forgetfulness
        new ItemBoxReward { ItemIdChoices = [70012] }, // Goddess Tear
        new ItemBoxReward { ItemIdChoices = [71011] }, // Emotion Mask ("Sac à émotion")

        // Currency / experience
        new GoldBoxReward { Amount = 200_000 },
        new GoldBoxReward { Amount = 100_000 },
        new GoldBoxReward { Amount = 50_000 },
        new ExperienceBoxReward { Amount = 200_000 },
        new ExperienceBoxReward { Amount = 150_000 }
    ];

    /// <summary>
    /// Item ids that trigger box-opening behavior in ItemUseHandler, mapped to their reward table.
    /// </summary>
    public static readonly ImmutableDictionary<uint, ImmutableArray<BoxReward>> Boxes =
        ImmutableDictionary.CreateRange([
            new KeyValuePair<uint, ImmutableArray<BoxReward>>(50071, EsotericLeaderBoxRewards)
        ]);
}
