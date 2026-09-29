using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>How much a notice matters: it colours the notice.</summary>
public enum WebRoofNoticeLevel
{
    Info,
    Warning,
    Danger
}

/// <summary>Something the page tells the person: the answer to a command, or what the status feed reported.</summary>
public sealed record WebRoofNotice(string Text, WebRoofNoticeLevel Level, DateTimeOffset At);

/// <summary>What one page shows of the roof, at one moment. A new view replaces it on every change.</summary>
public sealed record WebRoofView
{
    /// <summary>The view before the page's live connection started the console: nothing is offered yet.</summary>
    public static WebRoofView NotStarted { get; } = new()
    {
        OpenBlock = WebRoofConsole.NotStarted,
        CloseBlock = WebRoofConsole.NotStarted,
        ClearFaultBlock = WebRoofConsole.NotStarted
    };

    /// <summary>False until the page's live connection has started the console (never while prerendering).</summary>
    public bool IsStarted { get; init; }

    /// <summary>True while the page has the person's open session.</summary>
    public bool HasSession { get; init; }

    /// <summary>True when the person's role allows Open, Close and Clear fault (the controller still decides).</summary>
    public bool CanOperate { get; init; }

    /// <summary>The newest status, or null before the first.</summary>
    public RoofStatusResponse? Status { get; init; }

    public RoofStatusFeedState FeedState { get; init; }

    /// <summary>"live", "STALE", "connecting", "reconnecting", "refused" or "not connected".</summary>
    public string FeedLabel { get; init; } = "not connected";

    /// <summary>
    /// When the status shown may have stopped being the roof's: the feed went quiet or dropped, or the status is a single
    /// read while the feed is not live. Null while the status is live, and before the first.
    /// </summary>
    public DateTimeOffset? StaleSince { get; init; }

    /// <summary>True when there is no status, or the one shown is only the last known state.</summary>
    public bool IsStale => Status is null || StaleSince is not null;

    /// <summary>Why the status is missing or stale, or null while it is live.</summary>
    public string? FeedBanner { get; init; }

    /// <summary>True when the controller refused the session's status feed.</summary>
    public bool FeedRefused { get; init; }

    /// <summary>The command on its way ("Sending Open…"), or null.</summary>
    public string? Busy { get; init; }

    public bool CommandInFlight => Busy is not null;

    /// <summary>Why Open is not offered; null when it is.</summary>
    public string? OpenBlock { get; init; }

    /// <summary>Why Close is not offered; null when it is.</summary>
    public string? CloseBlock { get; init; }

    /// <summary>Why Clear fault is not offered; null when it is.</summary>
    public string? ClearFaultBlock { get; init; }

    /// <summary>True while this page renews the operator lease of a motion it started.</summary>
    public bool HoldsLease { get; init; }

    /// <summary>The newest notices, newest first.</summary>
    public IReadOnlyList<WebRoofNotice> Notices { get; init; } = [];
}

/// <summary>
/// The roof, for one live page (a Blazor circuit): the person's status feed, Open, Close, Clear fault and a status read,
/// with the rules and words every client shares (<see cref="RoofCommandRules"/>, <see cref="RoofStatusText"/>). Stop is
/// not here: the page's Stop form posts it (<see cref="WebStopEndpoint"/>) so it works without the live connection.
/// </summary>
/// <remarks>
/// <para>A motion started here holds the operator lease: this page renews it while the roof moves, and stops renewing
/// when the roof stops, when Stop is sent from any of the person's pages, when the session ends, and as soon as the
/// page's connection goes down. The controller then stops the roof when the lease runs out, so a roof is never kept moving
/// for a browser that has gone.</para>
/// <para>A status is shown as current only while the hub delivers it. Motion is not offered on a stale status: the roof
/// could not be watched. Stop wins: an Open or Close accepted after a Stop was sent is stopped again.</para>
/// <para>Thread-safe. <see cref="Changed"/> is raised on any thread; a component marshals it with
/// <c>InvokeAsync</c>.</para>
/// </remarks>
public sealed class WebRoofConsole : IDisposable
{
    /// <summary>How many notices a page keeps.</summary>
    public const int NoticeLimit = 5;

    /// <summary>A block reason for a page with no session (it has been signed out).</summary>
    public const string NoSession = "you are signed out";

    /// <summary>A block reason for a page whose live connection has not started the console yet.</summary>
    public const string NotStarted = "the page is still connecting";

    /// <summary>Said when the page's connection came back after it dropped the operator lease.</summary>
    public const string LeaseDroppedOnDisconnect =
        "The connection dropped, so this page stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.";

