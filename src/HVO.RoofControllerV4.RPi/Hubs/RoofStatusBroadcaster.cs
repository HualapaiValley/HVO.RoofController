using System.Security.Claims;
using System.Threading.Channels;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.SignalR;

namespace HVO.RoofControllerV4.RPi.Hubs;

/// <summary>Delivers one status message to one status hub connection.</summary>
public interface IRoofStatusSender
{
    Task SendAsync(string connectionId, RoofStatusHubMessage message, CancellationToken cancellationToken);
}

/// <summary>Sends through the hub context: the call completes when the connection's transport has taken the message.</summary>
internal sealed class HubRoofStatusSender(IHubContext<RoofStatusHub> hub) : IRoofStatusSender
{
    public Task SendAsync(string connectionId, RoofStatusHubMessage message, CancellationToken cancellationToken)
        => hub.Clients.Client(connectionId).SendAsync(RoofStatusHubContract.StatusMethod, message, cancellationToken);
}

/// <summary>
/// Publishes the controller's status to the status hub's connections: every <see cref="IRoofControllerServiceV4.StatusChanged"/>
/// snapshot, the current snapshot when a connection opens, and a heartbeat snapshot whenever nothing was published for
/// <see cref="RoofStatusHubContract.HeartbeatInterval"/>.
/// </summary>
/// <remarks>
/// <para>Publishing never waits for a client. Each connection has its own pending slot, which holds only the newest
/// message it has not yet received, and its own send loop; a slow or stuck client delays only its own messages, and
/// the controller's status dispatcher only writes to the slots.</para>
/// <para>A snapshot older than the last one published (a lower <see cref="RoofStatusResponse.StatusVersion"/>, which a
/// heartbeat racing a change can produce) is not published, so no client sees the status go backwards. A change event
/// must be strictly newer: each change has its own version, so an event whose version was already published (by a
/// heartbeat or a connection that read the snapshot first) is an older copy of it.</para>
/// <para>A hub connection authenticates once, when it opens. Once every heartbeat interval, whether or not anything
/// was published, every connection's key is checked again and the connection is closed when that key was removed,
/// rotated or re-roled, as the console does with its sign-in cookie.</para>
/// </remarks>
public sealed class RoofStatusBroadcaster : IHostedService, IDisposable
{
    /// <summary>Most hub connections open at once; the controller refuses more.</summary>
    public const int DefaultMaxConnections = RoofStatusHubContract.MaxConnections;

    /// <summary>
    /// Most hub connections open at once with one API key, so one client that leaks connections (or one leaked key)
    /// cannot take every slot from the kiosk and the other UIs.
    /// </summary>
    public const int DefaultMaxConnectionsPerKey = RoofStatusHubContract.MaxConnectionsPerKey;

    private readonly IRoofControllerServiceV4 _controller;
    private readonly IRoofStatusSender _sender;
    private readonly RoofApiKeyStore _keyStore;
    private readonly TimeProvider _time;
    private readonly ILogger<RoofStatusBroadcaster> _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxConnections;
    private readonly int _maxConnectionsPerKey;
    private readonly object _gate = new();
    private readonly Dictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private RoofStatusHubMessage? _latest;
    private long _lastPublishTimestamp;
    private Task _heartbeat = Task.CompletedTask;
    private bool _started;

    public RoofStatusBroadcaster(
        IRoofControllerServiceV4 controller,
        IRoofStatusSender sender,
        RoofApiKeyStore keyStore,
        TimeProvider time,
        ILogger<RoofStatusBroadcaster> logger)
        : this(controller, sender, keyStore, time, logger, RoofStatusHubContract.HeartbeatInterval, DefaultMaxConnections, DefaultMaxConnectionsPerKey)
    {
    }

