using QuantumCore.API.Game.Types;
using QuantumCore.Networking;

namespace QuantumCore.API;

public interface IConnection
{
    Guid Id { get; }
    EPhase Phase { get; set; }
    Task? ExecuteTask { get; }
    void Close(bool expected = true);

    /// <summary>
    /// Marks that any upcoming socket close on this connection was deliberately triggered by the server
    /// itself (currently: a Warp packet telling the client to disconnect and reconnect) rather than the
    /// player just closing the game - see the comment on Connection's `_expectedClose` field for why this
    /// distinction matters (it changes which cleanup path GameConnection.OnCloseAsync takes).
    /// </summary>
    void MarkExpectedClose();
    void Send<T>(T packet) where T : IPacketSerializable;
    Task StartAsync(CancellationToken token = default);
}
