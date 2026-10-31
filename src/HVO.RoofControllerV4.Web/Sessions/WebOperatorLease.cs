using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// The operator lease an operator holds from the web UI (#220): while at least one of their pages is open, their session
/// renews it (<see cref="RoofOperatorLeaseKeeper"/>), and while any person holds it the controller refuses Alpaca commands.
/// Each live page holds it (<see cref="Hold"/>) from the moment its live connection starts until the page closes; the
/// last page closing, the session ending and signing out release it. What the person does on any of their pages
/// (<see cref="NoteInput"/>: a click, a key, a navigation, Stop; never a timer or a status) keeps it from idling out.
/// </summary>
/// <remarks>
/// <para>While Alpaca commands are on, the controller lets a lease lapse after its idle limit without input, and a
/// heartbeat without input never takes it back: the person is told <see cref="RoofAlpacaText.MayNowMove"/> until they do
/// something.</para>
/// <para>A person who grants an override steps away (<see cref="GrantAsync"/>): the controller ends their lease with the
/// grant, since a grant never lifts a person's lease, and the heartbeats go on without input until the person does
/// something, which takes the lease back and so drops the grant. The input that sent the grant does not count.</para>
/// <para>Thread-safe. <see cref="Changed"/> is raised outside the lock, on any thread.</para>
/// </remarks>
public sealed class WebOperatorLease
{
    private readonly RoofControllerClient _client;
    private readonly string _person;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private RoofOperatorLeaseKeeper? _keeper;
    private Task _stopping = Task.CompletedTask;
    private int _holds;
    private bool _closed;
    private bool _steppedAway;

