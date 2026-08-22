using QuantumCore.Networking;

namespace QuantumCore.API.Packets.Skills;

/// <summary>
/// Tells the client to drop a previously-added buff icon/visual effect - matches the real protocol's
/// <c>TPacketGCAffectRemove</c> (header 0x7F).
/// </summary>
[Packet(0x7F, EDirection.OUTGOING)]
[PacketGenerator]
public partial class AffectRemove
{
    /// <summary>The same skill id the matching AffectAdd used.</summary>
    [Field(0)] public uint Type { get; set; }

    /// <summary>The same EPoint the matching AffectAdd used.</summary>
    [Field(1)] public byte ApplyOn { get; set; }
}
