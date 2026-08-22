namespace QuantumCore.Game;

/// <summary>
/// Per-horse-level rider stat bonuses, taken verbatim from the original game's own
/// <c>c_aHorseStat[HORSE_MAX_LEVEL+1]</c> table (<c>horse_rider.cpp</c>) - one row per level 0-30, columns
/// <c>iMinLevel, iNPCRace, iMaxHealth, iMaxStamina, iST, iDX, iHT, iIQ, iDamMean, iDamMin, iDamMax,
/// iArmor</c>. Only <c>iST/iDX/iHT/iIQ</c> (rider stat bonus) and <c>iArmor</c> (rider defence bonus) are
/// modeled here - see the doc comment on each accessor for why the rest is out of scope for now.
///
/// Confirmed against the original <c>CHARACTER::ComputePoints()</c> (char.cpp) that these are NOT additive
/// bonuses stacked on top of the rider's own stats - they're a FLOOR: while riding, each of
/// ST/DX/HT/IQ/Armor becomes <c>max(rider's own value, horse's value at the current horse level)</c>
/// (verbatim: <c>if (GetHorseST() > GetPoint(POINT_ST)) PointChange(POINT_ST, GetHorseST() -
/// GetPoint(POINT_ST));</c> for stats, and <c>if (iArmor &lt; GetHorseArmor()) iArmor =
/// GetHorseArmor();</c> for armor) - a decent horse stat only ever shows up once it exceeds what the rider
/// already has on their own.
///
/// Deliberately NOT modeled: <c>iMaxHealth</c>/<c>iMaxStamina</c> (the horse's own separate HP/stamina
/// pool - dying/starving/feeding is a whole separate subsystem this repo doesn't have yet) and
/// <c>iDamMean</c>/<c>iDamMin</c>/<c>iDamMax</c> (used by the original game for the horse's own kick
/// attack at the war tier, not a rider damage bonus - a separate, later feature).
///
/// <c>iMinLevel</c> (25/35/50 per tier) IS now modeled (<see cref="MinLevel"/>) and enforced by
/// <c>Commands/MountCommand.cs</c>'s no-arg path - the real game gates actually RIDING a tier on
/// character level, separately from the level-10 gate <c>HorseTrialQuest</c> already enforces just to
/// access the mount menu at all (<c>horse_ride.quest</c>'s own <c>pc.level>=10</c>, unrelated to this
/// per-tier gate). <c>/sethorselevel</c> and <c>/mount &lt;explicit vnum&gt;</c> both still deliberately
/// bypass it, same as they already bypass everything else - they're the GM/testing override path, matching
/// the convention <c>MountCommand.cs</c>'s own doc comment already describes for the explicit-vnum case.
/// </summary>
public static class HorseStats
{
    private readonly struct Row
    {
        public readonly byte MinLevel, St, Dx, Ht, Iq, Armor;

        public Row(byte minLevel, byte st, byte dx, byte ht, byte iq, byte armor)
        {
            MinLevel = minLevel;
            St = st;
            Dx = dx;
            Ht = ht;
            Iq = iq;
            Armor = armor;
        }
    }

    // Index = horse level (0-30). MinLevel, ST, DX, HT, IQ, Armor columns only - see class doc comment.
    private static readonly Row[] Table =
    [
        new Row(0, 0, 0, 0, 0, 0), // 0: no horse
        new Row(25, 26, 35, 18, 9, 32), // 1 (초기/base tier starts)
        new Row(25, 27, 36, 18, 9, 33),
        new Row(25, 28, 38, 19, 9, 33),
        new Row(25, 29, 39, 19, 10, 34),
        new Row(25, 30, 40, 20, 10, 34),
        new Row(25, 31, 41, 21, 10, 35),
        new Row(25, 32, 42, 21, 11, 36),
        new Row(25, 33, 44, 22, 11, 36),
        new Row(25, 34, 45, 22, 11, 37),
        new Row(25, 35, 46, 23, 12, 37),
        new Row(35, 40, 53, 27, 13, 41), // 11 (중기/mid tier starts)
        new Row(35, 41, 54, 27, 14, 42),
        new Row(35, 42, 56, 28, 14, 42),
        new Row(35, 43, 57, 28, 14, 43),
        new Row(35, 44, 58, 29, 15, 43),
        new Row(35, 44, 59, 30, 15, 44),
        new Row(35, 45, 60, 30, 15, 45),
        new Row(35, 46, 62, 31, 15, 45),
        new Row(35, 47, 63, 31, 16, 46),
        new Row(35, 48, 64, 32, 16, 46),
        new Row(50, 53, 71, 36, 18, 50), // 21 (말기/war tier starts)
        new Row(50, 55, 74, 37, 18, 51),
        new Row(50, 57, 76, 38, 19, 52),
        new Row(50, 59, 78, 39, 20, 54),
        new Row(50, 60, 80, 40, 20, 54),
        new Row(50, 61, 81, 40, 20, 55),
        new Row(50, 62, 83, 42, 21, 56),
        new Row(50, 63, 84, 42, 21, 57),
        new Row(50, 65, 87, 43, 22, 58),
        new Row(50, 67, 89, 45, 22, 59)
    ];

    private static Row RowFor(byte horseLevel) => Table[Math.Min(horseLevel, (byte)(Table.Length - 1))];

    public static byte MinLevel(byte horseLevel) => RowFor(horseLevel).MinLevel;
    public static byte St(byte horseLevel) => RowFor(horseLevel).St;
    public static byte Dx(byte horseLevel) => RowFor(horseLevel).Dx;
    public static byte Ht(byte horseLevel) => RowFor(horseLevel).Ht;
    public static byte Iq(byte horseLevel) => RowFor(horseLevel).Iq;
    public static byte Armor(byte horseLevel) => RowFor(horseLevel).Armor;
}
