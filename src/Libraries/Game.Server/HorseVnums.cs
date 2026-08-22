namespace QuantumCore.Game;

/// <summary>
/// Maps a player's horse level (0-30, see <see cref="QuantumCore.API.Core.Models.PlayerData.HorseLevel"/>)
/// to a mob proto id to mount, by appearance tier. Values verified directly against the original game's
/// <c>c_aHorseStat[HORSE_MAX_LEVEL+1]</c> table (horse_rider.cpp) - level 0 is <c>iNPCRace=0</c> (no
/// mount), 1-10 is <c>20101</c> ("초기"/base tier), 11-20 is <c>20104</c> ("중기"/mid tier - light armor,
/// original data lets the rider still attack from horseback at this tier), 21-30 is <c>20107</c>
/// ("말기"/final tier - war horse, heavy armor, also grants horse-specific skills in the original game -
/// not implemented here, out of scope for now per the appearance-only ask this was built for). All three
/// mob ids are verified present in this repo's actual src/Executables/Single/data/mob_proto.
/// </summary>
public static class HorseVnums
{
    public const byte MaxLevel = 30;

    private const uint BaseHorse = 20101; // levels 1-10
    private const uint MidHorse = 20104; // levels 11-20, light armor
    private const uint WarHorse = 20107; // levels 21-30, heavy armor

    /// <summary>Returns 0 (no mount) for level 0, otherwise a mob proto id to ride.</summary>
    public static uint ForLevel(byte horseLevel)
    {
        return horseLevel switch
        {
            0 => 0,
            <= 10 => BaseHorse,
            <= 20 => MidHorse,
            _ => WarHorse
        };
    }
}
