using System.Net;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.Client;

/// <summary>Where a <see cref="RoofStatusFeed"/> is in its connection to the status hub.</summary>
public enum RoofStatusFeedState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,

    /// <summary>Making the first connection.</summary>
    Connecting,

    /// <summary>Connected; status arrives as it changes and at least once per heartbeat interval.</summary>
    Connected,

    /// <summary>The connection was lost or could not be made; trying again after a growing delay.</summary>
    Reconnecting,

    /// <summary>
    /// The controller refused the credential. The feed tries again when the credential changes, and otherwise after the
    /// longest reconnect delay.
    /// </summary>
    Unauthorized
}

/// <summary>A status message the feed applied, with what it replaced.</summary>
public sealed class RoofStatusReceivedEventArgs : EventArgs
{
    internal RoofStatusReceivedEventArgs(RoofStatusHubMessage message, RoofStatusHubMessage? previous)
    {
        Message = message;
        Previous = previous;
        IsNewInstance = previous is not null && !string.Equals(previous.InstanceId, message.InstanceId, StringComparison.Ordinal);
        SafetyAlert = RoofStatusRules.DetectSafetyAlert(previous?.Status, message.Status);
    }

    public RoofStatusHubMessage Message { get; }

    public RoofStatusResponse Status => Message.Status;

    /// <summary>The message this one replaced, or null for the first.</summary>
    public RoofStatusHubMessage? Previous { get; }

    /// <summary>True when the controller restarted since the previous message (its instance id changed).</summary>
    public bool IsNewInstance { get; }

    /// <summary>A safety state the roof entered with this message, if any.</summary>
    public RoofSafetyAlert? SafetyAlert { get; }
}

/// <summary>
/// Live status from the controller's status hub. It reconnects on its own with a growing delay, keeps the newest
/// snapshot (ordered by the hub's sequence within one controller instance), and says when that snapshot may be out of
/// date, so a client never shows a state it has not been told as current.
/// </summary>
/// <remarks>
/// <para>The view is stale from the moment the connection drops, or when no message arrives for
/// <see cref="RoofStatusFeedOptions.StaleAfter"/>; the next message makes it current again. The last snapshot is kept
/// while stale so a client can show it as the last known state, with <see cref="StaleSince"/>.</para>
/// <para>The feed only reads: commands, Stop included, go over REST. A kiosk connects with its device key alone, so a
/// PIN session that ends does not interrupt status. Events are raised on a thread-pool thread; a UI marshals them to its
/// own thread. Thread-safe.</para>
/// </remarks>
public sealed class RoofStatusFeed : IAsyncDisposable
{
    private readonly RoofControllerClient _client;
    private readonly RoofConnectionOptions _options;
    private readonly RoofStatusFeedOptions _feed;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ITimer _staleTimer;
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource _wake = NewSignal();
    private RoofStatusFeedState _state;
    private RoofStatusHubMessage? _current;
    private DateTimeOffset? _lastMessageUtc;
    private DateTimeOffset? _staleSince;
    private Exception? _lastError;
    private int _connectionCount;
    private long _connection;
    private bool _receivedOnConnection;
    private bool _disposed;

    internal RoofStatusFeed(RoofControllerClient client)
    {
        _client = client;
        _options = client.Options;
        _feed = _options.StatusFeed;
        _time = _options.TimeProvider;
        _logger = (_options.LoggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<RoofStatusFeed>();
        _staleTimer = _time.CreateTimer(_ => OnSilence(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _client.CredentialChanged += OnCredentialChanged;
    }

    /// <summary>Raised for each message that replaces the current snapshot.</summary>
    public event EventHandler<RoofStatusReceivedEventArgs>? StatusReceived;

    /// <summary>Raised when <see cref="State"/> changes, or the view becomes stale or current again.</summary>
    public event EventHandler? StateChanged;

    public RoofStatusFeedState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The newest message, or null before the first. Kept while the view is stale.</summary>
    public RoofStatusHubMessage? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The newest status snapshot, or null before the first.</summary>
    public RoofStatusResponse? Status => Current?.Status;

    /// <summary>When the last message arrived, by this client's clock.</summary>
    public DateTimeOffset? LastMessageUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastMessageUtc;
            }
        }
    }

