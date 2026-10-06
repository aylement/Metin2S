using System.Net;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.World;
using QuantumCore.Networking;

namespace QuantumCore.Game.Bots;

/// <summary>
/// A <see cref="IGameConnection"/> implementation that is not backed by any socket.
/// Used to let a <see cref="QuantumCore.Game.World.Entities.PlayerEntity"/> exist and be simulated
/// by the server without a real client attached (e.g. for internal bots).
/// All outgoing packets are silently discarded instead of being sent anywhere.
/// </summary>
public class NullGameConnection : IGameConnection
{
    public Guid Id { get; } = Guid.NewGuid();
    public EPhase Phase { get; set; } = EPhase.GAME;
#pragma warning disable VSTHRD114 // avoid returning null from a Task-returning method - by design, no execution loop backs this connection
    public Task? ExecuteTask => null;
#pragma warning restore VSTHRD114
    public IServerBase Server { get; }
    public IPAddress BoundIpAddress => IPAddress.Loopback;
    public Guid? AccountId { get; set; }
    public string Username { get; set; } = "";
    public IPlayerEntity? Player { get; set; }

    public bool IsClosed { get; private set; }

    public NullGameConnection(IServerBase server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Server = server;
    }

    public void Close(bool expected = true)
    {
        // No real socket to close - just mark the connection as closed
        IsClosed = true;
    }

    public void MarkExpectedClose()
    {
        // No real socket/read-loop backs this connection to consult this flag - a no-op is correct.
    }

    public void Send<T>(T packet) where T : IPacketSerializable
    {
        // Bots have no client to send packets to - discard silently
    }

    public Task StartAsync(CancellationToken token = default)
    {
        return Task.CompletedTask;
    }

    public bool HandleHandshake(GcHandshakeData handshake)
    {
        // Bots never handshake with a real client
        return true;
    }
}
