using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Core.Timekeeping;
using QuantumCore.API.Game.Types;
using QuantumCore.API.PluginTypes;
using QuantumCore.Core.Packets;
using QuantumCore.Core.Utils;
using QuantumCore.Extensions;
using QuantumCore.Networking;

namespace QuantumCore.Core.Networking;

public abstract class Connection : BackgroundService, IConnection
{
    private readonly ILogger _logger;
    private readonly PluginExecutor _pluginExecutor;
    private readonly ConcurrentQueue<object> _packetsToSend = new();
    private readonly IPacketReader _packetReader;

    private TcpClient? _client;
    private Stream? _stream;
    private ServerTimestamp _lastHandshakeTime;
    private CancellationTokenSource? _cts;

    // Guards Close() against running twice for the same connection - see the comment on Close() itself
    // for why this was a real, live-hit bug, not just theoretical hardening.
    private int _closed;

    // Set by MarkExpectedClose() when THIS server deliberately tells the client to disconnect and
    // reconnect (currently only PlayerEntity.Warp()'s cross/same-map warp packet). ExecuteAsync's read
    // loop below sees the resulting socket close purely as "the stream ended" - it has no way to tell a
    // deliberate server-initiated disconnect apart from the player just closing the game, so it always
    // called Close(false) ("unexpected"), which makes GameConnection.OnCloseAsync take its heavy path
    // (CalculatePlayedTimeAsync + a full second PersistAsync). Warp() already does its OWN explicit
    // blocking persist right before sending the warp packet - a warp-triggered reconnect running that
    // heavy path AGAIN a moment later, on a connection that's mid-teardown, was live-confirmed as a real
    // (if intermittent) cause of "Cannot access a disposed context instance" and the client sometimes
    // needing a second manual reconnect after breaking a Devil Tower floor trigger.
    private volatile bool _expectedClose;

    public void MarkExpectedClose() => _expectedClose = true;

    public IPAddress BoundIpAddress { get; private set; } = IPAddress.Any;

    public Guid Id { get; }
    public uint Handshake { get; private set; }
    public bool Handshaking { get; private set; }
    public EPhase Phase { get; set; }

    protected Connection(ILogger logger, PluginExecutor pluginExecutor, IPacketReader packetReader)
    {
        _logger = logger;
        _pluginExecutor = pluginExecutor;
        _packetReader = packetReader;
        Id = Guid.NewGuid();
    }

    public void Init(TcpClient client)
    {
        _client = client;
        BoundIpAddress = ((IPEndPoint) _client.Client.LocalEndPoint!).Address;
        _cts = new CancellationTokenSource();
        _ = Task.Factory.StartNew(SendPacketsWhenAvailableAsync, _cts.Token, TaskCreationOptions.LongRunning,
            TaskScheduler.Current);
    }

    protected abstract void OnHandshakeFinished();

    protected abstract Task OnCloseAsync(bool expected = true);

    protected abstract Task OnReceiveAsync(IPacketSerializable packet);