    /// <summary>How soon a lease renewal that failed is tried again, while the roof moves.</summary>
    public static TimeSpan LeaseRetryDelay { get; } = TimeSpan.FromSeconds(1);

    private readonly WebSessionAccessor _sessions;
    private readonly WebCircuitMonitor _circuit;
    private readonly TimeProvider _time;
    private readonly ILogger<WebRoofConsole> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly ITimer _leaseTimer;
    private readonly List<WebRoofNotice> _notices = [];
    private Task? _starting;
    private bool _started;
    private WebSession? _session;
    private WebStatusFeedHold? _hold;
    private RoofStatusResponse? _status;
    private DateTimeOffset? _statusTakenAt;
    private string? _busy;
    private bool _refreshing;
    private bool _holdsLease;
    private bool _renewing;
    private DateTimeOffset? _leaseDue;
    private bool _leaseDroppedOnDisconnect;
    private bool _disposed;
    private WebRoofView _view = WebRoofView.NotStarted;

    public WebRoofConsole(WebSessionAccessor sessions, WebCircuitMonitor circuit, TimeProvider time, ILogger<WebRoofConsole> logger)
    {
        _sessions = sessions;
        _circuit = circuit;
        _time = time;
        _logger = logger;
        _leaseTimer = time.CreateTimer(_ => OnLeaseDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The newest view.</summary>
    public WebRoofView View => Volatile.Read(ref _view);

    /// <summary>Raised after <see cref="View"/> changed, on any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Joins the person's status feed. Called once the page's live connection is up (never while prerendering); a second
    /// call does nothing.
    /// </summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            return _disposed ? Task.CompletedTask : _starting ??= StartCoreAsync();
        }
    }

    public Task OpenAsync() => MoveAsync(RoofMotionDirection.Opening);

    public Task CloseAsync() => MoveAsync(RoofMotionDirection.Closing);

    public async Task ClearFaultAsync()
    {
        if (Begin(RoofCommandRules.GetClearFaultBlockReason, "Sending Clear fault…") is not { } command)
        {
            return;
        }

        try
        {
            var status = await command.Session.Client.Roof.ClearFaultAsync(null, command.Closing).ConfigureAwait(false);
            lock (_gate)
            {
                ApplyLocked(status);
                AddNoticeLocked($"Clear fault accepted. Fault: {RoofStatusText.DescribeFault(status)}.", WebRoofNoticeLevel.Info);
            }
        }
        catch (Exception error) when (!command.Closing.IsCancellationRequested)
        {
            Refused(error, "Clear fault", command.Session);
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        finally
        {
            End();
        }
    }

    /// <summary>Reads the status once over REST: shown as current only while the hub delivers the status too.</summary>
    public async Task RefreshAsync()
    {
        WebSession session;
        CancellationToken closing;
        lock (_gate)
        {
            if (_disposed || _refreshing || _session is null)
            {
                return;
            }

            _refreshing = true;
            session = _session;
            closing = _closing.Token;
        }

        try
        {
            var status = await session.Client.Roof.GetStatusAsync(closing).ConfigureAwait(false);
            lock (_gate)
            {
                ApplyLocked(status);
                AddNoticeLocked($"Read the status at {RoofStatusText.Time(status.SnapshotUtc)}.", WebRoofNoticeLevel.Info);
            }
        }
        catch (Exception error) when (!closing.IsCancellationRequested)
        {
            Refused(error, "The status read", session);
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
            }

            Publish();
        }
    }

    public void Dispose()
    {
        WebStatusFeedHold? hold;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_holdsLease)
            {
                _logger.LogWarning(
                    "Web page for {Name} closed while it held the operator lease; renewal stopped so the lease can expire",
                    _session?.Name);
            }

