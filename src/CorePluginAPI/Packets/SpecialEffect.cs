using QuantumCore.API.Game.Types.Combat;
using QuantumCore.Networking;

namespace QuantumCore.API.Packets;

/// <summary>
/// HEADER_GC_SEPCIAL_EFFECT (114 / 0x72 - note the original protocol's own "SEPCIAL" typo). Real struct
/// order is header/type/vid (see packet.h's SPacketGCSpecialEffect) - triggers a client-side attached
/// visual (e.g. the critical-hit flash) on the entity identified by Vid, broadcast to everyone nearby, not
/// just the two combatants (unlike DamageInfo).
/// </summary>
[Packet(0x72, EDirection.OUTGOING)]
[PacketGenerator]
public partial class SpecialEffect
{
    [Field(0)] public ESpecialEffectType Type { get; set; }
    [Field(1)] public uint Vid { get; set; }
}
