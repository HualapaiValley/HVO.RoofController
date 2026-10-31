using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// The operator lease an operator holds from the web UI (#220): while at least one of their pages is open, their session
/// renews it (<see cref="RoofOperatorLeaseKeeper"/>), and while any person holds it the controller refuses Alpaca commands.
/// Each live page holds it (<see cref="Hold"/>) from the moment its live connection starts until the page closes; the
/// last page closing, the session ending and signing out release it. What the person does on any of their pages
/// (<see cref="NoteInput"/>: a click, a key, a form, a navigation, never a timer or a status) keeps it from idling out.
/// </summary>
/// <remarks>
/// <para>While Alpaca commands are on, the controller lets a lease lapse after its idle limit without input. The keeper's
/// next heartbeat, which carries no input, would take it again; so once the idle deadline the controller gave this
/// person has passed with no input since, this session stops renewing it (releasing it if it is still held) and says
/// <see cref="RoofAlpacaText.MayNowMove"/> until the person does something, which takes it again. The deadline is known
/// only while the controller reports this person as the holder (it reports the oldest lease).</para>
/// <para>Thread-safe. <see cref="Changed"/> is raised outside the lock, on any thread.</para>
/// </remarks>
public sealed class WebOperatorLease
{
    private readonly RoofControllerClient _client;
    private readonly string _person;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly ITimer _idleTimer;
    private RoofOperatorLeaseKeeper? _keeper;
    private Task _stopping = Task.CompletedTask;
    private int _holds;
    private bool _closed;
    private bool _idledOut;
    private long _inputs;
    private long _inputsAtAnswer;

    internal WebOperatorLease(RoofControllerClient client, string person, TimeProvider time, ILogger logger)
    {
        _client = client;
        _person = person;
        _time = time;
        _logger = logger;
        _idleTimer = time.CreateTimer(_ => OnIdleDeadline(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after <see cref="State"/>, <see cref="IdledOut"/> or <see cref="IsRenewing"/> changed, on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the session renews the lease: a page is open, and it has neither idled out nor been closed.</summary>
    public bool IsRenewing
    {
        get { lock (_gate) { return _keeper is not null; } }
    }

    /// <summary>The controller's last answer to this session's heartbeats; null before the first, and while not renewing.</summary>
    public RoofOperatorLeaseState? State
    {
        get { lock (_gate) { return _keeper?.State; } }
    }

    /// <summary>True once the lease idled out for want of input, until the person does something on one of their pages.</summary>
    public bool IdledOut
    {
        get { lock (_gate) { return _idledOut; } }
    }

    /// <summary>
    /// What this person is told about their lease idling out (<see cref="RoofAlpacaText.DescribeIdleWarning"/>): the warning
    /// in its last minute, <see cref="RoofAlpacaText.MayNowMove"/> once it idled out, or null.
    /// </summary>
    public string? IdleWarning
    {
        get
        {
            lock (_gate)
            {
                return _idledOut ? RoofAlpacaText.MayNowMove : RoofAlpacaText.DescribeIdleWarning(_keeper?.State, _person);
            }
        }
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

            // Opening the first page is the person being here: it takes the lease, even after it idled out.
            started = ++_holds == 1;
            if (started)
            {
                _idledOut = false;
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
    /// The person did something on one of their pages: the next heartbeat resets the idle clock, and a lease that idled out
    /// is taken again. Never call it for a timer or a status update.
    /// </summary>
    public void NoteInput()
    {
        RoofOperatorLeaseKeeper? keeper;
        var restarted = false;
        var warned = false;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _inputs++;
            if (_idledOut && _holds > 0)
            {
                _idledOut = false;
                StartLocked();
                restarted = true;
            }

            keeper = _keeper;
            warned = !restarted && keeper is not null && RoofAlpacaText.DescribeIdleWarning(keeper.State, _person) is not null;
        }

        if (restarted)
        {
            _logger.LogInformation("{Name} did something after their operator lease idled out: the web UI takes it again", _person);
            RaiseChanged();
            return;
        }

        keeper?.NoteInput();
        if (warned)
        {
            // The person was warned the lease is about to lapse: their input goes now, not at the next heartbeat.
            _ = BeatNowAsync(keeper!);
        }
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
            if (!_closed)
            {
                _closed = true;
                _idleTimer.Dispose();
            }

            _idledOut = false;
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

            _idledOut = false;
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
        _inputsAtAnswer = _inputs;

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
        if (!_closed)
        {
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

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

            // The answer carries the input noted before it was sent; anything noted from now on goes with the next one.
            _inputsAtAnswer = _inputs;
            var state = _keeper!.State;
            if (state is { Held: true, IdleSecondsRemaining: { } seconds } && string.Equals(state.Holder, _person, StringComparison.Ordinal))
            {
                _idleTimer.Change(TimeSpan.FromSeconds(Math.Max(0, seconds)), Timeout.InfiniteTimeSpan);
            }
            else
            {
                _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        RaiseChanged();
    }

    // The idle deadline the controller gave has passed: unless the person did something since its answer, the controller
    // lapses the lease, and the next heartbeat (without input) must not take it again.
    private void OnIdleDeadline()
    {
        PendingStop? stop;
        lock (_gate)
        {
            if (_closed || _keeper is null || _inputs != _inputsAtAnswer)
            {
                return;
            }

            _idledOut = true;
            stop = StopLocked();
        }

        Begin(stop);
        _logger.LogInformation("The operator lease of {Name} idled out after no input: the web UI stopped renewing it, so Alpaca may move the roof", _person);
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