            DropLeaseLocked();
            Unsubscribe();
            hold = _hold;
            _hold = null;
        }

        _closing.Cancel();
        _leaseTimer.Dispose();
        hold?.Dispose();
    }

    private async Task StartCoreAsync()
    {
        var session = await _sessions.GetAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _started = true;
            if (session is not null)
            {
                try
                {
                    _hold = session.HoldStatusFeed();
                    _session = session;
                }
                catch (ObjectDisposedException)
                {
                    // The session was closed as the page started: the page is signed out.
                }
            }

            if (_session is { } open && _hold is { } hold)
            {
                hold.Feed.StatusReceived += OnStatusReceived;
                hold.Feed.StateChanged += OnFeedStateChanged;
                open.StopSent += OnStopSent;
                open.Ended += OnSessionEnded;
                _circuit.ConnectionChanged += OnConnectionChanged;
                if (hold.Feed.Current is { } current)
                {
                    ApplyLocked(current.Status);
                }
            }
        }

        Publish();
    }

    private void Unsubscribe()
    {
        if (_hold is { } hold)
        {
            hold.Feed.StatusReceived -= OnStatusReceived;
            hold.Feed.StateChanged -= OnFeedStateChanged;
        }

        if (_session is { } session)
        {
            session.StopSent -= OnStopSent;
            session.Ended -= OnSessionEnded;
        }

        _circuit.ConnectionChanged -= OnConnectionChanged;
    }

    private async Task MoveAsync(RoofMotionDirection direction)
    {
        var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";
        if (Begin(state => RoofCommandRules.GetMotionBlockReason(direction, state), $"Sending {verb}…") is not { } command)
        {
            return;
        }

        var session = command.Session;
        try
        {
            RoofStatusResponse status;
            try
            {
                status = direction == RoofMotionDirection.Opening
                    ? await session.Client.Roof.OpenAsync(command.Closing).ConfigureAwait(false)
                    : await session.Client.Roof.CloseAsync(command.Closing).ConfigureAwait(false);
            }
            catch (Exception error) when (RoofCommandRules.MayHaveReachedController(error) && !command.Closing.IsCancellationRequested)
            {
                // The controller may have set the roof moving, and there is no lease to renew: a Stop sent meanwhile may
                // have reached it first, so Stop is sent again.
                _logger.LogWarning(error, "{Verb} from {Name} got no answer", verb, session.Name);
                bool stopAgain;
                lock (_gate)
                {
                    AddNoticeLocked(RoofCommandRules.DescribeUnanswered(error, verb, $"press {RoofStopText.ButtonLabel}"), WebRoofNoticeLevel.Danger);
                    stopAgain = session.StopsSent != command.StopsSent;
                }

                if (stopAgain)
                {
                    await StopAgainAsync(session).ConfigureAwait(false);
                }

                return;
            }

            bool crossedStop;
            lock (_gate)
            {
                ApplyLocked(status);
                HoldLeaseLocked(status);

                // Checked after the lease is held: a Stop counted later drops it (OnStopSent).
                crossedStop = status.IsMoving && session.StopsSent != command.StopsSent;
                if (crossedStop)
                {
                    DropLeaseLocked();
                    AddNoticeLocked($"{verb} was accepted after Stop was sent, so Stop is sent again.", WebRoofNoticeLevel.Danger);
                }
                else
                {
                    AddNoticeLocked(
                        !status.IsMoving ? $"{verb} accepted. Roof: {RoofStatusText.DescribeRoof(status)}."
                            : _holdsLease ? $"{verb} accepted. This page renews the operator lease while the roof moves; if the page closes or loses its connection, the roof stops when the lease runs out. {RoofStopText.ButtonLabel} stops it now."
                            : $"{verb} accepted. {RoofStopText.ButtonLabel} stops it.",
                        WebRoofNoticeLevel.Info);
                }
            }

            if (crossedStop)
            {
                await StopAgainAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (!command.Closing.IsCancellationRequested)
        {
            Refused(error, verb, session);
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        finally
        {
            End();
        }
    }

    // Stop is sent whether or not the page is closing.
    private async Task StopAgainAsync(WebSession session)
    {
        var result = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            AddNoticeLocked(result.Message, result.Outcome switch
            {
                RoofStopOutcome.Acknowledged => WebRoofNoticeLevel.Info,
                RoofStopOutcome.RelayUnverified => WebRoofNoticeLevel.Warning,
                _ => WebRoofNoticeLevel.Danger
            });
        }
    }

    // One command at a time. Null when it is not sent: the notice says why.
    private Command? Begin(Func<RoofCommandState, string?> blockReason, string busy)
    {
        Command? command = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            var reason = NotOfferedLocked() ?? blockReason(CommandStateLocked());
            if (reason is not null)
            {
                AddNoticeLocked($"Not sent: {reason}.", WebRoofNoticeLevel.Warning);
            }
            else
            {
                _busy = busy;
                command = new Command(_session!, _session!.StopsSent, _closing.Token);
            }
        }

        Publish();
        return command;
    }

    private void End()
    {
        lock (_gate)
        {
            _busy = null;
        }

        Publish();
    }

    private void Refused(Exception error, string what, WebSession session)
    {
        if (error is RoofApiException refusal)
        {
            _logger.LogInformation("{What} from {Name} was refused: {Code}", what, session.Name, refusal.CodeText ?? refusal.StatusCode.ToString());
        }
        else
        {
            _logger.LogWarning(error, "{What} from {Name} failed", what, session.Name);
        }

        var message = RoofText.DescribeFailure(error);
        var detail = error is RoofApiException { Detail: { Length: > 0 } text } && text != message ? $" {text}" : string.Empty;
        lock (_gate)
        {
            if (error is RoofApiException { RoofStatus: { } status })
            {
                ApplyLocked(status);
            }

            AddNoticeLocked(message + detail, WebRoofNoticeLevel.Danger);
        }
    }

    // Why nothing is offered: the page has not started, or has no session.
    private string? NotOfferedLocked() => !_started ? NotStarted : _session is null ? NoSession : null;

    private RoofCommandState CommandStateLocked()
        => new(_status, IsStaleLocked(), _session is { } session && IsOperator(session.Role), _busy is not null);

    private static bool IsOperator(string role) => role is WebRoles.Operator or WebRoles.Admin;

    private bool ApplyLocked(RoofStatusResponse status)
    {
        if (!RoofStatusRules.ShouldApply(_status, status))
        {
            return false;
        }

        _status = status;
        _statusTakenAt = _time.GetUtcNow();
        if (!status.IsMoving)
        {
            DropLeaseLocked();
        }

        return true;
    }

    // Only while the page's connection is up: a page that lost it must not keep a motion alive.
    private void HoldLeaseLocked(RoofStatusResponse status)
    {
        if (!status.IsMoving || RoofStatusRules.GetLeaseRenewalDelay(status.LeaseSecondsRemaining) is not { } delay)
        {
            return;
        }

        if (!_circuit.IsConnected)
        {
            _leaseDroppedOnDisconnect = true;
            return;
        }

        _holdsLease = true;
        ScheduleLeaseLocked(delay);
    }

    // The earliest renewal wins.
    private void ScheduleLeaseLocked(TimeSpan delay)
    {
        var due = _time.GetUtcNow() + delay;
        if (_leaseDue is { } scheduled && scheduled <= due)
        {
            return;
        }

        _leaseDue = due;
        _leaseTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void DropLeaseLocked()
    {
        _holdsLease = false;
        _leaseDue = null;
        if (!_disposed)
        {
            _leaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnLeaseDue()
    {
        WebSession session;
        CancellationToken closing;
        lock (_gate)
        {
            if (_disposed || !_holdsLease || _session is null)
            {
                return;
            }

            _leaseDue = null;
            if (_renewing)
            {
                // The renewal on its way schedules the next.
                return;
            }

            _renewing = true;
            session = _session;
            closing = _closing.Token;
        }

        _ = RenewLeaseAsync(session, closing);
    }

    private async Task RenewLeaseAsync(WebSession session, CancellationToken closing)
    {
        try
        {
            var status = await session.Client.Roof.RenewLeaseAsync(closing).ConfigureAwait(false);
            lock (_gate)
            {
                ApplyLocked(status);
                if (_holdsLease)
                {
                    if (status.IsMoving && RoofStatusRules.GetLeaseRenewalDelay(status.LeaseSecondsRemaining) is { } delay)
                    {
                        ScheduleLeaseLocked(delay);
                    }
                    else
                    {
                        DropLeaseLocked();
                    }
                }
            }
        }
        catch (RoofApiException refusal) when (refusal.Code == RoofControllerErrorCode.LeaseNotActive)
        {
            lock (_gate)
            {
                DropLeaseLocked();
                if (refusal.RoofStatus is { } status)
                {
                    ApplyLocked(status);
                }
            }
        }
        catch (Exception) when (closing.IsCancellationRequested)
        {
            // The page closed.
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Lease renewal for {Name} failed", session.Name);
            lock (_gate)
            {
                if (_holdsLease)
                {
                    // Tried again while the roof moves; Stop, the end of the motion or a lost connection ends it.
                    AddNoticeLocked(
                        $"The lease could not be renewed: {RoofText.DescribeFailure(error)} If the controller is running, it stops the roof when the lease runs out.",
                        WebRoofNoticeLevel.Danger);
                    ScheduleLeaseLocked(LeaseRetryDelay);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _renewing = false;
            }

            Publish();
        }
    }

    private void OnStatusReceived(object? sender, RoofStatusReceivedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (e.IsNewInstance && e.Previous is not null)
            {
                AddNoticeLocked("The controller restarted.", WebRoofNoticeLevel.Warning);
            }

            if (e.SafetyAlert is { } alert)
            {
                AddNoticeLocked($"SAFETY: {alert.Title}. {alert.Message}", WebRoofNoticeLevel.Danger);
            }

            ApplyLocked(e.Status);
        }

        Publish();
    }

    private void OnFeedStateChanged(object? sender, EventArgs e) => Publish();

    // Stop from any of the person's pages ends this page's hold on a motion.
    private void OnStopSent(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            DropLeaseLocked();
        }

        Publish();
    }

    private void OnSessionEnded(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_holdsLease)
            {
                AddNoticeLocked(
                    "Your session ended, so this page stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.",
                    WebRoofNoticeLevel.Warning);
            }

            DropLeaseLocked();
        }

        Publish();
    }

    private void OnConnectionChanged(bool connected)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!connected)
            {
                if (_holdsLease)
                {
                    _logger.LogWarning(
                        "Web page connection for {Name} lost while it held the operator lease; renewal stopped so the lease can expire",
                        _session?.Name);
                    DropLeaseLocked();
                    _leaseDroppedOnDisconnect = true;
                }
            }
            else if (_leaseDroppedOnDisconnect)
            {
                _leaseDroppedOnDisconnect = false;
                AddNoticeLocked(LeaseDroppedOnDisconnect, WebRoofNoticeLevel.Warning);
            }
        }

        Publish();
    }

    private void AddNoticeLocked(string text, WebRoofNoticeLevel level)
    {
        _notices.Insert(0, new WebRoofNotice(text, level, _time.GetUtcNow()));
        if (_notices.Count > NoticeLimit)
        {
            _notices.RemoveRange(NoticeLimit, _notices.Count - NoticeLimit);
        }
    }

    private bool IsStaleLocked() => _status is null || GetStaleSinceLocked() is not null;

    private DateTimeOffset? GetStaleSinceLocked()
    {
        if (_status is null || _hold?.Feed is not { } feed)
        {
            return null;
        }

        // A status read over REST while the hub is not delivering is stale from the moment it was read.
        return feed.StaleSince ?? (IsLive(feed) ? null : _statusTakenAt ?? _time.GetUtcNow());
    }

    /// <summary>True while the hub delivers the status: connected, with a status that is not stale.</summary>
    private static bool IsLive(RoofStatusFeed feed)
        => feed.State == RoofStatusFeedState.Connected && feed.Status is not null && !feed.IsStale;

    private void Publish()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _view, BuildViewLocked());
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "A roof view handler failed");
        }
    }

    private WebRoofView BuildViewLocked()
    {
        var feed = _hold?.Feed;
        var state = feed?.State ?? RoofStatusFeedState.Stopped;
        var refused = state == RoofStatusFeedState.Unauthorized;
        var staleSince = GetStaleSinceLocked();
        string? banner = null;
        if (feed is not null)
        {
            banner = _status is null
                ? refused ? "No status: the controller refused your session. Sign in again. Stop still works." : "No status from the controller yet."
                : feed.StaleSince is { } since ? $"STALE: no status since {RoofStatusText.Time(since)}. Showing the last known state; Stop still works."
                : staleSince is { } read ? $"STALE: status from a single read at {RoofStatusText.Time(read)}; live status is not connected. Stop still works."
                : null;
        }

        var command = CommandStateLocked();
        var notOffered = NotOfferedLocked();
        return new WebRoofView
        {
            IsStarted = _started,
            HasSession = _session is not null,
            CanOperate = command.CanOperate == true,
            Status = _status,
            FeedState = state,
            FeedLabel = feed is null ? "not connected"
                : refused ? "refused"
                : staleSince is not null ? "STALE"
                : state switch
                {
                    // Live only with a status: connected, before the first message, it is still connecting.
                    RoofStatusFeedState.Connected => _status is null ? "connecting" : "live",
                    RoofStatusFeedState.Connecting => "connecting",
                    RoofStatusFeedState.Reconnecting => "reconnecting",
                    _ => "not connected"
                },
            StaleSince = staleSince,
            FeedBanner = banner,
            FeedRefused = refused,
            Busy = _busy,
            OpenBlock = notOffered ?? RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, command),
            CloseBlock = notOffered ?? RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Closing, command),
            ClearFaultBlock = notOffered ?? RoofCommandRules.GetClearFaultBlockReason(command),
            HoldsLease = _holdsLease,
            Notices = _notices.ToArray()
        };
    }

    private sealed record Command(WebSession Session, long StopsSent, CancellationToken Closing);
}
