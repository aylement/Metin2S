namespace QuantumCore.API.Game.Types.Combat;

/// <summary>
/// Matches the real client/server's SPECIAL_EFFECT enum (Packet.h) - values are significant, this is
/// sent over the wire as a raw byte via <see cref="QuantumCore.API.Packets.SpecialEffect"/>
/// (HEADER_GC_SEPCIAL_EFFECT, note the original protocol's own typo). Only a subset is used by this repo
/// so far; the rest are included for completeness/faithfulness to the real ordering.
/// </summary>
public enum ESpecialEffectType : byte
{
    NONE = 0,
    HPUP_RED,
    SPUP_BLUE,
    SPEEDUP_GREEN,
    DXUP_PURPLE,
    CRITICAL,
    PENETRATE,
    BLOCK,
    DODGE,
    CHINA_FIREWORK,
    SPIN_TOP,
    SUCCESS,
    FAIL,
    FR_SUCCESS,
    LEVELUP_ON_14_FOR_GERMANY,
    LEVELUP_UNDER_15_FOR_GERMANY,
    PERCENT_DAMAGE1,
    PERCENT_DAMAGE2,
    PERCENT_DAMAGE3,
    AUTO_HPUP,
    AUTO_SPUP,
    EQUIP_RAMADAN_RING,
    EQUIP_HALLOWEEN_CANDY,
    EQUIP_HAPPINESS_RING,
    EQUIP_LOVE_PENDANT
}