    internal WebOperatorLease(RoofControllerClient client, string person, TimeProvider time, ILogger logger)
    {
        _client = client;
        _person = person;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after <see cref="State"/>, <see cref="SteppedAway"/> or <see cref="IsRenewing"/> changed, on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the session renews the lease: a page is open, and the session has not ended.</summary>
    public bool IsRenewing
    {
        get { lock (_gate) { return _keeper is not null; } }
    }

    /// <summary>The controller's last answer to this session's heartbeats; null before the first, and while not renewing.</summary>
    public RoofOperatorLeaseState? State
    {
        get { lock (_gate) { return _keeper?.State; } }
    }

    /// <summary>
    /// True from the person's grant until they do something on one of their pages, or a heartbeat finds a lease of theirs
    /// again (taking one back drops the grant): their lease has ended so that Alpaca may use the grant.
    /// </summary>
    public bool SteppedAway
    {
        get { lock (_gate) { return _steppedAway; } }
    }

    /// <summary>
    /// What this person is told about their lease idling out (<see cref="RoofAlpacaText.DescribeIdleWarning"/>): the warning
    /// in its last minute, <see cref="RoofAlpacaText.MayNowMove"/> once it lapsed, or null.
    /// </summary>
    public string? IdleWarning
    {
        get { lock (_gate) { return RoofAlpacaText.DescribeIdleWarning(_keeper?.State, _person); } }
    }

    /// <summary>A page's hold: the first takes the lease; disposing the last releases it.</summary>
    public IDisposable Hold()
    {
        bool started;
        lock (_gate)
        {
            if (_closed)
            {
                return new WebOperatorLeaseHold(null);
            }

            started = ++_holds == 1;
            if (started)
            {
                _steppedAway = false;
                StartLocked();
            }
        }

        if (started)
        {
            RaiseChanged();
        }

        return new WebOperatorLeaseHold(this);
    }

    /// <summary>
    /// The person did something on one of their pages: the next heartbeat resets the idle clock, and takes back a lease that
    /// lapsed or that they stepped away from. Never call it for a timer or a status update.
    /// </summary>
    public void NoteInput()
    {
        RoofOperatorLeaseKeeper? keeper;
        bool now;
        bool returned;
        lock (_gate)
        {
            if (_closed || _keeper is null)
            {
                return;
            }

            keeper = _keeper;
            returned = _steppedAway;
            _steppedAway = false;

            // Warned, lapsed or away: the input goes at once, so the page shows the lease kept or taken back.
            now = returned || RoofAlpacaText.DescribeIdleWarning(keeper.State, _person) is not null;
        }

        keeper.NoteInput();
        if (now)
        {
            _ = BeatNowAsync(keeper);
        }

        if (returned)
        {
            _logger.LogInformation("{Name} did something after stepping away: the web UI takes the operator lease back, which drops the grant", _person);
            RaiseChanged();
        }
    }

    /// <summary>
    /// Grants an override for one command of <paramref name="kind"/> lifting <paramref name="reasonKeys"/>, as the person
    /// stepping away: no heartbeat crosses the grant, the input that sent it is forgotten, and from then on the heartbeats
    /// carry no input until the person does something (<see cref="RoofOperatorLeaseKeeper.StepAwayAsync"/>).
    /// </summary>
    public async Task<RoofOverrideGrant> GrantAsync(RoofAlpacaCommandKind kind, IReadOnlyList<string> reasonKeys, CancellationToken cancellationToken)
    {
        RoofOperatorLeaseKeeper? keeper;
        lock (_gate)
        {
            keeper = _keeper;
        }

        var grant = keeper is null
            ? await _client.Alpaca.GrantAsync(kind, reasonKeys, cancellationToken).ConfigureAwait(false)
            : await keeper.StepAwayAsync(token => _client.Alpaca.GrantAsync(kind, reasonKeys, token), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _steppedAway = !_closed;
        }

        _logger.LogInformation("{Name} granted one Alpaca {Kind} and stepped away from the operator lease", _person, kind);
        RaiseChanged();
        return grant;
    }

    /// <summary>
    /// Releases the lease and renews it no more (signing out, the session ending). Waits for the release, which is best
    /// effort: the lease lapses by itself when the heartbeats stop.
    /// </summary>
    public async Task CloseAsync()
    {
        PendingStop? stop;
        Task stopping;
        lock (_gate)
        {
            _closed = true;
            _steppedAway = false;
            stop = StopLocked();
            stopping = _stopping;
        }

        if (stop is not null)
        {
            Begin(stop);
            RaiseChanged();
        }

        await stopping.ConfigureAwait(false);
    }

    /// <summary>The keeper renewing the lease now, or null (tests send its heartbeats themselves).</summary>
    internal RoofOperatorLeaseKeeper? Keeper
    {
        get { lock (_gate) { return _keeper; } }
    }

    /// <summary>The release that is under way, or the last one (tests wait for it).</summary>
    internal Task Stopping
    {
        get { lock (_gate) { return _stopping; } }
    }

    internal void Release()
    {
        PendingStop? stop;
        lock (_gate)
        {
            if (_closed || _holds == 0 || --_holds > 0)
            {
                return;
            }

            _steppedAway = false;
            stop = StopLocked();
        }

        Begin(stop);
        RaiseChanged();
    }

    private void StartLocked()
    {
        var keeper = new RoofOperatorLeaseKeeper(_client, _time, _logger);
        keeper.StateChanged += OnKeeperStateChanged;
        _keeper = keeper;

        // A release still on its way (a page reloaded: its old page let go as the new one held) goes first, so it cannot
        // remove the lease the new heartbeats hold. A keeper stopped meanwhile does not start.
        if (_stopping.IsCompleted)
        {
            keeper.Start();
        }
        else
        {
            _ = _stopping.ContinueWith(_ => keeper.Start(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // Takes the keeper away and reserves its release, in order after any release before it; Begin sends it once the lock
    // is released (it is a request to the controller).
    private PendingStop? StopLocked()
    {
        if (_keeper is not { } keeper)
        {
            return null;
        }

        _keeper = null;
        keeper.StateChanged -= OnKeeperStateChanged;
        var stop = new PendingStop(keeper, _stopping, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _stopping = stop.Done.Task;
        return stop;
    }

    private void Begin(PendingStop? stop)
    {
        if (stop is not null)
        {
            _ = StopAsync(stop);
        }
    }

    // Disposing the keeper stops its heartbeats at once and releases the lease; a failure only means it lapses by itself.
    // Never throws.
    private async Task StopAsync(PendingStop stop)
    {
        try
        {
            await stop.Previous.ConfigureAwait(false);
            await stop.Keeper.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogDebug(error, "Releasing the operator lease of {Name} failed; it lapses by itself", _person);
        }
        finally
        {
            stop.Done.TrySetResult();
        }
    }

    private async Task BeatNowAsync(RoofOperatorLeaseKeeper keeper)
    {
        try
        {
            await keeper.BeatAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogDebug(error, "The operator lease heartbeat for {Name}'s input failed; the next one carries it", _person);
        }
    }

    private void OnKeeperStateChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _keeper) || _closed)
            {
                return;
            }

            // No heartbeat crosses a grant, so this answer came after it: a lease of the person's held again (another of
            // their sessions took it back) has dropped the grant.
            if (_steppedAway && _keeper!.State is { Held: true } state && string.Equals(state.Holder, _person, StringComparison.Ordinal))
            {
                _steppedAway = false;
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "An operator lease handler failed");
        }
    }

    private sealed record PendingStop(RoofOperatorLeaseKeeper Keeper, Task Previous, TaskCompletionSource Done);

    private sealed class WebOperatorLeaseHold(WebOperatorLease? lease) : IDisposable
    {
        private WebOperatorLease? _lease = lease;

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Release();
    }
}
