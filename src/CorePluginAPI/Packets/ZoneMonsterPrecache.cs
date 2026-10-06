using QuantumCore.Networking;

namespace QuantumCore.API.Packets;

/// <summary>
/// Sent once, right when a player's entity is actually spawned into a map (see
/// <c>Map.Update()</c>'s pending-spawn processing), with the distinct set of monster/NPC race ids that
/// map can ever spawn (resolved server-side from that map's regen.txt - including group/group-collection
/// entries, which the client can't resolve itself since it never parses regen.txt in the shipped game
/// build - only the map editor tool does, see the client's own MapOutdoorLoad.cpp under #ifdef
/// WORLD_EDITOR).
///
/// The client uses this purely as a hint to start background-loading those races' models/textures/motions
/// (CRaceData::CollectResourceFileNames -> CResourceManager::PushBackgroundLoadingSet) BEFORE the player
/// actually runs into one, instead of the freeze that happens today when CResource::Load() has to do a
/// synchronous disk read + LZO decompress + GPU upload the first time each race is actually seen. Losing
/// this packet (e.g. an older/incompatible client) only means prefetching doesn't happen - nothing here
/// is required for correct gameplay.
/// </summary>
[Packet(0xD3, EDirection.OUTGOING)]
[PacketGenerator]
public partial class ZoneMonsterPrecache
{
    // Despite the name/getter, the generator overrides this field's serialized VALUE with the total
    // packet byte size (header + this field + RaceIds bytes) at write time - see
    // Core.Networking.Generators/SerializeGenerator.cs's GenerateGetSizeMethod, which any field
    // referencing a dynamic array gets substituted for (this.GetSize() replaces the getter above). The
    // TYPE must stay ushort/WORD (not uint): the client's generic dynamic-packet framing
    // (TDynamicSizePacketHeader in Packet.h) is BYTE header + WORD size for every dynamic packet in the
    // protocol - a wider field here silently breaks that framing client-side.
    [Field(0)] public ushort Size => (ushort)RaceIds.Length;

    [Field(1)] public uint[] RaceIds { get; set; } = Array.Empty<uint>();
}