    internal RoofStatusBroadcaster(
        IRoofControllerServiceV4 controller,
        IRoofStatusSender sender,
        RoofApiKeyStore keyStore,
        TimeProvider time,
        ILogger<RoofStatusBroadcaster> logger,
        TimeSpan heartbeatInterval,
        int maxConnections,
        int maxConnectionsPerKey = DefaultMaxConnectionsPerKey)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnectionsPerKey, 1);
        _heartbeatInterval = heartbeatInterval;
        _maxConnections = maxConnections;
        _maxConnectionsPerKey = maxConnectionsPerKey;
    }

    /// <summary>Identifies this process's message stream (<see cref="RoofStatusHubMessage.InstanceId"/>).</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The last message published, or null before the first.</summary>
    public RoofStatusHubMessage? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Hub connections now registered.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            // The first heartbeat is due one interval from now, not at once.
            _lastPublishTimestamp = _time.GetTimestamp();
        }

        _controller.StatusChanged += OnStatusChanged;
        _heartbeat = Task.Run(() => HeartbeatAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _controller.StatusChanged -= OnStatusChanged;
        List<Subscriber> subscribers;
        lock (_gate)
        {
            _stopping.Cancel();
            subscribers = _subscribers.Values.ToList();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Stop();
        }

        try
        {
            await Task.WhenAll(subscribers.Select(s => s.Completion).Append(_heartbeat)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown timeout ran out; the send loops end with their cancelled sends.
        }
    }

    /// <inheritdoc cref="TryRegister(string, ClaimsPrincipal?, Action, out string?)"/>
    public bool TryRegister(string connectionId, ClaimsPrincipal? user, Action abort)
        => TryRegister(connectionId, user, abort, out _);

    /// <summary>
    /// Registers a hub connection and publishes the current snapshot, so the connection's first message is current.
    /// Returns false, registering nothing, when a connection limit (<see cref="DefaultMaxConnections"/> in all,
    /// <see cref="DefaultMaxConnectionsPerKey"/> for one key) is reached, the connection is already registered, or the
    /// broadcaster is stopping.
    /// </summary>
    /// <param name="abort">Closes the connection (used when its key is revoked).</param>
    /// <param name="refusal">Why the connection was refused, for the client; null when it was registered.</param>
    public bool TryRegister(string connectionId, ClaimsPrincipal? user, Action abort, out string? refusal)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentNullException.ThrowIfNull(abort);

        var keyId = user?.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value;
        var status = _controller.GetCurrentStatusSnapshot();
        int limit;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested || _subscribers.ContainsKey(connectionId))
            {
                refusal = "The controller is not accepting status connections.";
                return false;
            }

            if (_subscribers.Count >= _maxConnections)
            {
                limit = _maxConnections;
                refusal = "The controller is not accepting more status connections.";
            }
            else if (keyId is not null && _subscribers.Values.Count(s => s.KeyId == keyId) >= _maxConnectionsPerKey)
            {
                limit = _maxConnectionsPerKey;
                refusal = "The controller is not accepting more status connections for this key.";
            }
            else
            {
                var subscriber = new Subscriber(connectionId, user, keyId, abort, _sender, _logger);
                _subscribers.Add(connectionId, subscriber);
                if (!Publish_NoLock(status, fromChange: false))
                {
                    subscriber.Offer(_latest!);
                }

                refusal = null;
                limit = 0;
            }
        }

        // Logged outside the lock: the status dispatcher takes it for every change.
        if (refusal is not null)
        {
            _logger.LogWarning(
                "Status hub connection {ConnectionId} ({User}) refused: {Refusal} ({Limit} are already open).",
                connectionId, user?.Identity?.Name, refusal, limit);
            return false;
        }

        _logger.LogDebug("Status hub connection {ConnectionId} ({User}) registered.", connectionId, user?.Identity?.Name);
        return true;
    }

    /// <summary>Removes a hub connection and cancels any send still in progress to it.</summary>
    public void Unregister(string connectionId)
    {
        Subscriber? subscriber;
        lock (_gate)
        {
            if (!_subscribers.Remove(connectionId, out subscriber))
            {
                return;
            }
        }

        subscriber.Stop();
        _logger.LogDebug("Status hub connection {ConnectionId} unregistered.", connectionId);
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        lock (_gate)
        {
            _stopping.Cancel();
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Stop();
            }

            _subscribers.Clear();
        }
    }

    /// <summary>Runs on the controller's status dispatcher: only writes to the pending slots, never waits.</summary>
    private void OnStatusChanged(object? sender, RoofStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            Publish_NoLock(e.Status, fromChange: true);
        }
    }

    /// <param name="fromChange">
    /// True for a change event, which must be strictly newer than the last message: every change has its own version,
    /// so one whose version was already published is an older copy (read by a heartbeat or a new connection while the
    /// event waited on the dispatcher). A heartbeat or connect snapshot may repeat the version, as a fresh copy.
    /// </param>
    private bool Publish_NoLock(RoofStatusResponse status, bool fromChange)
    {
        if (_latest is not null
            && (status.StatusVersion < _latest.Status.StatusVersion
                || (fromChange && status.StatusVersion == _latest.Status.StatusVersion)))
        {
            return false;
        }

        _latest = new RoofStatusHubMessage(status, (_latest?.Sequence ?? 0) + 1, _time.GetUtcNow(), InstanceId);
        _lastPublishTimestamp = _time.GetTimestamp();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Offer(_latest);
        }

        return true;
    }

    /// <summary>
    /// Sends a heartbeat one interval after the last message, and checks the connections' keys once every interval on
    /// its own schedule: while the roof moves, a change is published about every second and the heartbeat is never due,
    /// but a revoked key's connection must still close.
    /// </summary>
    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var lastKeyCheck = _time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan heartbeatDue;
            lock (_gate)
            {
                heartbeatDue = _heartbeatInterval - _time.GetElapsedTime(_lastPublishTimestamp);
            }

            var keyCheckDue = _heartbeatInterval - _time.GetElapsedTime(lastKeyCheck);
            if (keyCheckDue <= TimeSpan.Zero)
            {
                lastKeyCheck = _time.GetTimestamp();
                try
                {
                    CloseRevokedConnections();
                }
                catch (Exception ex)
                {
                    // The key store could not be read; the next interval tries again.
                    _logger.LogWarning(ex, "Status hub key check failed.");
                }

                keyCheckDue = _heartbeatInterval;
            }

            var wait = heartbeatDue < keyCheckDue ? heartbeatDue : keyCheckDue;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                SendHeartbeat();
            }
            catch (Exception ex)
            {
                // The controller could not give a snapshot; the next interval tries again. Clients see no message and
                // mark their view stale.
                _logger.LogWarning(ex, "Status hub heartbeat failed.");
                lock (_gate)
                {
                    _lastPublishTimestamp = _time.GetTimestamp();
                }
            }
        }
    }

    private void SendHeartbeat()
    {
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                // Nobody to send to: wait another interval rather than read the controller for nothing.
                _lastPublishTimestamp = _time.GetTimestamp();
                return;
            }
        }

        var status = _controller.GetCurrentStatusSnapshot();
        lock (_gate)
        {
            Publish_NoLock(status, fromChange: false);
        }
    }

    private void CloseRevokedConnections()
    {
        List<Subscriber> revoked;
        lock (_gate)
        {
            revoked = _subscribers.Values.Where(s => !RoofConsoleAuthenticationStateProvider.IsStillValid(_keyStore, s.User)).ToList();
            foreach (var subscriber in revoked)
            {
                _subscribers.Remove(subscriber.ConnectionId);
            }
        }

        foreach (var subscriber in revoked)
        {
            _logger.LogInformation(
                "Status hub connection {ConnectionId} closed: the key '{KeyName}' that opened it was removed, rotated or re-roled.",
                subscriber.ConnectionId, subscriber.User?.Identity?.Name);
            subscriber.Stop();
            subscriber.Abort();
        }
    }

    /// <summary>One connection: a pending slot that keeps only the newest message, and the loop that sends from it.</summary>
    private sealed class Subscriber
    {
        private readonly Channel<RoofStatusHubMessage> _pending = Channel.CreateBounded<RoofStatusHubMessage>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        private readonly CancellationTokenSource _cancel = new();
        private readonly Action _abort;

        public Subscriber(string connectionId, ClaimsPrincipal? user, string? keyId, Action abort, IRoofStatusSender sender, ILogger logger)
        {
            ConnectionId = connectionId;
            User = user;
            KeyId = keyId;
            _abort = abort;
            // Its own pool task: a sender that blocks its caller holds up only this connection.
            Completion = Task.Run(() => SendLoopAsync(sender, logger), CancellationToken.None);
        }

        public string ConnectionId { get; }

        public ClaimsPrincipal? User { get; }

        /// <summary>The identifier of the API key that opened the connection, or null.</summary>
        public string? KeyId { get; }

        /// <summary>Completes when the send loop ends; never faults.</summary>
        public Task Completion { get; }

        /// <summary>Never waits: a full slot drops the message it holds for the newer one.</summary>
        public void Offer(RoofStatusHubMessage message) => _pending.Writer.TryWrite(message);

        public void Stop()
        {
            _pending.Writer.TryComplete();
            _cancel.Cancel();
        }

        public void Abort() => _abort();

        private async Task SendLoopAsync(IRoofStatusSender sender, ILogger logger)
        {
            try
            {
                await foreach (var message in _pending.Reader.ReadAllAsync(_cancel.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await sender.SendAsync(ConnectionId, message, _cancel.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Status message {Sequence} to hub connection {ConnectionId} failed.", message.Sequence, ConnectionId);
                    }
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                // Unregistered while waiting for the next message.
            }
        }
    }
}
