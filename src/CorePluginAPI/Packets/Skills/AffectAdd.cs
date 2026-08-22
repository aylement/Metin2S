using QuantumCore.Networking;

namespace QuantumCore.API.Packets.Skills;

/// <summary>
/// Tells the client to show a buff icon (top-left bar) and, if <see cref="Flag"/> carries one, the
/// matching visual effect on the character model (e.g. a weapon aura) - matches the real protocol's
/// <c>TPacketGCAffectAdd</c>/<c>TPacketAffectElement</c> (header 0x7E) exactly, field for field.
/// </summary>
[Packet(0x7E, EDirection.OUTGOING)]
[PacketGenerator]
public partial class AffectAdd
{
    /// <summary>The originating skill's id - the client looks up the icon/name/tooltip from this.</summary>
    [Field(0)] public uint Type { get; set; }

    /// <summary>Which EPoint this buff affects (matches QuantumCore.API.Game.Types.Entities.EPoint).</summary>
    [Field(1)] public byte PointIdxApplyOn { get; set; }

    [Field(2)] public int ApplyValue { get; set; }

    /// <summary>EAffectFlags bitmask - drives the on-model visual effect, if any.</summary>
    [Field(3)] public uint Flag { get; set; }

    [Field(4)] public int Duration { get; set; }
    [Field(5)] public int SpCost { get; set; }
}
