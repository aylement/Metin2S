using QuantumCore.API;
using QuantumCore.API.Game.Types;
using QuantumCore.Game.Drops;

namespace QuantumCore.Game.Extensions;

public class GameOptions
{
    /// <summary>
    /// Contains the in-game shop webpage address
    /// </summary>
    public string InGameShop { get; set; } = "https://example.com/";

    /// <summary>
    /// Contains the starting locations for each empire.
    /// <remarks>Index 0 will always contain a invalid empire coordinates</remarks>
    /// </summary>
    public Dictionary<EEmpire, Coordinates> Empire { get; set; } = new();

    public SkillsOptions Skills { get; set; } = new SkillsOptions();

    public DropOptions Drops { get; set; } = new DropOptions();

    /// <summary>
    /// How often (in seconds) every connected player's state gets persisted in the background, independent
    /// of disconnect. There was no such mechanism at all before this - saves only ever happened on
    /// disconnect (<see cref="QuantumCore.Game.World.Entities.PlayerEntity.OnDespawnAsync"/>), so a
    /// forceful process kill (crash, or an operator not going through a graceful shutdown) lost everything
    /// since the player's last disconnect. 60s by default; set to 0 to disable autosave entirely.
    /// </summary>
    public int AutoSaveIntervalSeconds { get; set; } = 60;
}

public class DropOptions
{
    /// <summary>
    /// Contains the delta chances for normal and boss monsters
    /// <remarks>Delta chance is applied in auxiliary item drop calculations in basis of the monster level that got killed.
    /// Check <see cref="QuantumCore.Game.Services.DropProvider"/> for implementation details</remarks>
    /// </summary>
    public DeltaChances Delta { get; set; } = new DeltaChances();

    /// <summary>
    /// When true, skips the real game's level-difference drop-rate penalty entirely (a killer far above a
    /// monster's level normally gets a drastically reduced, often ~0%, chance at any drop at all - the
    /// <see cref="Delta"/> table below, indexed by "monster level + 15 - player level") and always uses a
    /// neutral 100% delta instead. Off by default to match the real game; opt in via
    /// <c>Game:Drops:IgnoreLevelDifference</c> in appsettings.json.
    /// </summary>
    public bool IgnoreLevelDifference { get; set; }

    /// <summary>
    /// Contains metin stone info regarding spirit stone chances and rank level chances (+0,+1,+2...)
    /// </summary>
    public IReadOnlyList<MetinStoneDrop> MetinStones { get; set; } = new List<MetinStoneDrop>();

    /// <summary>
    /// Contains the spirit stone ids (at +0) that are used in metin drops
    /// </summary>
    public IReadOnlyList<uint> SpiritStones { get; set; } = new List<uint>();
}

public class DeltaChances
{
    public IReadOnlyList<uint> Boss { get; set; } = [];
    public IReadOnlyList<uint> Normal { get; set; } = [];
}

public class SkillsOptions
{
    /// <summary>
    /// Skill book id that is used when creating a specific skill book for a skill
    /// </summary>
    public uint GenericSkillBookId { get; set; } = 50300;

    /// <summary>
    /// Identifier for iterating over skill book ids
    /// </summary>
    public uint SkillBookStartId { get; set; } = 50400;

    /// <summary>
    /// Consumed player experience when using a skill book
    /// </summary>
    public int SkillBookNeededExperience { get; set; } = 20000;

    /// <summary>
    /// Minimum delay to wait after using a skill book
    /// </summary>
    public int SkillBookDelayMin { get; set; } = 64800;

    /// <summary>
    /// Maximum delay to wait after using a skill book
    /// </summary>
    public int SkillBookDelayMax { get; set; } = 108000;

    /// <summary>
    /// Identifier for the soul stone item
    /// </summary>
    public int SoulStoneId { get; set; } = 50513;
}
