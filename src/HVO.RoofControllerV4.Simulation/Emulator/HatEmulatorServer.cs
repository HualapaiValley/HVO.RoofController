using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HVO.Iot.Devices.Abstractions;
using HVO.RoofControllerV4.Common.Emulation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.Simulation.Emulator;

/// <summary>
/// Serves an <see cref="II2cRegisterClient"/> (the emulated HAT) over TCP with <see cref="HatEmulatorProtocol"/>, for the
/// controller's socket register client. Each connection must send a Hello first; then each Read or Write runs against
/// the client the target returns at that moment, one access at a time across all connections. An
/// <see cref="IOException"/> from the client (an injected bus failure, a powered-down HAT) is answered with IoError and
/// the connection stays up; a malformed request is answered with ProtocolError and the connection closes.
/// </summary>
/// <remarks>
/// The controls <see cref="Outage"/>, <see cref="ResponseDelay"/> and <see cref="DisconnectAll"/> break the link the way
/// a stopped emulator, a stalled one or a network drop would, for tests of the controller's behaviour.
/// </remarks>
public sealed class HatEmulatorServer : IAsyncDisposable
{
    private readonly Func<II2cRegisterClient> _target;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<long, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private Task? _acceptLoop;
    private long _nextConnectionId;
    private long _accepted;
    private long _requests;
    private long _responseDelayTicks;
    private volatile bool _outage;
    private int _disposed;

    /// <param name="target">Returns the client that answers each access (read again for every access, so a reset takes effect at once).</param>
    /// <param name="endpoint">Where to listen; port 0 picks a free port (see <see cref="LocalEndPoint"/>).</param>
    /// <param name="logger">Receives connection changes.</param>
    public HatEmulatorServer(Func<II2cRegisterClient> target, IPEndPoint endpoint, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(endpoint);
        _target = target;
        _logger = logger ?? NullLogger.Instance;
        _listener = new TcpListener(endpoint);
    }

    /// <summary>The bound endpoint, once <see cref="Start"/> has run.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>
    /// While true the emulator is unreachable: open connections are closed, a new connection is closed as soon as it is
    /// accepted, and a request in progress gets no response.
    /// </summary>
    public bool Outage
    {
        get => _outage;
        set
        {
            _outage = value;
            if (value)
            {
                DisconnectAll();
            }
        }
    }

    /// <summary>Delay before each response, to model a slow or stalled emulator. Zero by default.</summary>
    public TimeSpan ResponseDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _responseDelayTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Interlocked.Exchange(ref _responseDelayTicks, value.Ticks);
        }
    }

    /// <summary>Connections accepted so far (including those closed at once during an outage).</summary>
    public long AcceptedConnections => Interlocked.Read(ref _accepted);

    /// <summary>Connections open now.</summary>
    public int OpenConnections => _connections.Count;

    /// <summary>Read and Write requests served so far.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Starts listening and accepting connections.</summary>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
        _logger.LogInformation("HAT emulator listening on {Endpoint}", LocalEndPoint);
    }

    /// <summary>Closes every open connection, as a network drop would. New connections are still accepted.</summary>
    public void DisconnectAll()
    {
        foreach (var (id, client) in _connections)
        {
            if (_connections.TryRemove(id, out _))
            {
                client.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        DisconnectAll();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        await Task.WhenAll(_handlers.Values).ConfigureAwait(false);
        _stopping.Dispose();
        _accessGate.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("HAT emulator accept failed: {Error}", ex.SocketErrorCode);
                continue;
            }

            Interlocked.Increment(ref _accepted);
            if (_outage)
            {
                client.Dispose();
                continue;
            }

            client.NoDelay = true;
            var id = Interlocked.Increment(ref _nextConnectionId);
            _connections[id] = client;
            _handlers[id] = HandleAsync(id, client, cancellationToken);
        }
    }

    private async Task HandleAsync(long id, TcpClient client, CancellationToken cancellationToken)
    {
        // Leave the accept loop's thread at once: the loop starts the next accept without waiting for this connection.
        await Task.Yield();
        var remote = client.Client.RemoteEndPoint;
        try
        {
            var stream = client.GetStream();
            if (!await HelloAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _logger.LogInformation("HAT emulator: controller connected from {Remote}", remote);
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await HatEmulatorProtocol.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is not { } frame)
                {
                    break;
                }

                if (HatEmulatorProtocol.CheckAccess(frame) is { } problem)
                {
                    await RefuseAsync(stream, frame.Sequence, problem, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var response = await ExecuteAsync(frame, cancellationToken).ConfigureAwait(false);
                if (ResponseDelay is { } delay && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                if (_outage)
                {
                    break;
                }

                await HatEmulatorProtocol.WriteFrameAsync(stream, response, cancellationToken).ConfigureAwait(false);
                if (response.Kind == HatEmulatorFrameKind.ProtocolError)
                {
                    break;
                }
            }
        }
        catch (HatEmulatorProtocolException ex)
        {
            _logger.LogWarning("HAT emulator: malformed frame from {Remote}: {Reason}", remote, ex.Message);
            await TryRefuseAsync(client, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection closed or the server is stopping.
        }
        finally
        {
            _connections.TryRemove(id, out _);
            client.Dispose();
            _handlers.TryRemove(id, out _);
            _logger.LogInformation("HAT emulator: connection from {Remote} closed", remote);
        }
    }

    private async Task<bool> HelloAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var hello = await HatEmulatorProtocol.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (hello is not { } frame)
        {
            return false;
        }

        if (HatEmulatorProtocol.CheckHello(frame) is { } problem)
        {
            _logger.LogWarning("HAT emulator: refused a connection: {Reason}", problem);
            await RefuseAsync(stream, frame.Sequence, problem, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var settings = _target().ConnectionSettings;
        var reply = HatEmulatorProtocol.CreateHelloReply(frame.Sequence, settings.BusId, settings.DeviceAddress);
        await HatEmulatorProtocol.WriteFrameAsync(stream, reply, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<HatEmulatorFrame> ExecuteAsync(HatEmulatorFrame request, CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _requests);
            var target = _target();
            if (request.Kind == HatEmulatorFrameKind.Read)
            {
                var values = new byte[request.Count];
                target.ReadBlock(request.Register, values);
                return new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence, request.Register, request.Count, values);
            }

            target.WriteBlock(request.Register, request.Payload.Span);
            return new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence, request.Register, request.Count, ReadOnlyMemory<byte>.Empty);
        }
        catch (IOException ex)
        {
            return HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.IoError, request.Sequence, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            // The session replaced the plant between reading the target and the access.
            return HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.IoError, request.Sequence, "The HAT emulator is resetting.");
        }
        catch (ArgumentException ex)
        {
            return HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, request.Sequence, ex.Message);
        }
        finally
        {
            _accessGate.Release();
        }
    }

    private static async Task RefuseAsync(NetworkStream stream, uint sequence, string problem, CancellationToken cancellationToken)
    {
        var refusal = HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, sequence, problem);
        await HatEmulatorProtocol.WriteFrameAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryRefuseAsync(TcpClient client, string problem)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await RefuseAsync(client.GetStream(), 0, problem, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Best effort: the peer may already be gone.
        }
    }
}
