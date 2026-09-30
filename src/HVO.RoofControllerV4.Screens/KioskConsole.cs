using System.Text.Json;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The roof, for a kiosk at the roof: its status feed (the device key's), Stop, and, once a person unlocks it with their
/// PIN, Open, Close and Clear fault, with the rules and words every client shares (<see cref="RoofCommandRules"/>,
/// <see cref="RoofStatusText"/>, <see cref="RoofStopText"/>). It locks itself after <see cref="KioskConsoleOptions.IdleLock"/>
/// without a touch, and blanks the screen after the controller's kiosk screen timeout. The Mac app is the same console,
/// signed in to with a name and password (<see cref="SignInAsync"/>) and never blanked
/// (<see cref="KioskConsoleOptions.Wording"/>, <see cref="KioskConsoleOptions.Blanking"/>).
/// </summary>
/// <remarks>
/// <para>Stop is always offered, locked or not, and is sent at once on a connection of its own: it never waits for a
/// command, a lock or the controller being reachable. Its answer says whether it arrived; nothing else claims it did.</para>
/// <para>A motion started here holds the operator lease: the kiosk renews it while the roof moves, and stops renewing
/// when the roof stops, when Stop is sent, and when the kiosk locks. The controller then stops the roof when the lease
/// runs out. The kiosk does not lock itself while it holds the lease.</para>
/// <para>A status is shown as current only while the hub delivers it. Motion is not offered on a stale status: the roof
/// could not be watched. Stop wins: an Open or Close accepted after a Stop was sent is stopped again.</para>
/// <para>Thread-safe. <see cref="Changed"/> is raised on any thread; a view marshals it to its UI thread.</para>
/// </remarks>
public sealed class KioskConsole : IAsyncDisposable
{
    /// <summary>How many notices the kiosk keeps.</summary>
    public const int NoticeLimit = 5;

    /// <summary>The controller's setting for how long the kiosk screen stays on without a touch.</summary>
    public const string ScreenTimeoutKey = RoofControllerUiOptions.SectionName + ":" + nameof(RoofControllerUiOptions.KioskScreenTimeout);

    /// <summary>How soon a lease renewal that failed is tried again, while the roof moves.</summary>
    public static TimeSpan LeaseRetryDelay { get; } = TimeSpan.FromSeconds(1);

    private readonly RoofControllerClient _client;
    private readonly RoofKioskCredential _kiosk;
    private readonly KioskConsoleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly ITimer _leaseTimer;
    private readonly ITimer _tickTimer;
    private readonly List<KioskNotice> _notices = [];
    private RoofStatusFeed? _feed;
    private bool _started;
    private bool _disposed;
    private RoofSessionResponse? _session;
    private Task _signingOut = Task.CompletedTask;
    private RoofStatusResponse? _status;
    private DateTimeOffset? _statusTakenAt;
    private string? _busy;
    private bool _holdsLease;
    private bool _renewing;
    private DateTimeOffset? _leaseDue;
    private long _stopsSent;
    private int _stopsInFlight;
    private Task _stops = Task.CompletedTask;
    private string _stopMessage = RoofStopText.AlwaysAvailable;
    private RoofStopOutcome _stopOutcome;
    private DateTimeOffset _lastTouch;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _keptAliveAt;
    private bool _keepingAlive;
    private bool _blank;
    private TimeSpan _screenTimeout;
    private DateTimeOffset _screenTimeoutDue;
    private bool _readingScreenTimeout;
    private KioskView _view;

    /// <param name="client">The kiosk's client, whose credential is a <see cref="RoofKioskCredential"/>.</param>
    public KioskConsole(RoofControllerClient client, KioskConsoleOptions? options = null, ILogger<KioskConsole>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _kiosk = client.Credential as RoofKioskCredential
            ?? throw new ArgumentException("The kiosk needs a kiosk credential (RoofKioskCredential).", nameof(client));
        _options = options ?? new KioskConsoleOptions();
        _time = client.Options.TimeProvider;
        _logger = logger ?? NullLogger<KioskConsole>.Instance;
        var now = _time.GetUtcNow();
        _lastTouch = now;
        _lastActivity = now;
        _keptAliveAt = now;
        _screenTimeout = _options.DefaultScreenTimeout;
        _screenTimeoutDue = now;
        _session = _kiosk.PinSession;
        _view = KioskView.NotStarted with
        {
            OpenBlock = _options.Wording.Starting,
            CloseBlock = _options.Wording.Starting,
            ClearFaultBlock = _options.Wording.Starting
        };
        _leaseTimer = _time.CreateTimer(_ => OnLeaseDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _tickTimer = _time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _kiosk.PinSessionChanged += OnPinSessionChanged;
    }

    /// <summary>The newest view.</summary>
    public KioskView View => Volatile.Read(ref _view);

    /// <summary>The client the kiosk talks to the controller with: the settings and system screens use it too.</summary>
    public RoofControllerClient Client => _client;

    /// <summary>How a person signs in here, and the words for it.</summary>
    public KioskWording Wording => _options.Wording;

    /// <summary>Raised after <see cref="View"/> changed, on any thread.</summary>
    public event Action? Changed;

    /// <summary>True while a touch's keep-alive request is on its way (for tests: wait for it rather than for a time).</summary>
    internal bool KeepingAlive
    {
        get
        {
            lock (_gate)
            {
                return _keepingAlive;
            }
        }
    }

    /// <summary>Starts the status feed, the idle lock and the screen timeout. A second call does nothing.</summary>
    public void Start()
    {
        RoofStatusFeed feed;
        lock (_gate)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            feed = _feed = _client.CreateStatusFeed();
            feed.StatusReceived += OnStatusReceived;
            feed.StateChanged += OnFeedStateChanged;
            _tickTimer.Change(_options.Tick, _options.Tick);
        }

        feed.Start();
        if (_options.Blanking)
        {
            RefreshScreenTimeout();
        }

        Publish();
    }

    /// <summary>
    /// Records a touch anywhere on the screen: it keeps the kiosk unlocked and the screen on. Returns true when the screen
    /// was blank. Whether the touch also reaches the control under it is the view's to decide, from what it showed: the
    /// view may not have drawn the blank screen yet.
    /// </summary>
    public bool Touch()
    {
        bool woke;
        RoofSessionResponse? keepAlive = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            woke = _blank;
            _blank = false;
            _lastTouch = now;
            _lastActivity = now;
            if (_session is { } session && !_keepingAlive && now - _keptAliveAt >= GetKeepAliveInterval(session))
            {
                _keepingAlive = true;
                keepAlive = session;
            }
        }

        if (woke)
        {
            Publish();
        }

        if (keepAlive is not null)
        {
            _ = KeepAliveAsync(keepAlive);
        }

        return woke;
    }

    /// <summary>
    /// Unlocks the kiosk with a person's PIN. Returns null when it is unlocked, or why it is not (the controller's
    /// answer: a wrong PIN, a lockout, a controller that cannot be reached).
    /// </summary>
    public Task<string?> UnlockAsync(string name, string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(pin);
        return SignInCoreAsync(name, "Kiosk PIN sign-in", "Kiosk unlocked", closing => _client.Auth.SignInWithPinAsync(name, pin, closing));
    }

    /// <summary>
    /// Signs a person in with their name and password (the Mac app). Returns null when they are signed in, or why they
    /// are not (the controller's answer: a wrong name or password, a lockout, a controller that cannot be reached).
    /// </summary>
    public Task<string?> SignInAsync(string name, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return SignInCoreAsync(name, "Sign-in", "Signed in", closing => _client.Auth.SignInOnDeviceAsync(name, password, closing));
    }

    // What: "Kiosk PIN sign-in", and done: "Kiosk unlocked", for the log.
    private async Task<string?> SignInCoreAsync(string name, string what, string done, Func<CancellationToken, Task<RoofSessionResponse>> signIn)
    {
        Task signingOut;
        CancellationToken closing;
        lock (_gate)
        {
            if (_disposed)
            {
                return _options.Wording.Closing;
            }

            if (_session is not null)
            {
                return _options.Wording.AlreadySignedIn;
            }

            signingOut = _signingOut;
            closing = _closing.Token;
        }

        // The previous person's session ends first: signing out ends whichever session the kiosk uses then.
        await signingOut.ConfigureAwait(false);
        var sentAt = _time.GetUtcNow();
        try
        {
            var session = await signIn(closing).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _keptAliveAt = sentAt;
                    AddNoticeLocked(_options.Wording.SignedIn(session.Name, session.Role), KioskNoticeLevel.Info);
                }
            }

            _logger.LogInformation("{Done} by {Name} ({Role})", done, session.Name, session.Role);
            Publish();
            return null;
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            return _options.Wording.Closing;
        }
        catch (Exception error)
        {
            if (error is RoofApiException refusal)
            {
                _logger.LogInformation("{What} for {Name} was refused: {Code}", what, name, refusal.CodeText ?? refusal.StatusCode.ToString());
            }
            else
            {
                _logger.LogWarning(error, "{What} for {Name} failed", what, name);
            }

            return RoofText.DescribeFailure(error);
        }
    }

    /// <summary>
    /// Locks the kiosk at once and ends the PIN session at the controller. The returned task ends when the controller
    /// answered, or after <see cref="KioskConsoleOptions.SignOutTimeout"/>; it never throws.
    /// </summary>
    public Task LockAsync() => LockCore(_options.Wording.SignedOutNotice, KioskNoticeLevel.Info);

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
            var status = await _client.Roof.ClearFaultAsync(null, command.Closing).ConfigureAwait(false);
            lock (_gate)
            {
                KeptAliveLocked(command);
                ApplyLocked(status);
                AddNoticeLocked($"Clear fault accepted. Fault: {RoofStatusText.DescribeFault(status)}.", KioskNoticeLevel.Info);
            }
        }
        catch (Exception error) when (!command.Closing.IsCancellationRequested)
        {
            Refused(error, "Clear fault", command.Session);
        }
        catch (OperationCanceledException)
        {
            // The kiosk is closing.
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Sends Stop at once, on its own request: it never waits for another command, and nothing disables it. The returned
    /// task ends when the controller answered or the request failed; it never throws.
    /// </summary>
    public Task StopAsync()
    {
        Task stop;
        lock (_gate)
        {
            var sequence = ++_stopsSent;

            // Stop ends the kiosk's hold on a motion: the lease is no longer renewed.
            DropLeaseLocked();
            _stopMessage = RoofStopText.Sending;
            _stopOutcome = RoofStopOutcome.Sent;
            _stopsInFlight++;
            var now = _time.GetUtcNow();
            _lastActivity = now;
            WakeLocked(now);
            stop = Task.Run(() => SendStopAsync(sequence));
            _stops = Task.WhenAll(_stops, stop);
        }

        Publish();
        return stop;
    }

    /// <summary>Reads the kiosk screen timeout from the controller's settings now, unless a read is on its way.</summary>
    public void RefreshScreenTimeout()
    {
        if (!_options.Blanking)
        {
            return;
        }

        CancellationToken closing;
        lock (_gate)
        {
            if (_disposed || _readingScreenTimeout)
            {
                return;
            }

            _readingScreenTimeout = true;
            _screenTimeoutDue = _time.GetUtcNow() + _options.ScreenTimeoutRefresh;
            closing = _closing.Token;
        }

        _ = ReadScreenTimeoutAsync(closing);
    }

    /// <summary>
    /// Stops the feed and the timers. Stops still on their way are answered first (each within the client's
    /// <see cref="RoofConnectionOptions.StopTimeout"/>). The PIN session is not ended: <see cref="LockAsync"/> does that.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        RoofStatusFeed? feed;
        Task stops;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_holdsLease)
            {
                _logger.LogWarning("Kiosk closed while it held the operator lease; renewal stopped so the lease can expire");
            }

            DropLeaseLocked();
            _disposed = true;
            feed = _feed;
            if (feed is not null)
            {
                feed.StatusReceived -= OnStatusReceived;
                feed.StateChanged -= OnFeedStateChanged;
            }

            stops = _stops;
        }

        _kiosk.PinSessionChanged -= OnPinSessionChanged;
        _closing.Cancel();
        await _leaseTimer.DisposeAsync().ConfigureAwait(false);
        await _tickTimer.DisposeAsync().ConfigureAwait(false);
        if (feed is not null)
        {
            await feed.DisposeAsync().ConfigureAwait(false);
        }

        await stops.ConfigureAwait(false);
        _closing.Dispose();
    }

    /// <summary>The setting's value in <paramref name="settings"/>, or null when it is missing or not a duration.</summary>
    internal static TimeSpan? ReadScreenTimeout(RoofSettingsResponse settings)
    {
        var state = settings.Settings.FirstOrDefault(setting => setting.Key == ScreenTimeoutKey);
        if (state?.Value is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var seconds)
            && double.IsFinite(seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, RoofControllerUiOptions.MaximumKioskScreenTimeout.TotalSeconds));
        }

        return null;
    }

    private async Task MoveAsync(RoofMotionDirection direction)
    {
        var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";
        if (Begin(state => RoofCommandRules.GetMotionBlockReason(direction, state), $"Sending {verb}…") is not { } command)
        {
            return;
        }

        try
        {
            RoofStatusResponse status;
            try
            {
                status = direction == RoofMotionDirection.Opening
                    ? await _client.Roof.OpenAsync(command.Closing).ConfigureAwait(false)
                    : await _client.Roof.CloseAsync(command.Closing).ConfigureAwait(false);
            }
            catch (Exception error) when (RoofCommandRules.MayHaveReachedController(error) && !command.Closing.IsCancellationRequested)
            {
                // The controller may have set the roof moving, and there is no lease to renew: a Stop sent meanwhile may
                // have reached it first, so Stop is sent again.
                _logger.LogWarning(error, "{Verb} from {Name} got no answer", verb, command.Session.Name);
                bool stopAgain;
                lock (_gate)
                {
                    AddNoticeLocked(RoofCommandRules.DescribeUnanswered(error, verb, $"press {RoofStopText.ButtonLabel}"), KioskNoticeLevel.Danger);
                    stopAgain = _stopsSent != command.StopsSent;
                }

                if (stopAgain)
                {
                    await StopAsync().ConfigureAwait(false);
                }

                return;
            }

            bool crossedStop;
            lock (_gate)
            {
                KeptAliveLocked(command);
                ApplyLocked(status);

                // A motion is held only for the person who started it, while the kiosk is still theirs.
                var lockedMeanwhile = !ReferenceEquals(_session, command.Session);
                if (!lockedMeanwhile)
                {
                    HoldLeaseLocked(status);
                }

                // Checked after the lease is held: a Stop sent later drops it.
                crossedStop = status.IsMoving && _stopsSent != command.StopsSent;
                if (crossedStop)
                {
                    DropLeaseLocked();
                    AddNoticeLocked($"{verb} was accepted after Stop was sent, so Stop is sent again.", KioskNoticeLevel.Danger);
                }
                else
                {
                    AddNoticeLocked(
                        !status.IsMoving ? $"{verb} accepted. Roof: {RoofStatusText.DescribeRoof(status)}."
                            : lockedMeanwhile && status.LeaseSecondsRemaining is not null
                                ? $"{verb} {_options.Wording.AcceptedAfterSignOut}"
                            : _holdsLease ? $"{verb} accepted. {_options.Wording.RenewsLease} {RoofStopText.ButtonLabel} stops it now."
                            : $"{verb} accepted. {RoofStopText.ButtonLabel} stops it.",
                        lockedMeanwhile && status.IsMoving ? KioskNoticeLevel.Warning : KioskNoticeLevel.Info);
                }
            }

            if (crossedStop)
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error) when (!command.Closing.IsCancellationRequested)
        {
            Refused(error, verb, command.Session);
        }
        catch (OperationCanceledException)
        {
            // The kiosk is closing.
        }
        finally
        {
            End();
        }
    }

    private async Task SendStopAsync(long sequence)
    {
        RoofStopResult result;
        try
        {
            result = await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The client answers every failure itself; this is a client that was already closed.
            result = new RoofStopResult(RoofStopOutcome.Failed, RoofStopText.Failed(RoofText.DescribeFailure(error)), null, error);
        }

        lock (_gate)
        {
            _stopsInFlight--;

            // An older Stop's answer says nothing about a newer one, which may have failed or been confirmed.
            if (sequence == _stopsSent)
            {
                _stopMessage = result.Message;
                _stopOutcome = result.Outcome;
            }

            if (result.Status is { } status)
            {
                ApplyLocked(status);
            }
        }

        Publish();
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
                AddNoticeLocked($"Not sent: {reason}.", KioskNoticeLevel.Warning);
            }
            else
            {
                _busy = busy;
                command = new Command(_session!, _stopsSent, _closing.Token, _time.GetUtcNow());
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

    private void Refused(Exception error, string what, RoofSessionResponse session)
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

            AddNoticeLocked(message + detail, KioskNoticeLevel.Danger);
        }
    }

    // Why nothing is offered: the console has not started, or the kiosk is locked.
    private string? NotOfferedLocked() => !_started ? _options.Wording.Starting : _session is null ? _options.Wording.SignedOutBlock : null;

    private RoofCommandState CommandStateLocked()
        => new(_status, IsStaleLocked(), _session is { } session && IsOperator(session.Role), _busy is not null);

    private static bool IsOperator(string role) => role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole;

    private bool ApplyLocked(RoofStatusResponse status)
    {
        if (!RoofStatusRules.ShouldApply(_status, status))
        {
            return false;
        }

        var wasMoving = _status?.IsMoving;
        _status = status;
        _statusTakenAt = _time.GetUtcNow();
        if (!status.IsMoving)
        {
            DropLeaseLocked();
        }

        // The screen comes on when the roof starts or stops moving, so the change can be seen.
        if (wasMoving is { } moving && moving != status.IsMoving)
        {
            WakeLocked(_statusTakenAt.Value);
        }

        return true;
    }

    private void HoldLeaseLocked(RoofStatusResponse status)
    {
        if (!status.IsMoving || RoofStatusRules.GetLeaseRenewalDelay(status.LeaseSecondsRemaining) is not { } delay)
        {
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

    // The idle lock counts from the end of a motion the kiosk held: the person watched it until then.
    private void DropLeaseLocked()
    {
        if (_holdsLease)
        {
            _lastActivity = _time.GetUtcNow();
        }

        _holdsLease = false;
        _leaseDue = null;
        if (!_disposed)
        {
            _leaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnLeaseDue()
    {
        RoofSessionResponse session;
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

    private async Task RenewLeaseAsync(RoofSessionResponse session, CancellationToken closing)
    {
        var sentAt = _time.GetUtcNow();
        try
        {
            var status = await _client.Roof.RenewLeaseAsync(closing).ConfigureAwait(false);
            lock (_gate)
            {
                KeptAliveLocked(session, sentAt);
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
            // The kiosk is closing.
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Lease renewal for {Name} failed", session.Name);
            lock (_gate)
            {
                if (_holdsLease)
                {
                    // Tried again while the roof moves; Stop, the end of the motion or the lock ends it.
                    AddNoticeLocked(
                        $"The lease could not be renewed: {RoofText.DescribeFailure(error)} If the controller is running, it stops the roof when the lease runs out.",
                        KioskNoticeLevel.Danger);
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

    private Task LockCore(string notice, KioskNoticeLevel level)
    {
        lock (_gate)
        {
            if (_disposed || _session is not { } session)
            {
                return _signingOut;
            }

            _session = null;
            if (_holdsLease)
            {
                _logger.LogWarning("Kiosk locked while it held the operator lease for {Name}; renewal stopped so the lease can expire", session.Name);
                AddNoticeLocked(_options.Wording.LeaseDroppedOnSignOut, KioskNoticeLevel.Warning);
            }

            DropLeaseLocked();
            AddNoticeLocked(notice, level);
            _logger.LogInformation("Kiosk locked ({Notice}); it was unlocked by {Name}", notice, session.Name);

            // Run off the lock: ending the PIN session raises PinSessionChanged.
            _signingOut = Task.Run(() => SignOutAsync(session));
        }

        Publish();
        return _signingOut;
    }

    // Ends the PIN session at the controller while the kiosk still sends its token, then here. Never throws.
    private async Task SignOutAsync(RoofSessionResponse session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.SignOutTimeout, _time);
            await _client.Auth.SignOutAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error,
                "The PIN session of {Name} could not be ended at the controller; it ends there after its idle timeout",
                session.Name);
        }
        finally
        {
            if (ReferenceEquals(_kiosk.PinSession, session))
            {
                _kiosk.EndPinSession();
            }
        }
    }

    private void OnPinSessionChanged(object? sender, RoofSessionResponse? session)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (session is not null)
            {
                _session = session;
                _keptAliveAt = now;
                _lastActivity = now;
                WakeLocked(now);
            }
            else if (_session is not null)
            {
                // A request was refused for the PIN session: it ended at the controller (it was idle too long, or the
                // person's PIN or role changed). The kiosk locks; it did not end the session itself.
                _logger.LogWarning("The controller ended the PIN session of {Name}; the kiosk is locked", _session.Name);
                AddNoticeLocked(_holdsLease ? _options.Wording.SessionEndedWithLease : _options.Wording.SessionEnded, KioskNoticeLevel.Warning);
                DropLeaseLocked();
                _session = null;
                _lastActivity = now;
            }
        }

        Publish();
    }

    private async Task KeepAliveAsync(RoofSessionResponse session)
    {
        var sentAt = _time.GetUtcNow();
        CancellationToken closing;
        lock (_gate)
        {
            closing = _closing.Token;
        }

        try
        {
            // A refusal ends the PIN session (the credential's own rule), which locks the kiosk.
            await _client.Auth.GetCallerAsync(closing).ConfigureAwait(false);
            lock (_gate)
            {
                KeptAliveLocked(session, sentAt);
            }
        }
        catch (Exception) when (closing.IsCancellationRequested)
        {
            // The kiosk is closing.
        }
        catch (Exception error)
        {
            _logger.LogDebug(error, "Keeping the PIN session of {Name} open failed", session.Name);
        }
        finally
        {
            lock (_gate)
            {
                _keepingAlive = false;
            }
        }
    }

    private void KeptAliveLocked(Command command) => KeptAliveLocked(command.Session, command.SentAt);

    // A request that carried the session's token and was answered kept the session open at the controller.
    private void KeptAliveLocked(RoofSessionResponse session, DateTimeOffset sentAt)
    {
        if (ReferenceEquals(_session, session) && sentAt > _keptAliveAt)
        {
            _keptAliveAt = sentAt;
        }
    }

    private TimeSpan GetKeepAliveInterval(RoofSessionResponse session)
        => GetSessionIdleTimeout(session) is { } idle && idle / 4 < _options.KeepAlive
            ? TimeSpan.FromTicks(Math.Max(idle.Ticks / 4, TimeSpan.TicksPerSecond))
            : _options.KeepAlive;

    private static TimeSpan? GetSessionIdleTimeout(RoofSessionResponse session)
        => session.IdleTimeoutSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    private void OnTick()
    {
        string? lockNotice = null;
        var changed = false;
        var readTimeout = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();

            // Never while it renews the lease of a motion started here, nor while a command's answer is awaited.
            if (_session is { } session && !_holdsLease && _busy is null)
            {
                if (now - _lastActivity >= _options.IdleLock)
                {
                    lockNotice = _options.Wording.IdleSignedOutAfter(_options.IdleLock);
                }
                else if (GetSessionIdleTimeout(session) is { } idle && now - _keptAliveAt >= idle)
                {
                    lockNotice = _options.Wording.SessionIdleSignedOut(idle);
                }
            }

            if (_options.Blanking && !_blank && _screenTimeout > TimeSpan.Zero && now - _lastTouch >= _screenTimeout && !KeepAwakeLocked())
            {
                _blank = true;
                changed = true;
            }

            readTimeout = _options.Blanking && !_readingScreenTimeout && now >= _screenTimeoutDue;
        }

        if (lockNotice is not null)
        {
            _ = LockCore(lockNotice, KioskNoticeLevel.Info);
        }
        else if (changed)
        {
            Publish();
        }

        if (readTimeout)
        {
            RefreshScreenTimeout();
        }
    }

    // The screen stays on while the roof moves, a command or Stop is on its way, or the kiosk holds a motion's lease.
    private bool KeepAwakeLocked() => _status?.IsMoving == true || _busy is not null || _stopsInFlight > 0 || _holdsLease;

    // Turns the screen on and starts its timeout again.
    private void WakeLocked(DateTimeOffset now)
    {
        _blank = false;
        _lastTouch = now;
    }

    private async Task ReadScreenTimeoutAsync(CancellationToken closing)
    {
        try
        {
            var settings = await _client.Settings.GetAsync(closing).ConfigureAwait(false);
            if (ReadScreenTimeout(settings) is { } timeout)
            {
                lock (_gate)
                {
                    if (timeout != _screenTimeout)
                    {
                        _logger.LogInformation("Kiosk screen timeout is {Timeout}", timeout);
                        _screenTimeout = timeout;
                    }
                }
            }
        }
        catch (Exception) when (closing.IsCancellationRequested)
        {
            // The kiosk is closing.
        }
        catch (Exception error)
        {
            _logger.LogInformation("The kiosk screen timeout could not be read: {Reason}", RoofText.DescribeFailure(error));
        }
        finally
        {
            lock (_gate)
            {
                _readingScreenTimeout = false;
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

            var now = _time.GetUtcNow();
            if (e.IsNewInstance && e.Previous is not null)
            {
                AddNoticeLocked("The controller restarted.", KioskNoticeLevel.Warning);
                WakeLocked(now);
            }

            if (e.SafetyAlert is { } alert)
            {
                AddNoticeLocked($"SAFETY: {alert.Title}. {alert.Message}", KioskNoticeLevel.Danger);
                WakeLocked(now);
            }

            ApplyLocked(e.Status);
        }

        Publish();
    }

    private void OnFeedStateChanged(object? sender, EventArgs e) => Publish();

    private void AddNoticeLocked(string text, KioskNoticeLevel level)
    {
        _notices.Insert(0, new KioskNotice(text, level, _time.GetUtcNow()));
        if (_notices.Count > NoticeLimit)
        {
            _notices.RemoveRange(NoticeLimit, _notices.Count - NoticeLimit);
        }
    }

    private bool IsStaleLocked() => _status is null || GetStaleSinceLocked() is not null;

    private DateTimeOffset? GetStaleSinceLocked()
    {
        if (_status is null || _feed is not { } feed)
        {
            return null;
        }

        // A status from a command's answer while the hub is not delivering is stale from the moment it arrived.
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
            _logger.LogError(error, "A kiosk view handler failed");
        }
    }

    private KioskView BuildViewLocked()
    {
        var feed = _feed;
        var state = feed?.State ?? RoofStatusFeedState.Stopped;
        var refused = state == RoofStatusFeedState.Unauthorized;
        var unreachable = feed is not null && state is RoofStatusFeedState.Connecting or RoofStatusFeedState.Reconnecting && feed.LastError is not null;
        var staleSince = GetStaleSinceLocked();
        string? banner = null;
        if (feed is not null)
        {
            banner = refused ? _options.Wording.DescribeKeyRefused(staleSince)
                : unreachable ? KioskText.DescribeUnreachable(staleSince)
                : _status is null ? KioskText.NoStatusYet
                : feed.StaleSince is { } since ? $"STALE: no status since {RoofStatusText.Time(since)}. Showing the last known state; Stop still works."
                : staleSince is { } read ? $"STALE: status from a command's answer at {RoofStatusText.Time(read)}; live status is not connected. Stop still works."
                : null;
        }

        var command = CommandStateLocked();
        var notOffered = NotOfferedLocked();
        return new KioskView
        {
            IsStarted = _started,
            UnlockedBy = _session?.Name,
            Role = _session?.Role,
            Status = _status,
            FeedState = state,
            FeedLabel = feed is null ? "not connected"
                : refused ? "refused"
                : unreachable ? "unreachable"
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
            IsUnreachable = unreachable,
            ModeWarnings = _status is null ? [] : RoofStatusText.DescribeModeWarnings(_status),
            Busy = _busy,
            OpenBlock = notOffered ?? RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, command),
            CloseBlock = notOffered ?? RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Closing, command),
            ClearFaultBlock = notOffered ?? RoofCommandRules.GetClearFaultBlockReason(command),
            HoldsLease = _holdsLease,
            StopMessage = _stopMessage,
            StopOutcome = _stopOutcome,
            StopInFlight = _stopsInFlight > 0,
            IsBlank = _blank,
            ScreenTimeout = _options.Blanking ? _screenTimeout : TimeSpan.Zero,
            IdleLock = _options.IdleLock,
            Notices = _notices.ToArray()
        };
    }

    private sealed record Command(RoofSessionResponse Session, long StopsSent, CancellationToken Closing, DateTimeOffset SentAt);
}