    protected abstract ServerClock GetClock();

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_client is null)
        {
            _logger.LogCritical("Cannot execute when client is null");
            return;
        }

        _logger.LogInformation("New connection {ConnectionId} from {RemoteEndPoint}", Id,
            _client.Client.RemoteEndPoint?.ToString());

        _stream = _client.GetStream();
        StartHandshake();

        try
        {
            await foreach (var packet in _packetReader.EnumerateAsync(_stream, stoppingToken))
            {
                // _logger.LogDebug(" IN: {Type} {Data}", packet.GetType(), JsonSerializer.Serialize(packet));
                await _pluginExecutor.ExecutePluginsAsync<IPacketOperationListener>(_logger,
                    x => x.OnPrePacketReceivedAsync(packet, Array.Empty<byte>(), stoppingToken));

                await OnReceiveAsync((IPacketSerializable) packet);

                await _pluginExecutor.ExecutePluginsAsync<IPacketOperationListener>(_logger,
                    x => x.OnPostPacketReceivedAsync(packet, Array.Empty<byte>(), stoppingToken));
            }
        }
        catch (IOException e)
        {
            _logger.LogDebug(e, "Connection was closed. Probably by the other party");
            Close(_expectedClose);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown, don't log as error
            Close(_expectedClose);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to read from stream");
            Close(_expectedClose);
        }

        Close(_expectedClose);
    }

    public void Close(bool expected = true)
    {
        // ExecuteAsync's read loop below calls Close(false) from inside a catch block AND unconditionally
        // right after the try/catch (no `return` after the catch's own call) - meaning a normal disconnect
        // (any read exception, which is the ordinary way a closed socket surfaces) ran this method's full
        // body TWICE for the same connection. The second pass re-entered GameConnection.OnCloseAsync with
        // the same still-non-null Player and re-ran the full despawn/persist chain on an entity (and DB
        // scope) the first pass had already torn down - live-confirmed as the real cause of a Warp-
        // triggered reconnect sometimes dying with "Cannot access a disposed context instance"
        // (SqliteGameDbContext) and the new connection's first packet then failing with "Cannot move
        // player that does not exist". Idempotent guard instead of hunting down/fixing every call site.
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _cts?.Cancel();
        _client?.Close();

        // Deliberately BLOCKING here (.GetAwaiter().GetResult(), not the fire-and-forget `_ =` this started
        // as) - same reasoning/pattern already applied to PlayerEntity.Warp()'s persist call. For an
        // *unexpected* disconnect, OnCloseAsync(false) chains into World.DespawnPlayerAsync ->
        // PlayerEntity.OnDespawnAsync -> PersistAsync, i.e. the player's actual DB save for that
        // disconnect. Fire-and-forget here meant nothing in this method (or its caller, e.g. the read
        // loop in ExecuteAsync below) ever waited for that save to land: the connection/scope teardown
        // that follows could race it, and any exception anywhere in that chain was completely silent
        // (unobserved task exception - no log, no visibility). Net effect: a player could reconnect at an
        // earlier position than where they actually disconnected, with zero trace of why in the logs -
        // this is what "reconnected further back than where I disconnected" turned out to be. Blocking
        // guarantees the save (or a now-visible failure) completes before Close() returns; no captured
        // SynchronizationContext in this host, so this can't deadlock the same way it could in classic
        // ASP.NET/WinForms code.
#pragma warning disable VSTHRD002 // use await - TODO, same pattern as PlayerEntity.Warp()
        OnCloseAsync(expected).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client?.Dispose();
            _stream?.Dispose();
            _cts?.Dispose();
        }
    }

    public override sealed void Dispose()
    {
        Dispose(true);
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Send<T>(T packet) where T : IPacketSerializable
    {
        _packetsToSend.Enqueue(packet);
    }

    private async Task SendPacketsWhenAvailableAsync()
    {
        if (_client?.Connected != true)
        {
            _logger.LogWarning("Tried to send data to a closed connection");
            return;
        }

        // Was: dequeue exactly one packet, then await WriteAsync + FlushAsync + two plugin-hook round
        // trips for THAT packet alone, before even looking at the next one. Fine for the steady trickle
        // of single packets this loop normally sees, but a burst - initial map entry revealing everyone
        // already in view, or a player crossing into a dense area - can queue 100+ packets (2 per
        // revealed entity) within milliseconds. Live-measured via qcx_reveal_timing.log (server) vs
        // qcx_create_profile.log (client): a 68-entity burst that the server revealed in 21ms took the
        // client ~14 REAL seconds to finish receiving/creating, entirely explained by this loop sending
        // those ~136 packets one full async round trip at a time - looking exactly like "monsters spawn
        // in slowly" even though the server had already decided to reveal everyone at once. Fix: drain
        // whatever's queued RIGHT NOW into one buffer and do a single WriteAsync+FlushAsync for the
        // whole batch, instead of one pair per packet. Per-packet plugin hooks and debug logging are
        // still run for every packet - only the actual socket I/O is coalesced.
        var batch = new List<(object Packet, byte[] Bytes, int Size)>();

        while (_cts?.IsCancellationRequested != true)
        {
            try
            {
                if (_packetsToSend.IsEmpty)
                {
                    await Task.Delay(1).ConfigureAwait(false); // wait at least 1ms
                    continue;
                }

                batch.Clear();
                var totalSize = 0;
                // Cap how much we drain in one go so a pathologically large backlog doesn't demand one
                // enormous contiguous buffer - the loop just comes straight back for the rest, no delay.
                const int MAX_BATCH_PACKETS = 512;
                while (batch.Count < MAX_BATCH_PACKETS && _packetsToSend.TryDequeue(out var queued))
                {
                    var packet = (IPacketSerializable) queued;
                    var size = packet.GetSize();
                    var bytes = ArrayPool<byte>.Shared.Rent(size);
                    Array.Clear(bytes, 0, size);
                    packet.Serialize(bytes);
                    batch.Add((queued, bytes, size));
                    totalSize += size;
                }

                // TEMP DIAGNOSTIC - "new mobs only appear closer and closer over time while standing
                // still" investigation. If _packetsToSend is backing up faster than this loop can drain
                // it, the remaining queue depth right after a drain should trend upward over the session
                // - that would mean newly-decided reveals sit queued for longer and longer the longer the
                // session runs, regardless of distance, which could look exactly like this.
                if (batch.Count > 5)
                {
                    try
                    {
                        await System.IO.File.AppendAllTextAsync("C:\\qcx_send_queue.log",
                                $"[{DateTime.UtcNow:HH:mm:ss.fff}] batch={batch.Count} totalSize={totalSize} remainingQueued={_packetsToSend.Count}\n")
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // best effort
                    }
                }

                var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
                try
                {
                    if (_stream is null)
                    {
                        if (_cts is not null)
                        {
                            await _cts.CancelAsync();
                        }

                        _logger.LogCritical("Stream unexpectedly became null. This shouldn't happen");
                        break;
                    }

                    try
                    {
                        var offset = 0;
                        foreach (var (obj, bytes, size) in batch)
                        {
                            await _pluginExecutor
                                .ExecutePluginsAsync<IPacketOperationListener>(_logger,
                                    x => x.OnPrePacketSentAsync(obj, CancellationToken.None))
                                .ConfigureAwait(false);
                            Buffer.BlockCopy(bytes, 0, buffer, offset, size);
                            offset += size;
                        }

                        await _stream.WriteAsync(buffer.AsMemory(0, totalSize)).ConfigureAwait(false);
                        await _stream.FlushAsync().ConfigureAwait(false);

                        foreach (var (obj, bytes, size) in batch)
                        {
                            await _pluginExecutor.ExecutePluginsAsync<IPacketOperationListener>(_logger,
                                    x => x.OnPostPacketSentAsync(obj, bytes, CancellationToken.None))
                                .ConfigureAwait(false);

                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug("OUT: {Type} => {Packet} (0x{Bytes})", obj.GetType(),
#pragma warning disable VSTHRD103 // use async overload - doesn't work here
                                    JsonSerializer.Serialize(obj),
#pragma warning restore VSTHRD103
                                    string.Join("", bytes.AsSpan(0, size).ToArray().Select(x => x.ToString("X2"))));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to send packet batch ({Count} packets)", batch.Count);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    foreach (var (_, bytes, _) in batch)
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                }
            }
            catch (SocketException)
            {
                // connection closed. Ignore
                break;
            }
            catch (IOException)
            {
                // broken pipe / connection reset. Ignore
                break;
            }
        }
    }

    public void StartHandshake()
    {
        if (Handshaking)
        {
            _logger.LogDebug("Already handshaking");
            return;
        }

        // Generate random handshake and start the handshaking
        Handshake = CoreRandom.GenerateUInt32();
        Handshaking = true;
        this.SetPhase(EPhase.HANDSHAKE);
        SendHandshake(GetClock().Now, TimeSpan.Zero);
    }

    public bool HandleHandshake(GcHandshakeData handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        if (!Handshaking)
        {
            // We wasn't handshaking!
            _logger.LogInformation("Received handshake while not handshaking!");
            _client?.Close();
            return false;
        }

        if (handshake.Handshake != Handshake)
        {
            // We received a wrong handshake
            _logger.LogInformation("Received wrong handshake ({Handshake} != {HandshakeHandshake})", Handshake,
                handshake.Handshake);
            _client?.Close();
            return false;
        }

        var clock = GetClock();
        var nowTimestamp = clock.Now;
        var uptime = clock.ElapsedAt(nowTimestamp);
        var difference = uptime - (handshake.Time + handshake.Delta);
        if (difference >= TimeSpan.Zero && difference <= TimeSpan.FromMilliseconds(50))
        {
            // if we difference is less than or equal to 50ms the handshake is done and client time is synced enough
            _logger.LogInformation("Handshake done {ConnectionId}", Id);
            Handshaking = false;

            OnHandshakeFinished();
        }
        else
        {
            // calculate new delta
            var delta = (uptime - handshake.Time) / 2;
            if (delta < TimeSpan.Zero)
            {
                delta = (clock.ElapsedAt(nowTimestamp) - clock.ElapsedAt(_lastHandshakeTime)) / 2;
                _logger.LogDebug("Delta is too low, retry with last send time");
            }

            SendHandshake(nowTimestamp, delta);
        }

        return true;
    }

    private void SendHandshake(ServerTimestamp time, TimeSpan delta)
    {
        _lastHandshakeTime = time;
        var uptime = GetClock().ElapsedAt(time);
        Send(new GcHandshake(Handshake, (uint) uptime.TotalMilliseconds, (uint) delta.TotalMilliseconds));
    }
}