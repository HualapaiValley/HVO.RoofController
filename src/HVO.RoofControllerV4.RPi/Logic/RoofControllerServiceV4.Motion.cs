using System.Diagnostics;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

// Motion commands: admission (ordinary and Alpaca), waiting starts, the command generation and its relay fence, and the
// bounded Stop.
public partial class RoofControllerServiceV4
{
    /// <summary>
    /// The longest Stop (and a protective path) waits for the controller lock. Past it, Stop answers that the relays are
    /// unverified (<see cref="RoofControllerErrorCode.RelayStateUnverified"/>) and its relays-off write stays queued: it
    /// runs as soon as the lock is free. The lock is held only for checks and HAT I/O, so this is reached only while a
    /// HAT access hangs (the emulator's request timeout, or an I2C transfer at its adapter timeout).
    /// </summary>
    internal static readonly TimeSpan StopLockWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest one HAT access may take (#220, §17 AA5): a relay write, a relay read-back or an input read. Shorter
    /// than <see cref="StopLockWait"/>, so a Stop waiting behind a hanging access gets the lock once it has failed.
    /// </summary>
    internal static readonly TimeSpan DefaultHatIoLimit = TimeSpan.FromMilliseconds(1500);

    /// <summary>This controller's HAT access limit (<see cref="DefaultHatIoLimit"/>; tests may shorten it).</summary>
    internal TimeSpan HatIoLimit { get; set; } = DefaultHatIoLimit;

    /// <summary>True while a HAT access that ran past its limit has not returned (tests).</summary>
    internal bool IsHatIoOverrun => _hatIo.IsOverrun;

    // The command generation (#220): moves with every admitted start or reversal and every relays-off path (Stop, a
    // protective stop, a limit, a timeout, shutdown), never with a refusal, a repeat or a no-op. An Alpaca ticket records
    // it; a waiting start re-checks it; the relay fence compares against it. Read and written without the lock.
    private long _commandGeneration;

    // The generation of the latest relays-off request. A direction or STOP-permit ON whose start generation is not newer
    // is never written (RelayWriteOutcome.Fenced): a Stop that came after the start's admission wins at the last moment.
    private long _relayOffFence;

    // When the last verified relay-off that ended a drive command happened (controller time), for the direction-change delay.
    private DateTimeOffset? _lastDriveOffUtc;

    // The start waiting out the direction-change delay, or null.
    private PendingStart? _pendingStart;

    // A start that waited out the delay was refused when its wait ended (N6): until the next start, Stop or ClearFault.
    private bool _continuationRefused;

    // Who the commanded motion belongs to; null while idle.
    private RoofMotionOwner? _motionOwner;

    // The last snapshot published, for an answer that cannot take the lock (a Stop that waited StopLockWait).
    private volatile RoofStatusResponse? _lastPublishedSnapshot;

    private int _deferredStops;

    /// <summary>The command generation (#220). Moves with every admitted start or reversal and every relays-off path.</summary>
    public long CommandGeneration => Interlocked.Read(ref _commandGeneration);

    /// <summary>This controller process's random identifier (<see cref="RoofStatusResponse.ControllerInstanceId"/>).</summary>
    public string InstanceId => _controllerInstanceId;

    /// <summary>Stops queued because they could not take the lock within <see cref="StopLockWait"/>, not yet run.</summary>
    internal int DeferredStopCount => Volatile.Read(ref _deferredStops);

    /// <summary>
    /// Test seam: runs, under the controller lock, between a start's final checks and its relay write, so a test can fire
    /// a Stop or a protective stop exactly there. Never set in production.
    /// </summary>
    internal Action? BeforeEnergizeHook { get; set; }

    /// <summary>Moves the command generation and returns the new value.</summary>
    private long BumpGeneration() => Interlocked.Increment(ref _commandGeneration);

    /// <summary>
    /// A relays-off request: moves the generation and raises the relay fence to it, so no ON of an earlier admission is
    /// written after this point. Returns the new generation.
    /// </summary>
    private long FenceRelaysOff()
    {
        BoundPendingFence();
        var generation = BumpGeneration();
        long current;
        do
        {
            current = Interlocked.Read(ref _relayOffFence);
            if (current >= generation)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _relayOffFence, generation, current) != current);

        return generation;
    }

    /// <summary>True when an ON for a start admitted at <paramref name="generation"/> may still be written.</summary>
    private bool OnAllowed(long generation) => Interlocked.Read(ref _relayOffFence) < generation;

    #region Motion commands