    /// <summary>
    /// When the snapshot became possibly out of date: the connection dropped, or no message arrived in time. Null while
    /// it is current, and before the first snapshot.
    /// </summary>
    public DateTimeOffset? StaleSince
    {
        get
        {
            lock (_gate)
            {
                return _staleSince;
            }
        }
    }

    /// <summary>True when a snapshot is held but may be out of date. Show it as the last known state, not the current one.</summary>
    public bool IsStale => StaleSince is not null;

    /// <summary>Why the last connection failed or ended, or null while connected.</summary>
    public Exception? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>How many connections have been made since the feed was created.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connectionCount;
            }
        }
    }

    /// <summary>Starts connecting in the background. Does nothing when already started.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping is not null)
            {
                return;
            }

            _stopping = new CancellationTokenSource();
            var stopping = _stopping.Token;
            _loop = Task.Run(() => RunAsync(stopping), CancellationToken.None);
        }
    }

    /// <summary>Disconnects and stops reconnecting. The last snapshot is kept, and is stale.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task loop;
        lock (_gate)
        {
            stopping = _stopping;
            loop = _loop;
            _stopping = null;
        }

        if (stopping is null)
        {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        finally
        {
            stopping.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _client.CredentialChanged -= OnCredentialChanged;
        await StopAsync().ConfigureAwait(false);
        await _staleTimer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> may replace <paramref name="current"/>: the first message, a message from a
    /// new controller instance (a restart starts the sequence again), or a newer message from the same instance.
    /// </summary>
    public static bool ShouldApply(RoofStatusHubMessage? current, RoofStatusHubMessage candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return current is null
            || !string.Equals(current.InstanceId, candidate.InstanceId, StringComparison.Ordinal)
            || candidate.Sequence > current.Sequence;
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        SetState(RoofStatusFeedState.Connecting);
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            var wake = ResetWake();
            var unauthorized = false;
            HubConnection? connection = null;
            try
            {
                connection = Build(out var closed);
                await ConnectAsync(connection, stopping).ConfigureAwait(false);
                OnConnected();

                var stopped = NewSignal();
                Task ended;
                using (stopping.Register(() => stopped.TrySetResult()))
                {
                    ended = await Task.WhenAny(closed, wake.Task, stopped.Task).ConfigureAwait(false);
                }

                if (ended == closed)
                {
                    var error = await closed.ConfigureAwait(false);
                    _logger.LogInformation(error, "The status hub connection closed.");
                    SetError(error ?? new HttpRequestException("The controller closed the status connection."));
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                unauthorized = IsUnauthorized(ex);
                if (unauthorized)
                {
                    _client.Credential?.OnRefused(RoofCredentialUse.StatusHub);
                }

                _logger.Log(failures == 0 ? LogLevel.Information : LogLevel.Debug, ex, "Could not connect to the status hub.");
                SetError(ex);
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                // Only a connection that delivered status resets the delay: the controller sends the current snapshot as
                // soon as it registers a connection, so one closed before that (a connection limit, a revoked key) was
                // refused, and retrying it at once would only be refused again.
                if (OnDisconnected())
                {
                    failures = 0;
                }
            }

            if (stopping.IsCancellationRequested)
            {
                break;
            }

            if (wake.Task.IsCompleted)
            {
                // The credential changed: connect again at once with the new one.
                failures = 0;
                SetState(RoofStatusFeedState.Reconnecting);
                continue;
            }

            SetState(unauthorized ? RoofStatusFeedState.Unauthorized : RoofStatusFeedState.Reconnecting);
            var delay = unauthorized
                ? _feed.MaxReconnectDelay
                : RoofReconnectDelay.For(failures++, _feed.InitialReconnectDelay, _feed.MaxReconnectDelay, _feed.ReconnectJitter, Random.Shared.NextDouble());
            await Task.WhenAny(Task.Delay(delay, _time, stopping), wake.Task).ConfigureAwait(false);
        }

        SetState(RoofStatusFeedState.Stopped);
    }

    private async Task ConnectAsync(HubConnection connection, CancellationToken stopping)
    {
        using var timeout = new CancellationTokenSource(_feed.ConnectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);
        try
        {
            await connection.StartAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            throw new TimeoutException($"The status hub did not accept the connection within {_feed.ConnectTimeout.TotalSeconds:0.#} s.", ex);
        }
    }

    private HubConnection Build(out Task<Exception?> closed)
    {
        var headers = _client.Credential?.GetHeaders(RoofCredentialUse.StatusHub) ?? [];
        var hubUri = new Uri(_options.NormalizedBaseAddress, RoofStatusHubContract.Path.TrimStart('/'));
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUri, http =>
            {
                foreach (var header in headers)
                {
                    http.Headers[header.Key] = header.Value;
                }

                http.HttpMessageHandlerFactory = _ => _options.CreatePrimaryHandler();
                if (_options.WebSocketFactory is { } factory)
                {
                    http.WebSocketFactory = (context, cancellationToken) => factory(context.Uri, headers, cancellationToken);
                }
                else if (_options.ServerCertificateSha256 is { } pin)
                {
                    var validator = RoofCertificatePin.CreateValidator(pin);
                    http.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = validator;
                }
            })
            .AddJsonProtocol(json => json.PayloadSerializerOptions = RoofClientJson.Create());
        if (_options.LoggerFactory is { } loggerFactory)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton(loggerFactory));
        }

        var connection = builder.Build();
        var generation = Interlocked.Increment(ref _connection);
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            done.TrySetResult(error);
            return Task.CompletedTask;
        };
        connection.On<RoofStatusHubMessage>(RoofStatusHubContract.StatusMethod, message => OnMessage(message, generation));
        closed = done.Task;
        return connection;
    }

    private void OnMessage(RoofStatusHubMessage message, long generation)
    {
        if (message?.Status is null)
        {
            return;
        }

        RoofStatusReceivedEventArgs? received = null;
        bool wasStale;
        lock (_gate)
        {
            if (generation != Interlocked.Read(ref _connection))
            {
                return;
            }

            _lastMessageUtc = _time.GetUtcNow();
            _receivedOnConnection = true;
            wasStale = _staleSince is not null;
            _staleSince = null;
            _staleTimer.Change(_feed.StaleAfter, Timeout.InfiniteTimeSpan);
            if (ShouldApply(_current, message))
            {
                received = new RoofStatusReceivedEventArgs(message, _current);
                _current = message;
            }
        }

        if (received is not null)
        {
            StatusReceived?.Invoke(this, received);
        }

        if (wasStale)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnConnected()
    {
        lock (_gate)
        {
            // The first message can arrive before StartAsync returns, so what this connection received is left as it
            // is; OnDisconnected clears it for the next one.
            _connectionCount++;
            _lastError = null;
            _staleTimer.Change(_feed.StaleAfter, Timeout.InfiniteTimeSpan);
        }

        SetState(RoofStatusFeedState.Connected);
    }

    /// <summary>Ends the current connection's view. Returns true when that connection delivered at least one message.</summary>
    private bool OnDisconnected()
    {
        bool received;
        lock (_gate)
        {
            Interlocked.Increment(ref _connection);
            received = _receivedOnConnection;
            _receivedOnConnection = false;
            _staleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        MarkStale();
        return received;
    }

    private void OnSilence()
    {
        lock (_gate)
        {
            // A message may have arrived while this callback was queued.
            if (_lastMessageUtc is { } last && _time.GetUtcNow() - last < _feed.StaleAfter)
            {
                return;
            }
        }

        MarkStale();
    }

    private void MarkStale()
    {
        lock (_gate)
        {
            if (_current is null || _staleSince is not null)
            {
                return;
            }

            _staleSince = _time.GetUtcNow();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(RoofStatusFeedState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(Exception error)
    {
        lock (_gate)
        {
            _lastError = error;
        }
    }

    private TaskCompletionSource ResetWake()
    {
        var wake = NewSignal();
        Volatile.Write(ref _wake, wake);
        return wake;
    }

    private void OnCredentialChanged(object? sender, EventArgs e) => Volatile.Read(ref _wake).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsUnauthorized(Exception? ex) => ex switch
    {
        null => false,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsUnauthorized),
        _ => IsUnauthorized(ex.InnerException)
    };
}
