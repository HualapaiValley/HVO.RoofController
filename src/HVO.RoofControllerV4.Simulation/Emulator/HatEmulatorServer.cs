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
/// <para>A request whose connection has closed by the time its turn comes (the client timed out and gave up, or the link
/// was dropped) is not run: an I2C transfer cannot complete after the caller has given up on it, so a late access must not
/// land after the client's next ones.</para>
/// <para>That check alone is not proof: a connection's close and its successor's requests are separate TCP streams. So a
/// Hello that carries a client tag (<see cref="HatEmulatorProtocol.CreateHello(uint, int)"/>) fences that client's older
/// connections (#220, N3): it takes the access gate, and from then on a request of an older connection with the same tag
/// is never run, whenever its turn comes; its connection is closed instead. Connections without a tag, and other
/// clients', are not fenced. The controller's socket client
/// opens a new connection only after it has given up on the old one, so once a new connection's Hello is answered,
/// every write the client gave up on has either landed already (and the client's next read-back sees it) or never
/// will. That is the emulator transport's guarantee that a timed-out ON cannot energize a relay after a Stop that
/// reported success.</para>
/// </remarks>
/// <remarks>
/// The controls <see cref="Outage"/>, <see cref="ResponseDelay"/> and <see cref="DisconnectAll"/> break the link the way
/// a stopped emulator, a stalled one or a network drop would, for tests of the controller's behaviour. A connection that
/// sends no Hello within <see cref="HelloTimeout"/> is closed.
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
    private long _helloTimeoutTicks = TimeSpan.FromSeconds(5).Ticks;
    private long _writeHoldTicks;
    private long _fencedRequests;

    // The newest connection whose Hello carried each client tag.
    private readonly ConcurrentDictionary<int, long> _latestHello = new();
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

    /// <summary>
    /// How long each Write is held after it arrives and before it takes its turn, to model a write accepted by the
    /// transport but not yet carried out (a late ON, #220 N3). Zero by default. A held write is still fenced by a newer
    /// connection's Hello when its turn comes.
    /// </summary>
    public TimeSpan WriteHold
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _writeHoldTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Interlocked.Exchange(ref _writeHoldTicks, value.Ticks);
        }
    }

    /// <summary>Which writes <see cref="WriteHold"/> holds: every write when null (tests pick one, such as a relay ON).</summary>
    public Func<HatEmulatorFrame, bool>? HoldWhen { get; set; }

    /// <summary>Requests not run because a newer connection's Hello had fenced their connection.</summary>
    public long FencedRequests => Interlocked.Read(ref _fencedRequests);

    /// <summary>How long a new connection may take to send its Hello before it is closed. Five seconds by default.</summary>
    public TimeSpan HelloTimeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _helloTimeoutTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue));
            Interlocked.Exchange(ref _helloTimeoutTicks, value.Ticks);
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

        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            DisconnectAll();
            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The loop ends by itself on shutdown; a failure must not stop the shutdown or skip the handlers.
                    _logger.LogWarning(ex, "HAT emulator: the accept loop failed");
                }
            }

            // The accept loop has ended, so no handler is added after this snapshot. A handler that failed is logged by
            // its continuation.
            await Task.WhenAll(_handlers.Values).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _stopping.Dispose();
            _accessGate.Dispose();
        }
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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException
                && cancellationToken.IsCancellationRequested)
            {
                // A listener stopped by the shutdown throws InvalidOperationException, even with the token cancelled.
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

            var remote = RemoteOf(client);
            NetworkStream stream;
            try
            {
                // Before the connection is registered, while nothing else can dispose the client: GetStream racing a
                // Dispose (DisconnectAll) can throw ArgumentNullException.
                client.NoDelay = true;
                stream = client.GetStream();
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                // Reset by the peer before it could be served.
                client.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextConnectionId);
            _connections[id] = client;

            // Registered before the removal can run: a handler that ends at once still leaves _handlers.
            var handler = HandleAsync(id, client, stream, remote, cancellationToken);
            _handlers[id] = handler;
            _ = handler.ContinueWith(
                ended =>
                {
                    _handlers.TryRemove(id, out var _);
                    if (ended.Exception is { } failure)
                    {
                        // HandleAsync handles the connection's own errors; anything else is a defect to see.
                        _logger.LogWarning(failure.GetBaseException(), "HAT emulator: the handler for the connection from {Remote} failed", remote);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(long id, TcpClient client, NetworkStream stream, EndPoint? remote, CancellationToken cancellationToken)
    {
        // Leave the accept loop's thread at once: the loop starts the next accept without waiting for this connection.
        // DisconnectAll, an outage or the shutdown may close the connection at any point from here.
        await Task.Yield();
        var clientTag = 0;
        try
        {
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                hello.CancelAfter(HelloTimeout);
                try
                {
                    if (await HelloAsync(id, stream, hello.Token).ConfigureAwait(false) is not { } tag)
                    {
                        return;
                    }

                    clientTag = tag;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("HAT emulator: closed a connection from {Remote} that sent no Hello within {Timeout}", remote, HelloTimeout);
                    return;
                }
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

                if (frame.Kind == HatEmulatorFrameKind.Write && WriteHold is { } hold && hold > TimeSpan.Zero && (HoldWhen?.Invoke(frame) ?? true))
                {
                    await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
                }

                if (await ExecuteAsync(id, clientTag, frame, client, cancellationToken).ConfigureAwait(false) is not { } response)
                {
                    break;
                }

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
            await TryRefuseAsync(stream, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The connection closed or the server is stopping.
        }
        finally
        {
            _connections.TryRemove(id, out _);
            client.Dispose();
            _logger.LogInformation("HAT emulator: connection from {Remote} closed", remote);
        }
    }

    private static EndPoint? RemoteOf(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>True when the client has closed or reset the connection, or the server has dropped it.</summary>
    private static bool HasClosed(TcpClient client)
    {
        try
        {
            // Readable with nothing to read: the peer's FIN or RST. The client sends one request at a time, so a pending
            // request never counts as data here.
            return client.Client is not { } socket || (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>Answers the Hello; returns its client tag (0 for none), or null when the connection must close.</summary>
    private async Task<int?> HelloAsync(long id, NetworkStream stream, CancellationToken cancellationToken)
    {
        var hello = await HatEmulatorProtocol.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (hello is not { } frame)
        {
            return null;
        }

        if (HatEmulatorProtocol.CheckHello(frame) is { } problem)
        {
            _logger.LogWarning("HAT emulator: refused a connection: {Reason}", problem);
            await RefuseAsync(stream, frame.Sequence, problem, cancellationToken).ConfigureAwait(false);
            return null;
        }

        // The fence: from here no request of this client's older connections runs, whenever its turn comes. Under the
        // access gate, so an access already running finishes first and none of an older connection starts after.
        var tag = HatEmulatorProtocol.ReadClientTag(frame);
        if (tag != 0)
        {
            await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The newest connection wins, whichever Hello is answered last (a delayed Hello of an older connection
                // never points the fence back at it).
                _latestHello.AddOrUpdate(tag, id, (_, latest) => Math.Max(latest, id));
            }
            finally
            {
                _accessGate.Release();
            }
        }

        var settings = _target().ConnectionSettings;
        var reply = HatEmulatorProtocol.CreateHelloReply(frame.Sequence, settings.BusId, settings.DeviceAddress);
        await HatEmulatorProtocol.WriteFrameAsync(stream, reply, cancellationToken).ConfigureAwait(false);
        return tag;
    }

    /// <summary>
    /// Runs the access, or returns null without running it when the connection has closed while it waited its turn, or a
    /// newer connection's Hello has fenced it.
    /// </summary>
    private async Task<HatEmulatorFrame?> ExecuteAsync(long id, int clientTag, HatEmulatorFrame request, TcpClient client, CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clientTag != 0 && _latestHello.TryGetValue(clientTag, out var latest) && latest != id)
            {
                Interlocked.Increment(ref _fencedRequests);
                _logger.LogInformation("HAT emulator: a {Kind} from a connection a newer Hello fenced was not run", request.Kind);
                return null;
            }

            if (HasClosed(client))
            {
                return null;
            }

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

    private static async Task TryRefuseAsync(NetworkStream stream, string problem)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await RefuseAsync(stream, 0, problem, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Best effort: the peer may already be gone.
        }
    }
}