    /// <inheritdoc />
    public Result<RoofControllerStatus> Open() => StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening)).GetAwaiter().GetResult();

    /// <inheritdoc />
    public Result<RoofControllerStatus> Close() => StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing)).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<Result<RoofControllerStatus>> StartMotionAsync(RoofMotionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Direction is not (RoofMotionDirection.Opening or RoofMotionDirection.Closing))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A motion request is Opening or Closing.");
        }

        var command = request.Direction == RoofMotionDirection.Opening ? "open" : "close";
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RoofControllerTelemetry.StartCommand(command);
        Result<RoofControllerStatus> result;
        try
        {
            result = await StartMotionCoreAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_syncLock)
            {
                _logger.LogError(ex, "Unexpected error while starting {Direction}; stopping", request.Direction);
                if (_commandedMotion != RoofMotionDirection.None)
                {
                    StopMotion_NoLock(RoofControllerStopReason.EmergencyStop, $"Unexpected error while starting {request.Direction}: {ex.Message}");
                }

                FinishMutation_NoLock();
                result = Result<RoofControllerStatus>.Failure(Rejection_NoLock(RoofControllerErrorCode.Unknown, $"Unexpected error while starting {request.Direction}.", ex));
            }
        }

        RoofControllerTelemetry.CompleteCommand(activity, command, result.IsSuccessful, startTimestamp);
        return result;
    }

    private async Task<Result<RoofControllerStatus>> StartMotionCoreAsync(RoofMotionRequest request)
    {
        PendingStart? pending;
        Task<Result<RoofControllerStatus>>? joined;
        Result<RoofControllerStatus> admitted;
        lock (_syncLock)
        {
            admitted = Admit_NoLock(request, out pending, out joined);
        }

        if (joined is not null)
        {
            return await joined.ConfigureAwait(false);
        }

        if (pending is null)
        {
            return admitted;
        }

        // The direction-change delay, waited without the lock: Stop, a protective stop or another command can act meanwhile.
        while (true)
        {
            var wait = pending.ReadyAtUtc - CurrentControllerTime();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, pending.Cancellation.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            }

            lock (_syncLock)
            {
                if (pending.Outcome.Task.IsCompleted)
                {
                    break;
                }

                Result<RoofControllerStatus>? outcome;
                try
                {
                    outcome = Continue_NoLock(pending);
                }
                catch (Exception ex)
                {
                    // Every caller that joined the waiting start gets an answer (PR #230 review, Opus F6), and nothing
                    // it may have started keeps running.
                    _logger.LogError(ex, "Unexpected error while continuing a waiting {Direction}; it is refused", pending.Direction);
                    if (_commandedMotion != RoofMotionDirection.None)
                    {
                        StopMotion_NoLock(RoofControllerStopReason.EmergencyStop, $"Unexpected error while continuing a waiting {pending.Direction}: {ex.Message}");
                    }

                    outcome = Result<RoofControllerStatus>.Failure(Rejection_NoLock(RoofControllerErrorCode.Unknown,
                        $"Unexpected error while continuing a waiting {pending.Direction}; nothing moved for it.", ex));
                }

                if (outcome is { } done)
                {
                    Complete_NoLock(pending, done);
                    break;
                }
            }
        }

        return await pending.Outcome.Task.ConfigureAwait(false);
    }

    private DateTimeOffset CurrentControllerTime()
    {
        lock (_syncLock)
        {
            return Now;
        }
    }

    /// <summary>
    /// Admits (or refuses) a start, under the lock, in the order of #220's rules. Returns the answer, or a waiting start
    /// (<paramref name="pending"/>) the caller waits out, or a waiting start of the same direction the caller joins
    /// (<paramref name="joined"/>).
    /// </summary>
    private Result<RoofControllerStatus> Admit_NoLock(RoofMotionRequest request, out PendingStart? pending, out Task<Result<RoofControllerStatus>>? joined)
    {
        pending = null;
        joined = null;
        var direction = request.Direction;
        var admission = request.Admission;

        if (_shuttingDown || _disposed)
        {
            return RejectShuttingDown_NoLock<RoofControllerStatus>();
        }

        if (!_initialized)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.NotInitialized, "The roof controller is not initialized.");
        }

        // Step 1 (Alpaca): commands on for this kind, the account's authorization unchanged.
        if (admission?.CheckGate() is { } gate)
        {
            return Refuse_NoLock(gate);
        }

        // An ordinary command keeps its place for a clear-fault pulse; an Alpaca command's comes after the use-up point.
        if (admission is null && _clearFaultInProgress)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.OperationInProgress, "A clear-fault pulse is in progress.");
        }

        // Step 2: the hard protections.
        if (_faultLatched)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.FaultLatched,
                $"A safety fault is latched ({_latchedFaultReason}); run ClearFault before commanding motion.");
        }

        var now = Now;

        // A deadline can pass before the watchdog timer callback or the next supervision cycle acts on it. Enforce it
        // first, so neither a repeat nor a reversal revives an expired lease or outlives a missed watchdog or at-speed
        // deadline, and the stop is recorded (and latched) with the deadline's reason.
        if (_commandedMotion != RoofMotionDirection.None)
        {
            var repeat = _commandedMotion == direction;

            // A deadline stop is bounded through its LEDs here; a reversal it leaves to go ahead is a start, unbounded.
            using (BoundedSection())
            {
                CheckDeadlines_NoLock(now);

                // The inputs re-read at an input deadline can show the destination limit: the roof arrived, so a
                // repeat has nothing left to do.
                if (repeat && _commandedMotion == RoofMotionDirection.None && _lastMotionStopReason == RoofControllerStopReason.LimitSwitchReached
                    && !_faultLatched && _relayRegisterState == RoofRelayRegisterState.Verified)
                {
                    FinishMutation_NoLock();
                    return Result<RoofControllerStatus>.Success(_status);
                }

                if (_commandedMotion == RoofMotionDirection.None
                    && (repeat || _faultLatched || _relayRegisterState != RoofRelayRegisterState.Verified))
                {
                    FinishMutation_NoLock();
                    return RejectAfterDeadlineStop_NoLock<RoofControllerStatus>();
                }
            }

            // Only the lease expired, or the destination limit was reached, under a reversal: the roof is now
            // stopped (verified) and the reversal is a start.
        }

        // A start from rest reads the inputs fresh here: the drive fault, the limits and the run input are hard
        // protections, and the destination limit decides a no-op. (A reversal reads them after its stop, below.)
        var idle = _commandedMotion == RoofMotionDirection.None && _pendingStart is null;
        if (idle)
        {
            if (InputCheck_NoLock(checkRunInput: true) is { } refused)
            {
                return refused;
            }
        }

        // Step 3 (Alpaca): a person holding the operator lease, the human Stop hold.
        if (admission?.CheckHumanControl() is { } human)
        {
            return Refuse_NoLock(human);
        }

        // Classify: join a waiting start, a repeat, a reversal, a no-op, or a start.
        var reversal = false;
        if (_pendingStart is { } waiting)
        {
            if (waiting.Direction == direction && (admission is not null || !waiting.Owner.IsAlpaca || request.Owner.IsAlpaca))
            {
                // The same direction: joins the waiting start and gets its real answer, never an early success. A person's
                // command during the Alpaca account's waiting start is not a repeat: it takes the start over (below).
                joined = waiting.Outcome.Task;
                return default!;
            }

            reversal = waiting.Direction != direction;
        }
        else if (_commandedMotion == direction)
        {
            return Repeat_NoLock(request, now);
        }
        else if (_commandedMotion != RoofMotionDirection.None)
        {
            reversal = true;
        }
        else if (DestinationActive_NoLock(direction))
        {
            _logger.LogInformation("{Direction} requested but the destination limit is already active; no motion", direction);
            FinishMutation_NoLock();
            return Result<RoofControllerStatus>.Success(_status);
        }

        var delay = _options.DirectionChangeDelay;
        var wait = reversal && _commandedMotion != RoofMotionDirection.None ? delay : RemainingDelay_NoLock(now);
        if (admission is not null)
        {
            if (admission.CheckDeadline(wait) is { } late)
            {
                return Refuse_NoLock(late);
            }

            // The use-up point: whatever happens from here on, any grant of this kind is spent.
            admission.UseGrant();

            if (admission.CheckGeneration(CommandGeneration) is { } superseded)
            {
                return Refuse_NoLock(superseded);
            }

            if (_clearFaultInProgress)
            {
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.OperationInProgress, "A clear-fault pulse is in progress.");
            }

            // The Alpaca server never reverses a person's motion, nor one nobody in particular started.
            if (reversal && !(_pendingStart?.Owner ?? _motionOwner ?? RoofMotionOwner.Unleased).IsAlpaca)
            {
                return Refuse_NoLock(AlpacaRefusal("PersonsMotion",
                    "The roof is moving (or about to) on a person's command, and an Alpaca client never reverses a person's motion."));
            }

            if (admission.CheckPolicy() is { } policy)
            {
                return Refuse_NoLock(policy);
            }
        }

        // A waiting start in either direction gives way to this one: a reversal of it, or a person taking over the Alpaca
        // account's waiting start.
        if (_pendingStart is { } replaced)
        {
            Cancel_NoLock(replaced, Rejection_NoLock(RoofControllerErrorCode.Superseded,
                replaced.Direction == direction
                    ? "A person's command took over this waiting start; it now runs as theirs."
                    : $"A {direction} command replaced this waiting {replaced.Direction}; nothing moved for it.").WithRule("Superseded"));
        }

        // Reversal of a running motion: stop (verified) before anything else.
        if (_commandedMotion != RoofMotionDirection.None)
        {
            if (!StopMotion_NoLock(RoofControllerStopReason.NormalStop, null))
            {
                FinishMutation_NoLock();
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified,
                    "Reversal refused: the stop before reversing could not verify the relay register.");
            }

            // The drive run input is checked now when the start energizes at once (with the coast stop IN4 drops within
            // milliseconds); a start that waits out the direction-change delay checks it at its continuation instead.
            now = Now;
            if (InputCheck_NoLock(checkRunInput: RemainingDelay_NoLock(now) <= TimeSpan.Zero) is { } refused)
            {
                return refused;
            }

            now = Now;
        }

        return StartOrWait_NoLock(request, now, out pending);
    }

    /// <summary>
    /// Every start reads the safety inputs fresh and refuses on the drive fault, contradictory limits, a fault the read
    /// latched, and (with <paramref name="checkRunInput"/> and the at-speed interlock) a drive still reporting running.
    /// </summary>
    private Result<RoofControllerStatus>? InputCheck_NoLock(bool checkRunInput)
    {
        if (!ReadInputs_NoLock())
        {
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.HardwareUnavailable,
                "Safety inputs could not be read; motion refused.");
        }

        Evaluate_NoLock(Now);
        if (DriveFaultActive_NoLock == true)
        {
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.InterlockActive, "The drive fault input (IN3) is active; motion refused.");
        }

        if (LimitsContradictory_NoLock)
        {
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.InterlockActive, "Both limit switches report active; motion refused.");
        }

        if (_faultLatched)
        {
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.FaultLatched,
                $"A safety fault is latched ({_latchedFaultReason}); run ClearFault before commanding motion.");
        }

        // With the run interlock configured, the start must be confirmed by IN4 rising. A drive that already reports
        // running (ramping down after a ramp stop, in a DC brake with the Run output on, or running without a
        // command) would confirm it at once, so the start is refused until IN4 drops. With the coast stop IN4 drops
        // within milliseconds, so a reversal proceeds while the roof is still coasting.
        if (checkRunInput && _options.AtSpeedConfirmationTimeout is not null && _rawIn4 == true)
        {
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.InterlockActive,
                "The drive still reports running (IN4); motion refused until it stops.");
        }

        return null;
    }

    private bool DestinationActive_NoLock(RoofMotionDirection direction)
        => (direction == RoofMotionDirection.Opening ? OpenLimitActive_NoLock : ClosedLimitActive_NoLock) == true;

    /// <inheritdoc />
    public TimeSpan GetStartDelay(RoofMotionDirection direction)
    {
        lock (_syncLock)
        {
            var now = Now;
            if (_pendingStart is { } waiting)
            {
                if (waiting.Direction != direction)
                {
                    return _options.DirectionChangeDelay;
                }

                var left = waiting.ReadyAtUtc - now;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }

            if (_commandedMotion == direction)
            {
                return TimeSpan.Zero;
            }

            return _commandedMotion != RoofMotionDirection.None ? _options.DirectionChangeDelay : RemainingDelay_NoLock(now);
        }
    }

    /// <summary>How much of the direction-change delay is still to run before a start may energize a relay.</summary>
    private TimeSpan RemainingDelay_NoLock(DateTimeOffset now)
    {
        var delay = _options.DirectionChangeDelay;
        if (delay <= TimeSpan.Zero || _lastDriveOffUtc is not { } off)
        {
            return TimeSpan.Zero;
        }

        var remaining = off + delay - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// A repeat of the current command. An ordinary repeat renews the operator lease only; it never extends the
    /// watchdog, which is an absolute cap measured from motion start. A person's repeat of the Alpaca account's motion
    /// makes it theirs. An Alpaca repeat changes nothing.
    /// </summary>
    private Result<RoofControllerStatus> Repeat_NoLock(RoofMotionRequest request, DateTimeOffset now)
    {
        if (request.Admission is null)
        {
            if (_motionOwner?.IsAlpaca == true && !request.Owner.IsAlpaca)
            {
                _logger.LogInformation("{Owner}'s {Direction} takes over the Alpaca account's motion", request.Owner.Name ?? "A person", _commandedMotion);
                _motionOwner = request.Owner;
                _leaseDeadlineUtc = _options.OperatorLeaseTimeout is { } takeover ? now + takeover : null;
            }
            else if (_options.OperatorLeaseTimeout is { } leaseTimeout && _leaseDeadlineUtc is not null)
            {
                _leaseDeadlineUtc = now + leaseTimeout;
            }
        }

        FinishMutation_NoLock();
        return Result<RoofControllerStatus>.Success(_status);
    }

    /// <summary>
    /// The start itself: energizes now, or, while the direction-change delay still runs, registers a waiting start that
    /// is checked again before its relay. Either way the command generation moves (an admitted start).
    /// </summary>
    private Result<RoofControllerStatus> StartOrWait_NoLock(RoofMotionRequest request, DateTimeOffset now, out PendingStart? pending)
    {
        pending = null;
        _continuationRefused = false;
        var generation = BumpGeneration();
        var remaining = RemainingDelay_NoLock(now);
        if (remaining > TimeSpan.Zero)
        {
            pending = new PendingStart(request.Direction, generation, request.Owner, request.Admission, now + remaining);
            _pendingStart = pending;
            _logger.LogInformation("Roof {Direction} admitted for {Owner}; waiting {Wait} ms for the direction-change delay before the relay",
                request.Direction, request.Owner.Kind, Math.Ceiling(remaining.TotalMilliseconds));
            FinishMutation_NoLock();
            return default!;
        }

        return Energize_NoLock(request.Direction, generation, request.Owner, now);
    }

    /// <summary>
    /// A waiting start's continuation, under the lock: everything checked again with fresh inputs, then the relay. Returns
    /// the answer, or null when the delay has to run longer.
    /// </summary>
    private Result<RoofControllerStatus>? Continue_NoLock(PendingStart pending)
    {
        if (!ReferenceEquals(_pendingStart, pending))
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.Superseded, "A Stop or another command came first; nothing moved for this one.");
        }

        if (_shuttingDown || _disposed)
        {
            return RejectShuttingDown_NoLock<RoofControllerStatus>();
        }

        if (CommandGeneration != pending.Generation)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.Superseded,
                "A Stop, a protective stop or another command came while this start waited; nothing moved for it.");
        }

        var now = Now;
        if (RemainingDelay_NoLock(now) > TimeSpan.Zero)
        {
            pending.ReadyAtUtc = now + RemainingDelay_NoLock(now);
            return null;
        }

        // From here the start is being continued: nothing else can join or cancel it, and every answer below is its own.
        _pendingStart = null;
        FinishMutation_NoLock();

        if (_clearFaultInProgress)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.OperationInProgress, "A clear-fault pulse is in progress.");
        }

        if (_faultLatched)
        {
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.FaultLatched,
                $"A safety fault is latched ({_latchedFaultReason}); run ClearFault before commanding motion.");
        }

        if (InputCheck_NoLock(checkRunInput: true) is { } refused)
        {
            return refused;
        }

        if (DestinationActive_NoLock(pending.Direction))
        {
            _logger.LogInformation("Waiting {Direction} found the destination limit already active; no motion", pending.Direction);
            FinishMutation_NoLock();
            return Result<RoofControllerStatus>.Success(_status);
        }

        if (pending.Admission?.Recheck() is { } recheck)
        {
            // The status keeps why (N6): the roof stays stopped, and the Alpaca status action shows the reason.
            _lastError = $"A waiting {pending.Direction.ToString().ToLowerInvariant()} was refused when its direction-change delay ended: {recheck.Message}";
            _continuationRefused = true;
            FinishMutation_NoLock();
            var refusal = Refuse_NoLock(recheck);
            pending.Admission.ContinuationRefused((RoofControllerException)refusal.Error!);
            return refusal;
        }

        return Energize_NoLock(pending.Direction, pending.Generation, pending.Owner, Now);
    }

    /// <summary>Ends a waiting start with <paramref name="outcome"/>; every caller that joined it gets the same answer.</summary>
    private void Complete_NoLock(PendingStart pending, Result<RoofControllerStatus> outcome)
    {
        if (ReferenceEquals(_pendingStart, pending))
        {
            _pendingStart = null;
        }

        pending.Outcome.TrySetResult(outcome);
        FinishMutation_NoLock();
    }

    /// <summary>Cancels a waiting start: it is refused with <paramref name="refusal"/> and wakes at once.</summary>
    private void Cancel_NoLock(PendingStart pending, RoofControllerException refusal)
    {
        if (ReferenceEquals(_pendingStart, pending))
        {
            _pendingStart = null;
        }

        if (pending.Outcome.TrySetResult(Result<RoofControllerStatus>.Failure(refusal)))
        {
            _logger.LogInformation("Waiting {Direction} cancelled: {Reason}", pending.Direction, refusal.Message);
        }

        pending.Cancellation.Cancel();
    }

    /// <summary>Cancels any waiting start because a relays-off path ran (Stop, a protective stop, shutdown).</summary>
    private void CancelPendingStart_NoLock(string why)
    {
        if (_pendingStart is { } pending)
        {
            Cancel_NoLock(pending, Rejection_NoLock(RoofControllerErrorCode.Superseded, why).WithRule("Superseded"));
        }
    }

    /// <summary>
    /// Arms supervision and energizes the direction, the relay ONs fenced at <paramref name="generation"/>. A relays-off
    /// request after the admission (a Stop) wins: nothing is energized, the relays are driven off and the start is
    /// refused as superseded, with no fault latched.
    /// </summary>
    private Result<RoofControllerStatus> Energize_NoLock(RoofMotionDirection direction, long generation, RoofMotionOwner owner, DateTimeOffset now)
    {
        // Arm all supervision before energizing.
        var departureLimit = direction == RoofMotionDirection.Opening ? ClosedLimitActive_NoLock : OpenLimitActive_NoLock;
        _motionGeneration++;
        _commandedMotion = direction;
        _motionOwner = owner;
        _lastMotionDirection = direction;
        _motionStartUtc = now;
        _lastMotionStopUtc = null;
        _driveStopDelayPending = null;
        _lastError = null;
        ArmWatchdog_NoLock(now);

        // The Alpaca account's lease is permanent: its motion has no renewable lease deadline (the watchdog still caps it).
        _leaseDeadlineUtc = !owner.IsAlpaca && _options.OperatorLeaseTimeout is { } lease ? now + lease : null;
        _atSpeedDeadlineUtc = _options.AtSpeedConfirmationTimeout is { } atSpeed ? now + atSpeed : null;
        _atSpeedConfirmed = false;
        _runLostUtc = null;
        _driveStoppedAtStart = _rawIn4 == false;
        _startedAtLimit = departureLimit == true;
        _departureReleaseVerified = departureLimit != true;
        _releaseObservedUtc = null;
        _departureDeadlineUtc = departureLimit == true && _options.DepartureReleaseTimeout is { } departure ? now + departure : null;

        BeforeEnergizeHook?.Invoke();
        var outcome = SetRelayStatesAtomically(stopRelay: true, openRelay: direction == RoofMotionDirection.Opening,
            closeRelay: direction == RoofMotionDirection.Closing, fenceGeneration: generation);
        if (outcome == RelayWriteOutcome.Fenced)
        {
            // A Stop (or a protective stop) came between the checks and the relay: it wins, with nothing energized.
            CancelMotionSupervision_NoLock();
            _commandedMotion = RoofMotionDirection.None;
            _motionOwner = null;
            _logger.LogInformation("Roof {Direction} not started: a relays-off request came after its admission (generation {Generation})", direction, generation);
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.Superseded,
                $"A Stop came before the {direction.ToString().ToLowerInvariant()} relay was energized; nothing moved.");
        }

        if (outcome != RelayWriteOutcome.Verified)
        {
            StopMotion_NoLock(RoofControllerStopReason.RelayVerificationFailed,
                $"The relay register did not verify while energizing {direction}; motion aborted.");
            FinishMutation_NoLock();
            return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified,
                $"{direction} aborted: the relay register did not verify the requested state.");
        }

        _logger.LogInformation("Roof {Direction} started for {Owner}. Watchdog={Watchdog}s Lease={Lease} AtSpeedTimeout={AtSpeed} DepartingFromLimit={Departing} DepartureTimeout={DepartureTimeout}",
            direction, owner.Kind, _options.SafetyWatchdogTimeout.TotalSeconds, _leaseDeadlineUtc is null ? null : _options.OperatorLeaseTimeout,
            _options.AtSpeedConfirmationTimeout, departureLimit == true, _departureDeadlineUtc is null ? null : _options.DepartureReleaseTimeout);

        // Edges may have been missed between the read and the relay writes; the supervision cycle re-reads promptly.
        FinishMutation_NoLock();
        WakeSupervision_NoLock();
        return Result<RoofControllerStatus>.Success(_status);
    }

    private Result<RoofControllerStatus> Refuse_NoLock(RoofControllerException refusal)
    {
        _logger.LogInformation("Roof controller request rejected: {Code} {Rule} - {Message}", refusal.Code, refusal.AlpacaRefusal, refusal.Message);
        return Result<RoofControllerStatus>.Failure(refusal.WithSnapshot(BuildSnapshot_NoLock(Now, forKey: false)));
    }

    private static RoofControllerException AlpacaRefusal(string rule, string message)
        => new(RoofControllerErrorCode.AlpacaCommandRefused, message) { AlpacaRefusal = rule };

    #endregion

    #region Stop

    /// <inheritdoc />
    public Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RoofControllerTelemetry.StartCommand("stop");

        // First the generation and the relay fence, before the lock: an admitted start that has not yet written its
        // relay never will, and a waiting start is superseded.
        FenceRelaysOff();
        var pendingFence = TrackPendingFence(startTimestamp + (long)(StopLockWait.TotalSeconds * Stopwatch.Frequency));

        // One deadline for the whole Stop: its wait for the lock and every HAT access it makes, the deadline stop it may
        // make first and the LEDs included (#220, PR #230 rereviews S-F4, F19). Past it the Stop answers that the relays
        // are unverified, and its relays-off work goes on after (AllRelaysOff_NoLock queues it).
        var deadline = startTimestamp + (long)(StopLockWait.TotalSeconds * Stopwatch.Frequency);
        Result<RoofControllerStatus> result;
        var lockTaken = false;
        var reverify = false;
        var section = BoundedSection(deadline, armNow: true, ownFence: pendingFence);
        try
        {
            Monitor.TryEnter(_syncLock, StopLockWait, ref lockTaken);
            if (!lockTaken)
            {
                result = DeferStop(reason, deadline);
            }
            else if (_disposed)
            {
                result = RejectShuttingDown_NoLock<RoofControllerStatus>();
            }
            else
            {
                // A stop preempts a clear-fault pulse; the all-off sequence below also drops the clear-fault relay.
                _clearFaultCts?.Cancel();
                _continuationRefused = false;
                CancelPendingStart_NoLock("A Stop came while this start waited; nothing moved for it.");

                // A deadline that has already passed stops the motion first, so the recorded stop reason (and any latch)
                // is the deadline's rather than the operator's. When that stop verified every relay off, the Stop has
                // nothing left to write; otherwise the stop below re-asserts and verifies all-off.
                var verified = false;
                if (_commandedMotion != RoofMotionDirection.None)
                {
                    CheckDeadlines_NoLock(Now);
                    verified = _commandedMotion == RoofMotionDirection.None
                        && _relayRegisterState == RoofRelayRegisterState.Verified && _relayRegisterMask == 0;
                }

                if (!verified)
                {
                    verified = _commandedMotion != RoofMotionDirection.None
                        ? StopMotion_NoLock(reason, null)
                        : StopIdle_NoLock(reason);
                }

                FinishMutation_NoLock();

                // Decided after all of its I/O, the LEDs included: a Stop whose bound passed answers that the relays are
                // unverified, and its relays-off work goes on until a read-back shows them off (PR #230 rereview S-F4).
                var overran = BoundPassed(deadline);

                // An access the bound cut short and still out is followed by the lane's recovery, which reads the relays
                // back off; otherwise the Stop queues that read-back itself.
                reverify = verified && overran && !_hatIo.IsOverrun;
                result = verified && !overran
                    ? Result<RoofControllerStatus>.Success(_status)
                    : Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified, overran
                        ? "Stop did not finish within its 2 s bound; the relays-off work goes on until a read-back shows them off. Use the independent hardware stop."
                        : "Stop could not verify the relay register all-off state. Use the independent hardware stop.");
            }
        }
        finally
        {
            section.Dispose();
            pendingFence.Dispose();
            if (lockTaken)
            {
                Monitor.Exit(_syncLock);
            }
        }

        if (reverify)
        {
            // The relays read back off before the bound passed, during the LEDs: they are read back again once the lock is
            // free. (A relays-off sequence the bound cut short queued its own.)
            QueueDeferredStop(reason, null, onlyWhileMoving: false, stopRequest: true, deadline: deadline);
        }

        RoofControllerTelemetry.CompleteCommand(activity, "stop", result.IsSuccessful, startTimestamp);
        return result;
    }

    /// <summary>
    /// Stop could not take the lock within <see cref="StopLockWait"/>: the call holding it is blocked in HAT I/O. The stop
    /// is queued to run the moment the lock is free (its fence already keeps any earlier start's relay off), and the
    /// answer says the relays are not verified off.
    /// </summary>
    private Result<RoofControllerStatus> DeferStop(RoofControllerStopReason reason, long deadline)
    {
        QueueDeferredStop(reason, null, onlyWhileMoving: false, stopRequest: true, deadline: deadline);
        const string message = "Stop could not take the controller lock within 2 s: a relay or input access is still in progress. " +
            "The relays-off write is queued and runs as soon as it ends; until then the relay state is unverified. Use the independent hardware stop.";
        _logger.LogCritical("{Message} Reason={Reason}", message, reason);
        return Result<RoofControllerStatus>.Failure(new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, message,
            _lastPublishedSnapshot is { } last ? last with { RelayRegisterState = RoofRelayRegisterState.Unverified } : null));
    }

    /// <summary>
    /// Queues a stop to run as soon as the controller lock is free. With <paramref name="onlyWhileMoving"/> it acts only
    /// when motion (or a waiting start) is still there by then; its fence is raised at once. A Stop request's
    /// (<paramref name="stopRequest"/>) also ends a refused continuation's Error, as a Stop that took the lock does.
    /// </summary>
    private void QueueDeferredStop(RoofControllerStopReason reason, string? error, bool onlyWhileMoving, long? motionGeneration = null,
        bool stopRequest = false, long? deadline = null)
    {
        FenceRelaysOff();
        var pendingFence = TrackPendingFence(deadline ?? StopDeadlineFromNow());
        Interlocked.Increment(ref _deferredStops);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    using var appliedFence = pendingFence;
                    if (_disposed)
                    {
                        return;
                    }

                    // A protective stop meant for one motion (the watchdog's) never stops a later one.
                    if (motionGeneration is { } meant && meant != _motionGeneration)
                    {
                        return;
                    }

                    CancelPendingStart_NoLock("A stop queued behind a blocked HAT access ran; nothing moved for this start.");
                    if (stopRequest)
                    {
                        _continuationRefused = false;
                    }

                    if (_commandedMotion != RoofMotionDirection.None)
                    {
                        StopMotion_NoLock(reason, error);
                    }
                    else if (!onlyWhileMoving)
                    {
                        StopIdle_NoLock(reason);
                    }

                    _logger.LogWarning("Queued stop ({Reason}) ran once the controller lock was free; relay register {Mask} is {State}",
                        reason, FormatMask(_relayRegisterMask), _relayRegisterState);
                    FinishMutation_NoLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queued stop ({Reason}) failed", reason);
            }
            finally
            {
                pendingFence.Dispose();
                Interlocked.Decrement(ref _deferredStops);
            }
        });
    }

    #endregion

    #region Lease

    /// <inheritdoc />
    public Result<RoofStatusResponse> RenewLease()
    {
        lock (_syncLock)
        {
            // A deadline stop found here is bounded through its LEDs (PR #230 rereview S-F4).
            using var section = BoundedSection();
            if (_shuttingDown || _disposed)
            {
                return RejectShuttingDown_NoLock<RoofStatusResponse>();
            }

            if (!_initialized)
            {
                return Reject_NoLock<RoofStatusResponse>(RoofControllerErrorCode.NotInitialized, "The roof controller is not initialized.");
            }

            var now = Now;
            if (_commandedMotion == RoofMotionDirection.None || _leaseDeadlineUtc is null || _options.OperatorLeaseTimeout is not { } leaseTimeout)
            {
                return Reject_NoLock<RoofStatusResponse>(RoofControllerErrorCode.LeaseNotActive, "No leased motion is active.");
            }

            // Never revive an expired lease, or renew past a missed watchdog or at-speed deadline; stop now instead of
            // waiting for the next supervision cycle.
            CheckDeadlines_NoLock(now);
            if (_commandedMotion == RoofMotionDirection.None)
            {
                FinishMutation_NoLock();
                return RejectAfterDeadlineStop_NoLock<RoofStatusResponse>();
            }

            _leaseDeadlineUtc = now + leaseTimeout;
            FinishMutation_NoLock();
            return Result<RoofStatusResponse>.Success(BuildSnapshot_NoLock(now, forKey: false));
        }
    }

    /// <summary>
    /// Rejection for a lease renewal, repeated Open/Close or reversal that found a passed deadline and stopped motion
    /// instead, or whose input re-read at a deadline found the destination limit. (A reversal that found only an expired
    /// lease or the destination limit, with the stop verified, proceeds as a start; a repeat that found the destination
    /// limit succeeds.)
    /// </summary>
    private Result<T> RejectAfterDeadlineStop_NoLock<T>()
    {
        if (_relayRegisterState == RoofRelayRegisterState.Unverified)
        {
            return Reject_NoLock<T>(RoofControllerErrorCode.RelayStateUnverified,
                $"Motion had passed a deadline ({_lastMotionStopReason}) and the stop could not verify the relay register. Use the independent hardware stop.");
        }

        if (_faultLatched)
        {
            return Reject_NoLock<T>(RoofControllerErrorCode.FaultLatched,
                $"Motion had passed a safety deadline ({_lastMotionStopReason}); motion stopped and the fault is latched.");
        }

        if (_lastMotionStopReason == RoofControllerStopReason.LimitSwitchReached)
        {
            return Reject_NoLock<T>(RoofControllerErrorCode.LeaseNotActive, "Motion had already ended at the destination limit.");
        }

        return Reject_NoLock<T>(RoofControllerErrorCode.LeaseNotActive, "The operator lease had already expired; motion stopped.");
    }

    #endregion

    /// <summary>A start admitted while the direction-change delay still ran: it waits, without the lock, then is checked again.</summary>
    private sealed class PendingStart(RoofMotionDirection direction, long generation, RoofMotionOwner owner, IRoofCommandAdmission? admission, DateTimeOffset readyAtUtc)
    {
        public RoofMotionDirection Direction { get; } = direction;

        /// <summary>The generation the admission moved to; the continuation goes ahead only while it is still current.</summary>
        public long Generation { get; } = generation;

        public RoofMotionOwner Owner { get; } = owner;

        public IRoofCommandAdmission? Admission { get; } = admission;

        /// <summary>When the delay ends (controller time).</summary>
        public DateTimeOffset ReadyAtUtc { get; set; } = readyAtUtc;

        public TaskCompletionSource<Result<RoofControllerStatus>> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
