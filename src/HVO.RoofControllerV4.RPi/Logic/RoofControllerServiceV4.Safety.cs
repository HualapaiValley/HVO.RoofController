using System.Diagnostics;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

// Safety evaluation, stop sequences, relay transactions, input reads and background supervision.
public partial class RoofControllerServiceV4
{
    #region Input edge hooks

    /// <summary>IN1 (open limit) edge from the HAT poll loop. <paramref name="isHigh"/> is the raw electrical level.</summary>
    protected virtual void OnForwardLimitSwitchChanged(bool isHigh) => OnInputEdge(1, isHigh);

    /// <summary>IN2 (closed limit) edge from the HAT poll loop. <paramref name="isHigh"/> is the raw electrical level.</summary>
    protected virtual void OnReverseLimitSwitchChanged(bool isHigh) => OnInputEdge(2, isHigh);

    /// <summary>IN3 (drive fault) edge from the HAT poll loop. Polarity is set by <see cref="RoofControllerOptionsV4.FaultInputActiveHigh"/>.</summary>
    protected virtual void OnFaultNotificationChanged(bool isHigh) => OnInputEdge(3, isHigh);

    /// <summary>IN4 (drive at-speed) edge from the HAT poll loop.</summary>
    protected virtual void OnAtSpeedChanged(bool isHigh) => OnInputEdge(4, isHigh);

    private void OnInputEdge(int input, bool isHigh)
    {
        // A protective edge's stop has one bound from the edge, over its wait for the lock and every HAT access it makes,
        // as a Stop has; a stop another edge leads to is bounded from its decision.
        var deadline = StopDeadlineFromNow();

        // A protective edge (a drive fault, or a limit asserting) raises its fence before anything waits, so a start that
        // passed its checks while it came never energizes (#220, PR #230 review F3, rereview S-F3).
        var options = _options;
        var faultActive = input == 3 && isHigh == options.FaultInputActiveHigh;
        var limitAsserted = input is 1 or 2 && !options.IgnorePhysicalLimitSwitches && isHigh != options.UseNormallyClosedLimitSwitches;
        PendingFenceRequest? pendingFence = null;
        if ((faultActive || limitAsserted) && _initialized && !_disposed)
        {
            FenceRelaysOff();
            pendingFence = TrackPendingFence(deadline);
        }

        // The motion the edge belongs to, so a queued edge is judged with it.
        var motion = Interlocked.Read(ref _motionGeneration);

        // Never blocks the HAT's poll loop for long, and never drops an edge: past StopLockWait it is applied as soon as
        // the lock is free.
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_syncLock, StopLockWait, ref lockTaken);
            if (!lockTaken)
            {
                QueueInputEdge(input, isHigh, faultActive, motion, pendingFence);
                pendingFence = null; // The worker owns the pending request until it applies the edge.
                return;
            }

            if (_disposed || !_initialized)
            {
                return;
            }

            using var section = BoundedSection(faultActive || limitAsserted ? deadline : null, ownFence: pendingFence);
            ApplyInputEdge_NoLock(input, isHigh);
        }
        finally
        {
            pendingFence?.Dispose();
            if (lockTaken)
            {
                Monitor.Exit(_syncLock);
            }
        }
    }

    private void ApplyInputEdge_NoLock(int input, bool isHigh)
    {
        switch (input)
        {
            case 1: _rawIn1 = isHigh; break;
            case 2: _rawIn2 = isHigh; break;
            case 3: _rawIn3 = isHigh; break;
            default: _rawIn4 = isHigh; break;
        }

        _logger.LogDebug("Input IN{Input} edge RawHigh={RawHigh} (Commanded={Commanded})", input, isHigh, _commandedMotion);

        // Edge events update the cached level only; freshness comes from direct reads.
        Evaluate_NoLock(Now);
        FinishMutation_NoLock();
    }

    /// <summary>
    /// An edge that could not take the lock within <see cref="StopLockWait"/> (a HAT access holds it): applied the moment
    /// the lock is free. Its own evidence is applied first, while the motion it came during (<paramref name="motion"/>) is
    /// still the one commanded, so a limit trip that has cleared again by then still stops the roof (and latches where a
    /// trip does); then the inputs are read fresh. A drive fault seen on the edge stops any motion and latches, even if
    /// the input has cleared by then.
    /// </summary>
    private void QueueInputEdge(int input, bool isHigh, bool faultActive, long motion, PendingFenceRequest? pendingFence)
    {
        _logger.LogWarning("Input IN{Input} edge RawHigh={RawHigh} came while the controller lock was held by a blocked HAT access; it is applied as soon as the lock is free", input, isHigh);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    // Complete the applied request before this worker releases the monitor. The outer finally
                    // also covers an exception while acquiring it; disposing a request again is harmless.
                    using var appliedFence = pendingFence;
                    if (_disposed || !_initialized)
                    {
                        return;
                    }

                    if (Interlocked.Read(ref _motionGeneration) == motion)
                    {
                        // The edge's own evidence, with the motion it came during (#220, PR #230 rereview S-F3).
                        ApplyInputEdge_NoLock(input, isHigh);
                    }

                    if (ReadInputs_NoLock())
                    {
                        Evaluate_NoLock(Now);
                        FinishMutation_NoLock();
                    }

                    if (faultActive && !(_faultLatched && _latchedFaultReason == RoofControllerStopReason.DriveFault))
                    {
                        const string error = "Drive fault input (IN3) became active while the controller lock was held by a blocked HAT access.";
                        if (_commandedMotion != RoofMotionDirection.None)
                        {
                            StopMotion_NoLock(RoofControllerStopReason.DriveFault, error);
                        }
                        else
                        {
                            LatchIdleDriveFault_NoLock(error);
                        }

                        FinishMutation_NoLock();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Applying the queued IN{Input} edge failed", input);
            }
            finally
            {
                pendingFence?.Dispose();
            }
        });
    }

    #endregion

    #region Safety evaluation

    /// <summary>
    /// Applies the safety rules to the cached inputs. While moving: contradictory limits, destination limit, departure
    /// limit reassertion and drive fault stop motion, and the drive run input (IN4) is tracked. While idle: an active drive
    /// fault latches and becomes the last stop, and contradictory limits latch.
    /// </summary>
    private void Evaluate_NoLock(DateTimeOffset now)
    {
        RecordInputTransitions_NoLock();
        if (!_initialized)
        {
            return;
        }

        if (_commandedMotion != RoofMotionDirection.None)
        {
            var direction = _commandedMotion;
            var destinationActive = direction == RoofMotionDirection.Opening ? OpenLimitActive_NoLock : ClosedLimitActive_NoLock;

            if (LimitsContradictory_NoLock)
            {
                StopMotion_NoLock(RoofControllerStopReason.ContradictoryLimitInputs, "Both limit switches report active while moving (wiring or switch failure).");
            }
            else if (destinationActive == true)
            {
                StopMotion_NoLock(RoofControllerStopReason.LimitSwitchReached, null);
            }
            else if (DepartureLimitReasserted_NoLock(now))
            {
                var limitName = direction == RoofMotionDirection.Opening ? "closed" : "open";
                StopMotion_NoLock(RoofControllerStopReason.StartLimitReasserted,
                    $"The {limitName} limit asserted while {direction.ToString().ToLowerInvariant()} after it was released; the roof may be moving the wrong way.");
            }
            else if (DriveFaultActive_NoLock == true)
            {
                StopMotion_NoLock(RoofControllerStopReason.DriveFault, "Drive fault input (IN3) became active while moving.");
            }
            else
            {
                TrackDriveRunning_NoLock(now, direction);
            }
        }

        if (_commandedMotion == RoofMotionDirection.None)
        {
            RecordDriveStopped_NoLock(now);

            if (DriveFaultActive_NoLock == true)
            {
                LatchIdleDriveFault_NoLock("Drive fault input (IN3) is active.");
            }

            if (LimitsContradictory_NoLock)
            {
                Latch_NoLock(RoofControllerStopReason.ContradictoryLimitInputs, "Both limit switches report active (wiring or switch failure).");
            }
        }
    }

    /// <summary>
    /// Tracks the drive run input (IN4) while moving. HIGH confirms the start. With the interlock configured
    /// (<see cref="RoofControllerOptionsV4.AtSpeedConfirmationTimeout"/>), LOW after confirmation starts the run-loss
    /// window: the destination limit, which also removes the run input, normally stops motion first, and IN4 returning
    /// HIGH cancels it. <see cref="CheckDeadlines_NoLock"/> stops motion when the window passes.
    /// </summary>
    private void TrackDriveRunning_NoLock(DateTimeOffset now, RoofMotionDirection direction)
    {
        if (_rawIn4 == true)
        {
            if (!_atSpeedConfirmed)
            {
                _atSpeedConfirmed = true;
                if (_driveStoppedAtStart && _motionStartUtc is { } startedAt)
                {
                    RoofControllerTelemetry.RecordDriveStartDelay(direction, now - startedAt);
                }

                _logger.LogDebug("Drive at-speed (IN4) confirmed while {Direction}", direction);
            }

            if (_runLostUtc is { } lostAt)
            {
                _runLostUtc = null;
                _logger.LogInformation("Drive run input (IN4) returned after {Elapsed} ms while {Direction}", (now - lostAt).TotalMilliseconds, direction);
            }
        }
        else if (_rawIn4 == false && _atSpeedConfirmed && _options.AtSpeedConfirmationTimeout is not null && _runLostUtc is null)
        {
            _runLostUtc = now;
            _logger.LogWarning("Drive run input (IN4) dropped while {Direction} without the destination limit; stopping unless it returns within {Window} ms",
                direction, RunLossConfirmationDelay.TotalMilliseconds);

            // The loop may be sleeping for a full verification interval; the window must be enforced when it ends, before
            // an external stop that is released (with the run input still held) restarts the drive.
            WakeSupervision_NoLock();
        }
    }

    /// <summary>
    /// Supervises the limit opposite to the direction of travel. When motion started on that limit, its release must
    /// be observed continuously for at least <see cref="RoofControllerOptionsV4.LimitSwitchDebounce"/> before it is
    /// verified; reassertion before verification is treated as switch chatter. Once verified (or when motion started
    /// away from that limit), any assertion stops motion.
    /// </summary>
    private bool DepartureLimitReasserted_NoLock(DateTimeOffset now)
    {
        var departureLimit = _commandedMotion == RoofMotionDirection.Opening ? ClosedLimitActive_NoLock : OpenLimitActive_NoLock;
        if (departureLimit is null)
        {
            return false;
        }

        if (!_departureReleaseVerified)
        {
            if (departureLimit == false)
            {
                if (_releaseObservedUtc is null)
                {
                    _releaseObservedUtc = now;

                    // Verify the release when the debounce ends rather than at the next verification interval, so a
                    // reassertion after that is a stop and not chatter.
                    WakeSupervision_NoLock();
                }

                if (now - _releaseObservedUtc.Value >= _options.LimitSwitchDebounce)
                {
                    _departureReleaseVerified = true;
                    if (_motionStartUtc is { } startedAt)
                    {
                        RoofControllerTelemetry.RecordDepartureRelease(_commandedMotion, _releaseObservedUtc.Value - startedAt);
                    }

                    _logger.LogDebug("Departure limit release verified after {Elapsed} ms", (now - _releaseObservedUtc.Value).TotalMilliseconds);
                }
            }
            else if (_releaseObservedUtc is not null)
            {
                _logger.LogDebug("Departure limit reasserted before its release was verified (chatter); waiting for a stable release");
                _releaseObservedUtc = null;
            }

            return false;
        }

        return departureLimit == true;
    }

    private static bool IsLatchingReason(RoofControllerStopReason reason) => reason is
        RoofControllerStopReason.SafetyWatchdogTimeout or
        RoofControllerStopReason.DriveFault or
        RoofControllerStopReason.RelayVerificationFailed or
        RoofControllerStopReason.InputReadFailure or
        RoofControllerStopReason.ContradictoryLimitInputs or
        RoofControllerStopReason.StartLimitReasserted or
        RoofControllerStopReason.DriveNotRunning or
        RoofControllerStopReason.DepartureLimitNotReleased;

    /// <summary>
    /// The drive fault input (IN3) is active while no motion is commanded: latches
    /// <see cref="RoofControllerStopReason.DriveFault"/> and reports it as the last stop, as a trip during motion does
    /// (Refs #257). The last motion stop's reason, which the repeat and lease decisions read, is kept as it was.
    /// </summary>
    private void LatchIdleDriveFault_NoLock(string message)
    {
        if (_lastStopReason != RoofControllerStopReason.DriveFault)
        {
            _logger.LogInformation("Drive fault input (IN3) active while idle; the last stop is now {Reason} (was {Previous})",
                RoofControllerStopReason.DriveFault, _lastStopReason);
            _lastStopReason = RoofControllerStopReason.DriveFault;
        }

        Latch_NoLock(RoofControllerStopReason.DriveFault, message);
    }

    /// <summary>
    /// Latches a safety fault. The first latched reason is kept, except that a relay verification failure always takes
    /// precedence because relay state is then unknown.
    /// </summary>
    private void Latch_NoLock(RoofControllerStopReason reason, string message)
    {
        CancelPendingStart_NoLock($"A safety fault latched ({reason}) while this start waited; nothing moved for it.");
        if (!_faultLatched)
        {
            _faultLatched = true;
            _latchedFaultReason = reason;
            _lastError = message;
            _logger.LogError("Safety fault latched: {Reason} - {Message}", reason, message);
        }
        else if (reason == RoofControllerStopReason.RelayVerificationFailed && _latchedFaultReason != reason)
        {
            _logger.LogError("Safety fault latch escalated from {Previous} to {Reason} - {Message}", _latchedFaultReason, reason, message);
            _latchedFaultReason = reason;
            _lastError = message;
        }
    }

    #endregion

    #region Stop sequences

    /// <summary>
    /// Stops commanded motion: cancels all motion supervision, drives every relay off and verifies the register.
    /// Returns true only when the all-off register state was read back. An unverified stop latches
    /// <see cref="RoofControllerStopReason.RelayVerificationFailed"/> while <see cref="LastStopReason"/> keeps the cause.
    /// </summary>
    private bool StopMotion_NoLock(RoofControllerStopReason reason, string? error)
    {
        // Inside a bounded section the stop, and the rest of the section, has its one bound from here.
        BoundTheStop();
        Interlocked.Increment(ref _stopSequenceCount);
        var now = Now;
        var direction = _commandedMotion;
        var wasMoving = direction != RoofMotionDirection.None;
        var startedAt = _motionStartUtc;
        var startedAtLimit = _startedAtLimit;
        var startConfirmed = _atSpeedConfirmed && _driveStoppedAtStart;

        CancelMotionSupervision_NoLock();
        _commandedMotion = RoofMotionDirection.None;
        _motionOwner = null;

        if (wasMoving)
        {
            _lastMotionDirection = direction;
            _lastStopReason = reason;
            _lastMotionStopReason = reason;
            _lastMotionStopUtc = now;
            _driveRunningAfterStopReported = false;
        }

        var verified = AllRelaysOff_NoLock();
        if (wasMoving)
        {
            // The direction-change delay runs from here: the drive command ended (whether or not the read-back verified it).
            _lastDriveOffUtc = Now;
        }

        if (IsLatchingReason(reason))
        {
            Latch_NoLock(reason, error ?? $"Safety stop: {reason}.");
        }
        else if (error is not null)
        {
            _lastError = error;
        }

        if (!verified)
        {
            Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed,
                $"Stop ({reason}) could not verify the relay register all-off state; relay state is unknown.");
            _logger.LogCritical("Stop sequence for {Reason} could not verify all relays off (register mask {Mask}). Relay state is UNVERIFIED; use the independent hardware stop.",
                reason, FormatMask(_relayRegisterMask));
        }

        if (wasMoving)
        {
            RecordStopTiming_NoLock(now, direction, reason, startedAt, startedAtLimit, startConfirmed);
            RoofControllerTelemetry.RecordSafetyStop(reason, GetSafetyStopSource(reason, direction));
            if (IsLatchingReason(reason) || reason == RoofControllerStopReason.OperatorLeaseExpired)
            {
                _logger.LogWarning("Roof motion ({Direction}) stopped by safety supervision: {Reason}. {Error}", direction, reason, error);
            }
            else
            {
                _logger.LogInformation("Roof motion ({Direction}) stopped: {Reason}", direction, reason);
            }
        }

        return verified;
    }

    /// <summary>
    /// Records a move's travel time, and its drive stop delay: at once when the drive run input (IN4) already reports
    /// stopped (zero, after a drive start it saw), or when IN4 drops (<see cref="RecordDriveStopped_NoLock"/>).
    /// </summary>
    private void RecordStopTiming_NoLock(DateTimeOffset now, RoofMotionDirection direction, RoofControllerStopReason reason,
        DateTimeOffset? startedAt, bool startedAtLimit, bool startConfirmed)
    {
        if (startedAt is { } start)
        {
            RoofControllerTelemetry.RecordTravel(direction, reason, startedAtLimit, now - start);
        }

        if (_rawIn4 == true)
        {
            _driveStopDelayPending = (now, direction, reason);
        }
        else if (startConfirmed)
        {
            RoofControllerTelemetry.RecordDriveStopDelay(direction, reason, TimeSpan.Zero);
        }
    }

    /// <summary>Records the drive stop delay of the last stop once the drive run input (IN4) reports stopped.</summary>
    private void RecordDriveStopped_NoLock(DateTimeOffset now)
    {
        if (_driveStopDelayPending is { } pending && _rawIn4 == false)
        {
            _driveStopDelayPending = null;
            RoofControllerTelemetry.RecordDriveStopDelay(pending.Direction, pending.Reason,
                now > pending.At ? now - pending.At : TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Stop requested while no motion is commanded: re-asserts and verifies the all-off relay state without changing
    /// <see cref="LastStopReason"/>.
    /// </summary>
    private bool StopIdle_NoLock(RoofControllerStopReason reason)
    {
        Interlocked.Increment(ref _stopSequenceCount);
        var verified = AllRelaysOff_NoLock();
        if (!verified)
        {
            Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed,
                $"Stop ({reason}) could not verify the relay register all-off state; relay state is unknown.");
            _logger.LogCritical("Idle stop ({Reason}) could not verify all relays off (register mask {Mask}). Relay state is UNVERIFIED.",
                reason, FormatMask(_relayRegisterMask));
        }

        return verified;
    }

    private void CancelMotionSupervision_NoLock()
    {
        _motionGeneration++;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        _watchdogDeadlineUtc = null;
        _motionStartUtc = null;
        _leaseDeadlineUtc = null;
        _atSpeedDeadlineUtc = null;
        _atSpeedConfirmed = false;
        _runLostUtc = null;
        _driveStoppedAtStart = false;
        _startedAtLimit = false;
        _departureReleaseVerified = false;
        _releaseObservedUtc = null;
        _departureDeadlineUtc = null;
    }

    private static RoofSafetyStopSource GetSafetyStopSource(RoofControllerStopReason reason, RoofMotionDirection direction) => reason switch
    {
        RoofControllerStopReason.LimitSwitchReached => direction == RoofMotionDirection.Opening
            ? RoofSafetyStopSource.OpenLimitSwitch
            : RoofSafetyStopSource.ClosedLimitSwitch,
        RoofControllerStopReason.SafetyWatchdogTimeout => RoofSafetyStopSource.Watchdog,
        RoofControllerStopReason.DriveFault or RoofControllerStopReason.EmergencyStop => RoofSafetyStopSource.Fault,
        RoofControllerStopReason.RelayVerificationFailed => RoofSafetyStopSource.Relay,
        RoofControllerStopReason.InputReadFailure => RoofSafetyStopSource.Inputs,
        RoofControllerStopReason.OperatorLeaseExpired => RoofSafetyStopSource.Lease,
        RoofControllerStopReason.DriveNotRunning => RoofSafetyStopSource.Drive,
        RoofControllerStopReason.ContradictoryLimitInputs
            or RoofControllerStopReason.StartLimitReasserted
            or RoofControllerStopReason.DepartureLimitNotReleased => RoofSafetyStopSource.Limits,
        _ => RoofSafetyStopSource.Operator
    };

    #endregion

    #region Watchdog

    private void ArmWatchdog_NoLock(DateTimeOffset now)
    {
        var generation = _motionGeneration;
        var timeout = _options.SafetyWatchdogTimeout;
        _watchdogDeadlineUtc = now + timeout;
        _watchdogTimer?.Dispose();
        _watchdogTimer = _timeProvider.CreateTimer(_ => OnSafetyWatchdogElapsed(generation), null, timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Watchdog timer callback. The watchdog is an absolute cap measured from motion start; it is never extended by a
    /// repeated command. Stale callbacks (from an earlier motion) and callbacks after disposal are ignored.
    /// </summary>
    protected void OnSafetyWatchdogElapsed(long generation)
    {
        // One bound for the callback's wait for the lock and every HAT access its stop makes, as a Stop has.
        var deadline = StopDeadlineFromNow();
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_syncLock, StopLockWait, ref lockTaken);
            if (!lockTaken)
            {
                // A blocked HAT access holds the lock: the watchdog stop is queued to run the moment it is free.
                _logger.LogCritical("Safety watchdog TRIGGERED while the controller lock is held by a blocked HAT access; the stop is queued");
                QueueDeferredStop(RoofControllerStopReason.SafetyWatchdogTimeout,
                    $"Safety watchdog stopped motion after {_options.SafetyWatchdogTimeout.TotalSeconds:0.#} s without reaching the destination limit.",
                    onlyWhileMoving: true, motionGeneration: generation);
                return;
            }

            if (_disposed)
            {
                return;
            }

            if (generation != _motionGeneration || _commandedMotion == RoofMotionDirection.None)
            {
                _logger.LogDebug("Ignoring stale safety watchdog callback (generation {Generation}, current {Current})", generation, _motionGeneration);
                return;
            }

            _logger.LogWarning("Safety watchdog TRIGGERED after {Timeout}s of {Direction}; stopping", _options.SafetyWatchdogTimeout.TotalSeconds, _commandedMotion);
            using var section = BoundedSection(deadline);
            StopMotion_NoLock(RoofControllerStopReason.SafetyWatchdogTimeout,
                $"Safety watchdog stopped motion after {_options.SafetyWatchdogTimeout.TotalSeconds:0.#} s without reaching the destination limit.");
            FinishMutation_NoLock();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_syncLock);
            }
        }
    }

    #endregion

    #region Relay transactions

    private static int RelayBit(int relayId) => 1 << (relayId - 1);

    /// <summary>
    /// One HAT access on the HAT's lane, bounded by <see cref="HatIoLimit"/> (#220, §17 AA5): past it, it fails with
    /// <see cref="HatIoOverrunException"/> as a failed transfer does, and every later access fails at once until it has
    /// returned (<see cref="OnHatIoRecovered"/>).
    /// </summary>
    private T HatCall<T>(Func<T> access, bool protectiveRelayOff = false)
    {
        BoundPendingFence();
        var limit = HatIoLimit;
        var bounded = false;
        var accessDeadline = t_ioDeadline;
        if ((!protectiveRelayOff || accessDeadline is null) && t_foreignFenceDeadline is { } foreign)
        {
            accessDeadline = accessDeadline is { } own ? Math.Min(own, foreign) : foreign;
        }

        if (accessDeadline is { } deadline)
        {
            // A stop's one deadline spans its wait for the lock and all of its HAT I/O (#220, PR #230 rereviews S-F4, F19).
            var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (left <= TimeSpan.Zero)
            {
                t_boundCut = true;
                throw new HatIoOverrunException("The 2 s bound of the stop in progress has passed; this access was not sent. The relays-off work goes on after it.");
            }

            if (left < limit)
            {
                limit = left;
                bounded = true;
            }
        }

        try
        {
            return _hatIo.Run(access, limit);
        }
        catch (HatIoOverrunException) when (bounded)
        {
            // Cut by the stop's bound, not by the access's own limit.
            t_boundCut = true;
            throw;
        }
    }

    // The deadline (a Stopwatch timestamp) bounding the HAT I/O of the stop running on this thread, or null.
    [ThreadStatic]
    private static long? t_ioDeadline;

    // True inside a bounded section (BoundedSection) on this thread, with the deadline a stop decided in it takes (null: 2 s
    // from the decision). Outside one no stop is bounded: a queued stop's, shutdown's and disposal's relays-off work goes
    // on at each access's own limit.
    [ThreadStatic]
    private static bool t_inBoundedSection;

    [ThreadStatic]
    private static long? t_sectionDeadline;

    // True once the bound of the section on this thread has cut a HAT access short (or kept one from being sent).
    [ThreadStatic]
    private static bool t_boundCut;

    [ThreadStatic]
    private static RoofControllerServiceV4? t_sectionController;

    [ThreadStatic]
    private static PendingFenceRequest? t_sectionOwnFence;

    [ThreadStatic]
    private static bool t_watchForeignFences;

    [ThreadStatic]
    private static bool t_fencePending;

    // Routine work yields to this deadline. A stop decided by this caller uses t_ioDeadline for its all-off writes
    // and read-back: another request must not refuse the very work it is waiting for (#231, Opus R2 F7).
    [ThreadStatic]
    private static long? t_foreignFenceDeadline;

    private PendingFenceRequest? _pendingFences;

    // Pending requests outlive the gap before a locked section opens (#231, Sol R1 F1). A section observes every
    // foreign request still waiting to be applied; no ephemeral watcher publication or notification can lose one.
    private bool BoundPendingFence()
    {
        if (t_sectionController != this || !t_watchForeignFences)
        {
            return false;
        }

        var deadline = long.MaxValue;
        for (var request = Volatile.Read(ref _pendingFences); request is not null; request = request.Next)
        {
            if (!request.IsCompleted && request != t_sectionOwnFence)
            {
                deadline = Math.Min(deadline, request.Deadline);
            }
        }

        if (deadline != long.MaxValue)
        {
            t_fencePending = true;
            t_foreignFenceDeadline = t_foreignFenceDeadline is { } earlier && earlier < deadline ? earlier : deadline;
        }

        return t_fencePending;
    }

    // Recorded after the relay fence rises and before the caller waits. The list is lock-free: Stop never waits for a
    // registration lock. A queued caller transfers its request to its worker, so later cycles still yield to it.
    private PendingFenceRequest TrackPendingFence(long deadline)
    {
        var request = new PendingFenceRequest(this, deadline);
        PendingFenceRequest? current;
        do
        {
            current = Volatile.Read(ref _pendingFences);
            request.Next = current;
        }
        while (Interlocked.CompareExchange(ref _pendingFences, request, current) != current);

        return request;
    }

    private void PrunePendingFences()
    {
        while (Volatile.Read(ref _pendingFences) is { IsCompleted: true } completed)
        {
            Interlocked.CompareExchange(ref _pendingFences, completed.Next, completed);
        }
    }

    private sealed class PendingFenceRequest(RoofControllerServiceV4 controller, long deadline) : IDisposable
    {
        private int _completed;
        public long Deadline { get; } = deadline;
        public PendingFenceRequest? Next { get; set; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public void Dispose()
        {
            Volatile.Write(ref _completed, 1);
            controller.PrunePendingFences();
        }
    }

    /// <summary>
    /// The deadline of a Stop or a protective stop that starts now: <see cref="StopLockWait"/> from now, for its wait for
    /// the lock and every HAT access it makes, its LEDs included (#220, PR #230 rereviews S-F4, F19).
    /// </summary>
    private static long StopDeadlineFromNow() => Stopwatch.GetTimestamp() + (long)(StopLockWait.TotalSeconds * Stopwatch.Frequency);

    /// <summary>True once <paramref name="deadline"/> (a Stopwatch timestamp) has passed.</summary>
    private static bool HasPassed(long deadline) => Stopwatch.GetTimestamp() >= deadline;

    /// <summary>
    /// True once the bound of the section on this thread has cut a HAT access short or kept one from being sent, or
    /// <paramref name="deadline"/> has passed. (A cut access can end a moment before the deadline itself.)
    /// </summary>
    private static bool BoundPassed(long deadline) => t_boundCut || HasPassed(deadline);

    /// <summary>
    /// A locked section (a command, an input edge, the watchdog's timer, a supervision cycle) in which a stop is bounded
    /// (#220, PR #230 rereviews S-F4, F19, F24): once a stop is decided in it (<see cref="BoundTheStop"/>), every HAT access
    /// until the section ends, the caller's LEDs included, has one deadline. That is <paramref name="deadline"/> when the
    /// section has one from its start (an edge or the watchdog's timer, whose bound counts their wait for the lock),
    /// otherwise 2 s from the decision; a Stop's bound runs from its start (<paramref name="armNow"/>). Before a stop is
    /// decided, routine accesses keep their own limit only, until another caller raises a protective fence: then the
    /// routine work shares that fence's arrival deadline, and cosmetic accesses yield to it (#231, F27). A stop this
    /// caller decides retains its own deadline for all-off and verification, even when a foreign request is overdue.
    /// </summary>
    private HatIoSection BoundedSection(long? deadline = null, bool armNow = false, PendingFenceRequest? ownFence = null)
    {
        var section = new HatIoSection(t_ioDeadline, t_inBoundedSection, t_sectionDeadline, t_boundCut,
            t_sectionController, t_sectionOwnFence, t_watchForeignFences, t_fencePending, t_foreignFenceDeadline);
        t_inBoundedSection = true;
        t_sectionDeadline = deadline;
        t_boundCut = false;
        t_sectionController = this;
        // Stop opens its section before waiting for the lock and already has its own arrival deadline. Only a
        // section holding the lock can delay foreign protective work; local all-off retries are never requests.
        t_watchForeignFences = Monitor.IsEntered(_syncLock);
        t_sectionOwnFence = ownFence;
        t_fencePending = false;
        t_foreignFenceDeadline = null;
        if (armNow)
        {
            BoundTheStop();
        }

        return section;
    }

    /// <summary>A stop has been decided: bounds the rest of the section's HAT I/O (see <see cref="BoundedSection"/>).</summary>
    private static void BoundTheStop()
    {
        if (!t_inBoundedSection)
        {
            return;
        }

        var deadline = t_sectionDeadline ?? StopDeadlineFromNow();
        t_ioDeadline = t_ioDeadline is { } earlier && earlier < deadline ? earlier : deadline;
    }

    /// <summary>Restores the HAT I/O bound of the section around <see cref="BoundedSection"/>.</summary>
    private readonly struct HatIoSection : IDisposable
    {
        private readonly long? _ioDeadline;
        private readonly bool _inBoundedSection;
        private readonly long? _sectionDeadline;
        private readonly bool _boundCut;
        private readonly RoofControllerServiceV4? _controller;
        private readonly PendingFenceRequest? _ownFence;
        private readonly bool _watchForeignFences;
        private readonly bool _fencePending;
        private readonly long? _foreignFenceDeadline;

        public HatIoSection(long? ioDeadline, bool inBoundedSection, long? sectionDeadline, bool boundCut,
            RoofControllerServiceV4? controller, PendingFenceRequest? ownFence, bool watchForeignFences, bool fencePending,
            long? foreignFenceDeadline)
        {
            _ioDeadline = ioDeadline;
            _inBoundedSection = inBoundedSection;
            _sectionDeadline = sectionDeadline;
            _boundCut = boundCut;
            _controller = controller;
            _ownFence = ownFence;
            _watchForeignFences = watchForeignFences;
            _fencePending = fencePending;
            _foreignFenceDeadline = foreignFenceDeadline;
        }

        public void Dispose()
        {
            t_ioDeadline = _ioDeadline;
            t_inBoundedSection = _inBoundedSection;
            t_sectionDeadline = _sectionDeadline;
            t_boundCut = _boundCut;
            t_sectionController = _controller;
            t_sectionOwnFence = _ownFence;
            t_watchForeignFences = _watchForeignFences;
            t_fencePending = _fencePending;
            t_foreignFenceDeadline = _foreignFenceDeadline;
        }
    }

    /// <summary>
    /// A HAT access that ran past its limit has returned (on the lane's thread). It may have switched a relay late, so
    /// every relay is written off and verified as soon as the lock is free; the latch its overrun set stays until a
    /// person clears it.
    /// </summary>
    private void OnHatIoRecovered()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogCritical("A HAT access that exceeded its allowed wait has returned; every relay is written off and verified now");
        QueueDeferredStop(RoofControllerStopReason.RelayVerificationFailed, null, onlyWhileMoving: false);
    }

    private static string FormatMask(int? mask) => mask is { } value ? $"0x{value:X2}" : "unreadable";

    /// <summary>
    /// Writes an ON for a start admitted at <paramref name="fenceGeneration"/>: dropped (<see cref="RelayWriteOutcome.Fenced"/>)
    /// when a relays-off request came after that admission. Called inside the HAT transaction lock, immediately before the
    /// bus write, so it is the last point software decides. A null generation is unfenced (tests).
    /// </summary>
    private RelayWriteOutcome TryWriteRelayOn_NoLock(int relayId, long? fenceGeneration)
    {
        if (fenceGeneration is { } generation && !OnAllowed(generation))
        {
            _logger.LogInformation("Relay {Relay} ON dropped: a relays-off request came after the start's admission (generation {Generation})", relayId, generation);
            return RelayWriteOutcome.Fenced;
        }

        return TryWriteRelay_NoLock(relayId, true) ? RelayWriteOutcome.Verified : RelayWriteOutcome.Failed;
    }

    private bool TryWriteRelay_NoLock(int relayId, bool on, bool protectiveStop = false)
    {
        try
        {
            var result = HatCall(() => _hat.SetRelay(relayId, on), protectiveRelayOff: protectiveStop && !on);
            if (result.IsSuccessful)
            {
                return true;
            }

            _logger.LogWarning(result.Error, "Relay {Relay} write ({State}) failed", relayId, on ? "on" : "off");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay {Relay} write ({State}) threw", relayId, on ? "on" : "off");
        }

        return false;
    }

    /// <summary>Reads the relay register (bits 0-3). Updates relay-read freshness tracking; never throws.</summary>
    private int? TryReadRelayMask_NoLock(bool protectiveStop = false)
    {
        Exception? error = null;
        try
        {
            var result = HatCall(_hat.GetRelaysMask, protectiveRelayOff: protectiveStop);
            if (result.IsSuccessful)
            {
                _lastSuccessfulRelayReadUtc = Now;
                if (_consecutiveRelayReadFailures > 0)
                {
                    _logger.LogInformation("Relay register reads recovered after {Failures} consecutive failures", _consecutiveRelayReadFailures);
                }

                _consecutiveRelayReadFailures = 0;
                return result.Value & 0x0F;
            }

            error = result.Error;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _consecutiveRelayReadFailures++;
        _logger.LogWarning(error, "Relay register read-back failed ({Failures} consecutive)", _consecutiveRelayReadFailures);
        return null;
    }

    private void RecordRelayState_NoLock(bool verified, int? mask)
    {
        _relayRegisterState = verified ? RoofRelayRegisterState.Verified : RoofRelayRegisterState.Unverified;
        _relayRegisterMask = mask;
    }

    /// <summary>
    /// Drives every relay off (direction relays first, then STOP permit, then clear-fault) and verifies the register
    /// reads back zero. Falls back to a whole-register write when the per-relay writes did not verify.
    /// </summary>
    private bool AllRelaysOff_NoLock()
    {
        // Every relays-off path moves the command generation and raises the relay fence first (#220).
        FenceRelaysOff();
        CancelPendingStart_NoLock("The relays were driven off while this start waited; nothing moved for it.");
        lock (_hatTransactionLock)
        {
            var protectiveStop = t_ioDeadline is not null;
            TryWriteRelay_NoLock(_options.OpenRelayId, false, protectiveStop);
            TryWriteRelay_NoLock(_options.CloseRelayId, false, protectiveStop);
            TryWriteRelay_NoLock(_options.StopRelayId, false, protectiveStop);
            TryWriteRelay_NoLock(_options.ClearFaultRelayId, false, protectiveStop);

            var mask = TryReadRelayMask_NoLock(protectiveStop);
            if (mask != 0)
            {
                _logger.LogWarning("All-off read-back was {Mask}; writing the whole relay register to zero", FormatMask(mask));
                try
                {
                    var result = HatCall(() => _hat.SetRelaysMask(0), protectiveRelayOff: protectiveStop);
                    if (!result.IsSuccessful)
                    {
                        _logger.LogWarning(result.Error, "Relay register zero write failed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Relay register zero write threw");
                }

                mask = TryReadRelayMask_NoLock(protectiveStop);
            }

            var verified = mask == 0;
            RecordRelayState_NoLock(verified, mask);
            if (!verified && (t_ioDeadline ?? t_foreignFenceDeadline) is { } deadline && BoundPassed(deadline))
            {
                // The stop's bound cut its relays-off work short: it goes on as soon as the lock is free, each access at its
                // own limit, and the relays stay unverified until a read-back shows them off.
                QueueDeferredStop(RoofControllerStopReason.RelayVerificationFailed, null, onlyWhileMoving: false, deadline: deadline);
            }

            return verified;
        }
    }

    /// <summary>
    /// The single entry point for relay combinations, unfenced (tests force relay states through it). Returns true only
    /// when each step's register read-back matched.
    /// </summary>
    protected bool SetRelayStatesAtomically(bool stopRelay, bool openRelay, bool closeRelay)
        => SetRelayStatesAtomically(stopRelay, openRelay, closeRelay, fenceGeneration: null) == RelayWriteOutcome.Verified;

    /// <summary>
    /// The single entry point for relay combinations. Valid requests are all-off, STOP permit only, STOP + open and
    /// STOP + close. Requests with both directions, or a direction without the STOP permit, are refused: all relays are
    /// driven off and <see cref="RelayWriteOutcome.Failed"/> is returned. Every ON is fenced at
    /// <paramref name="fenceGeneration"/>: a relays-off request after the start's admission drops it, drives every relay
    /// off and returns <see cref="RelayWriteOutcome.Fenced"/>. <see cref="RelayWriteOutcome.Verified"/> only when each
    /// step's register read-back matched.
    /// </summary>
    private RelayWriteOutcome SetRelayStatesAtomically(bool stopRelay, bool openRelay, bool closeRelay, long? fenceGeneration)
    {
        lock (_syncLock)
        {
            if (openRelay && closeRelay)
            {
                _logger.LogCritical("Refused relay request with both Open and Close asserted; driving all relays off");
                AllRelaysOff_NoLock();
                return RelayWriteOutcome.Failed;
            }

            if ((openRelay || closeRelay) && !stopRelay)
            {
                _logger.LogCritical("Refused relay request with a direction but no STOP permit; driving all relays off");
                AllRelaysOff_NoLock();
                return RelayWriteOutcome.Failed;
            }

            if (!stopRelay)
            {
                return AllRelaysOff_NoLock() ? RelayWriteOutcome.Verified : RelayWriteOutcome.Failed;
            }

            var stopBit = RelayBit(_options.StopRelayId);
            lock (_hatTransactionLock)
            {
                // 1. Clear the direction(s) not requested and verify that nothing outside the requested set
                //    (in particular the opposite direction and the clear-fault relay) is asserted.
                var requested = stopBit
                                | (openRelay ? RelayBit(_options.OpenRelayId) : 0)
                                | (closeRelay ? RelayBit(_options.CloseRelayId) : 0);
                if (!openRelay) TryWriteRelay_NoLock(_options.OpenRelayId, false);
                if (!closeRelay) TryWriteRelay_NoLock(_options.CloseRelayId, false);
                var mask = TryReadRelayMask_NoLock();
                if (mask is null || (mask.Value & ~requested) != 0)
                {
                    return AbortEnergize_NoLock("opposite direction not verified off", mask);
                }

                // 2. Assert the STOP permit.
                var permit = TryWriteRelayOn_NoLock(_options.StopRelayId, fenceGeneration);
                if (permit != RelayWriteOutcome.Verified)
                {
                    return permit == RelayWriteOutcome.Fenced
                        ? FencedEnergize_NoLock()
                        : AbortEnergize_NoLock("STOP permit write failed", mask);
                }

                mask = TryReadRelayMask_NoLock();
                if (mask is null || (mask.Value & stopBit) == 0 || (mask.Value & ~requested) != 0)
                {
                    return AbortEnergize_NoLock("STOP permit not verified", mask);
                }

                if (!openRelay && !closeRelay)
                {
                    RecordRelayState_NoLock(true, mask);
                    return RelayWriteOutcome.Verified;
                }

                // 3. Assert the direction.
                var directionRelay = openRelay ? _options.OpenRelayId : _options.CloseRelayId;
                var expected = requested;
                var written = TryWriteRelayOn_NoLock(directionRelay, fenceGeneration);
                if (written != RelayWriteOutcome.Verified)
                {
                    return written == RelayWriteOutcome.Fenced
                        ? FencedEnergize_NoLock()
                        : AbortEnergize_NoLock("direction write failed", mask);
                }

                mask = TryReadRelayMask_NoLock();
                if (mask != expected)
                {
                    return AbortEnergize_NoLock($"direction not verified (expected 0x{expected:X2})", mask);
                }

                RecordRelayState_NoLock(true, mask);
                return RelayWriteOutcome.Verified;
            }
        }
    }

    /// <summary>A start's ON was fenced by a later relays-off request: every relay off, nothing latched.</summary>
    private RelayWriteOutcome FencedEnergize_NoLock()
    {
        AllRelaysOff_NoLock();
        return RelayWriteOutcome.Fenced;
    }

    private RelayWriteOutcome AbortEnergize_NoLock(string reason, int? mask)
    {
        _logger.LogCritical("Relay energize sequence aborted: {Reason}; register {Mask}. Driving all relays off", reason, FormatMask(mask));
        // The energize request failed whatever the all-off outcome; the relay state reflects the all-off read-back.
        AllRelaysOff_NoLock();

        // A direction or the permit may have been on for a moment: the direction-change delay runs from here.
        _lastDriveOffUtc = Now;
        return RelayWriteOutcome.Failed;
    }

    /// <summary>Asserts only the clear-fault relay and verifies the register shows exactly that bit.</summary>
    private bool AssertClearFaultRelay_NoLock()
    {
        lock (_hatTransactionLock)
        {
            var bit = RelayBit(_options.ClearFaultRelayId);
            if (!TryWriteRelay_NoLock(_options.ClearFaultRelayId, true))
            {
                RecordRelayState_NoLock(false, TryReadRelayMask_NoLock());
                return false;
            }

            var mask = TryReadRelayMask_NoLock();
            var verified = mask == bit;
            RecordRelayState_NoLock(verified, mask);
            return verified;
        }
    }

    /// <summary>Releases the clear-fault relay and verifies the register reads zero, falling back to all-off.</summary>
    private bool ReleaseClearFaultRelay_NoLock()
    {
        lock (_hatTransactionLock)
        {
            TryWriteRelay_NoLock(_options.ClearFaultRelayId, false);
            var mask = TryReadRelayMask_NoLock();
            if (mask == 0)
            {
                RecordRelayState_NoLock(true, mask);
                return true;
            }
        }

        _logger.LogWarning("Clear-fault relay release did not verify; driving all relays off");
        return AllRelaysOff_NoLock();
    }

    #endregion

    #region Input reads

    /// <summary>Direct read of all four inputs. Updates freshness tracking; never throws.</summary>
    private bool ReadInputs_NoLock()
    {
        Exception? error = null;
        try
        {
            var result = HatCall(_hat.GetAllDigitalInputs);
            if (result.IsSuccessful)
            {
                var (in1, in2, in3, in4) = result.Value;
                _rawIn1 = in1;
                _rawIn2 = in2;
                _rawIn3 = in3;
                _rawIn4 = in4;
                _lastSuccessfulInputReadUtc = Now;
                if (_consecutiveInputReadFailures > 0)
                {
                    _logger.LogInformation("Safety input reads recovered after {Failures} consecutive failures", _consecutiveInputReadFailures);
                }

                _consecutiveInputReadFailures = 0;
                return true;
            }

            error = result.Error;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _consecutiveInputReadFailures++;

        // The HAT may have reset (its LED modes return to following the inputs); re-apply them with the next LED update.
        _lastIndicatorLedMask = null;
        _lastAttemptedIndicatorLedMask = null;
        _ledModesApplied = false;
        _lastError = $"Safety input read failed ({_consecutiveInputReadFailures} consecutive): {error?.Message ?? "unknown error"}";
        _logger.LogWarning(error, "Safety input read failed ({Failures} consecutive)", _consecutiveInputReadFailures);
        return false;
    }

    #endregion

    #region Supervision

    /// <summary>
    /// One supervision pass: retries an unverified relay state, reads the inputs, applies the safety rules, compares the
    /// relay register with the commanded state and enforces the watchdog, lease and at-speed deadlines. While idle it
    /// also reads back the HAT LED modes. Runs on the background loop; tests call it directly.
    /// </summary>
    internal void RunSupervisionCycle() => RunSupervisionCycle(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// <see cref="RunSupervisionCycle()"/> waiting at most <paramref name="lockWait"/> for the lock. When a blocked HAT access
    /// holds it longer while the roof moves, a protective stop is queued to run the moment the lock is free: supervision
    /// cannot see the limits meanwhile.
    /// </summary>
    private void RunSupervisionCycle(TimeSpan lockWait)
    {
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_syncLock, lockWait, ref lockTaken);
            if (!lockTaken)
            {
                if (_lastPublishedSnapshot is { IsMoving: true })
                {
                    _logger.LogCritical("Supervision could not take the controller lock within {Wait} while the roof moves (a HAT access is blocked); a protective stop is queued", lockWait);
                    QueueDeferredStop(RoofControllerStopReason.InputReadFailure,
                        $"Supervision could not run for {lockWait.TotalSeconds:0.#} s while the roof moved: a HAT access was blocked.", onlyWhileMoving: true);
                }

                return;
            }

            if (_disposed || _shuttingDown || !_initialized)
            {
                return;
            }

            // A stop the cycle decides is bounded as a Stop is, from its decision to the cycle's end, its LEDs included; the
            // routine reads before it keep their own limit (PR #230 rereview F24). The wait for the lock is bounded above.
            using var section = BoundedSection();
            var now = Now;

            if (BoundPendingFence())
            {
                // The protective caller already owns the evidence. Do not begin another routine read when it is
                // waiting for this monitor; its pending request remains visible until that caller applies it.
                // Idle work yields too: refusing a routine read is not evidence of an input transfer failure.
                FinishMutation_NoLock();
                return;
            }

            if (_relayRegisterState == RoofRelayRegisterState.Unverified && !_clearFaultInProgress && _commandedMotion == RoofMotionDirection.None)
            {
                if (AllRelaysOff_NoLock())
                {
                    _logger.LogWarning("Relay register all-off re-verified by supervision; the RelayVerificationFailed latch remains until cleared");
                }
            }

            // A request can arrive during the routine all-off retry above. Its deadline may have cut that retry:
            // yield the following read too, rather than count an access we would refuse before sending as failed.
            if (!BoundPendingFence())
            {
                if (ReadInputs_NoLock())
                {
                    Evaluate_NoLock(now);
                }
                else if (_commandedMotion != RoofMotionDirection.None && _consecutiveInputReadFailures >= _options.MaxConsecutiveInputReadFailures)
                {
                    StopMotion_NoLock(RoofControllerStopReason.InputReadFailure,
                        $"Safety inputs could not be read {_consecutiveInputReadFailures} consecutive times while moving.");
                }
            }

            // An edge or Stop has raised its fence while the cycle held the lock. Its own all-off verification takes
            // priority over another routine read (and the LEDs below); let it take the lock promptly.
            if (!BoundPendingFence())
            {
                CheckRelayRegister_NoLock();
            }
            CheckDeadlines_NoLock(now);
            if (_commandedMotion == RoofMotionDirection.None)
            {
                CheckIndicatorLedModes_NoLock();
            }

            FinishMutation_NoLock();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_syncLock);
            }
        }
    }

    private void CheckRelayRegister_NoLock()
    {
        if (_clearFaultInProgress || _relayRegisterState == RoofRelayRegisterState.Unverified)
        {
            return;
        }

        int? mask;
        lock (_hatTransactionLock)
        {
            mask = TryReadRelayMask_NoLock();
        }

        if (mask is null)
        {
            HandleRelayReadFailure_NoLock();
            return;
        }

        var expected = _commandedMotion switch
        {
            RoofMotionDirection.Opening => RelayBit(_options.StopRelayId) | RelayBit(_options.OpenRelayId),
            RoofMotionDirection.Closing => RelayBit(_options.StopRelayId) | RelayBit(_options.CloseRelayId),
            _ => 0
        };

        if (mask == expected)
        {
            _relayRegisterMask = mask;
            return;
        }

        var message = $"Relay register {FormatMask(mask)} does not match the commanded state 0x{expected:X2}.";
        if (_commandedMotion != RoofMotionDirection.None)
        {
            StopMotion_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
        }
        else
        {
            _logger.LogCritical("{Message} Driving all relays off", message);
            BoundTheStop();
            AllRelaysOff_NoLock();
            Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
        }
    }

    /// <summary>
    /// A periodic relay register read failed. Below <see cref="MaxConsecutiveRelayReadFailures"/> the last verified
    /// state is kept and the failure reported. At the threshold, commanded motion stops and latches
    /// <see cref="RoofControllerStopReason.RelayVerificationFailed"/>; when idle, the all-off sequence runs again and
    /// latches only if it cannot verify within the stop bound. Status and readiness report unhealthy relay reads until
    /// a read succeeds.
    /// </summary>
    private void HandleRelayReadFailure_NoLock()
    {
        var failures = _consecutiveRelayReadFailures;
        if (failures < MaxConsecutiveRelayReadFailures)
        {
            _lastError = $"Relay register read failed ({failures} consecutive); the last verified state is retained.";
            _logger.LogWarning("Supervision could not read the relay register ({Failures} consecutive); the last verified state is retained", failures);
            return;
        }

        var message = $"The relay register could not be read {failures} consecutive times; the relay state cannot be supervised.";
        if (_commandedMotion != RoofMotionDirection.None)
        {
            StopMotion_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
            return;
        }

        _logger.LogCritical("{Message} Driving all relays off", message);
        BoundTheStop();
        if (AllRelaysOff_NoLock())
        {
            _logger.LogWarning("Relay register all-off verified after {Failures} failed supervision reads", failures);
            return;
        }

        Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
    }

    private void CheckDeadlines_NoLock(DateTimeOffset now)
    {
        if (_commandedMotion == RoofMotionDirection.None)
        {
            CheckDriveStoppedAfterStop_NoLock(now);
            return;
        }

        if (_watchdogDeadlineUtc is { } watchdogDeadline && now >= watchdogDeadline)
        {
            _logger.LogWarning("Safety watchdog TRIGGERED (supervision backstop) after {Timeout}s of {Direction}; stopping",
                _options.SafetyWatchdogTimeout.TotalSeconds, _commandedMotion);
            StopMotion_NoLock(RoofControllerStopReason.SafetyWatchdogTimeout,
                $"Safety watchdog stopped motion after {_options.SafetyWatchdogTimeout.TotalSeconds:0.#} s without reaching the destination limit.");
            return;
        }

        // The at-speed deadline latches and the lease does not, so it is checked first: when both have passed (a lease at
        // least as long as the at-speed window), the missed at-speed confirmation must not be recorded as a lease expiry.
        if (_atSpeedDeadlineUtc is { } atSpeedDeadline && !_atSpeedConfirmed && now >= atSpeedDeadline)
        {
            StopMotion_NoLock(RoofControllerStopReason.DriveNotRunning,
                $"The drive did not report at-speed (IN4) within {_options.AtSpeedConfirmationTimeout?.TotalSeconds:0.#} s of the motion command.");
            return;
        }

        if (InputDeadlinePassed_NoLock(now))
        {
            // The cached inputs may predate the deadline (an edge the poll loop has not delivered yet, such as the
            // destination limit). Re-read them first so a stop at the limit, or a recovered input, takes precedence.
            if (_lastSuccessfulInputReadUtc is not { } lastRead || lastRead < now)
            {
                if (ReadInputs_NoLock())
                {
                    Evaluate_NoLock(now);
                }

                if (_commandedMotion == RoofMotionDirection.None)
                {
                    return;
                }
            }

            if (_departureDeadlineUtc is { } departureDeadline && !_departureReleaseVerified && now >= departureDeadline)
            {
                var limitName = _commandedMotion == RoofMotionDirection.Opening ? "closed" : "open";
                StopMotion_NoLock(RoofControllerStopReason.DepartureLimitNotReleased,
                    $"The {limitName} limit did not release and stay released for {_options.LimitSwitchDebounce.TotalMilliseconds:0} ms within {_options.DepartureReleaseTimeout?.TotalSeconds:0.#} s of the motion command; the roof may be jammed or moving the wrong way.");
                return;
            }

            if (_runLostUtc is { } runLost && now - runLost >= RunLossConfirmationDelay)
            {
                StopMotion_NoLock(RoofControllerStopReason.DriveNotRunning,
                    $"The drive stopped reporting running (IN4) for {(now - runLost).TotalMilliseconds:0} ms while {_commandedMotion.ToString().ToLowerInvariant()} without the destination limit.");
                return;
            }
        }

        if (_leaseDeadlineUtc is { } leaseDeadline && now >= leaseDeadline)
        {
            StopMotion_NoLock(RoofControllerStopReason.OperatorLeaseExpired, "The operator lease expired before it was renewed.");
        }
    }

    /// <summary>True when the departure-release deadline or the IN4 run-loss window has passed.</summary>
    private bool InputDeadlinePassed_NoLock(DateTimeOffset now)
        => (_departureDeadlineUtc is { } departureDeadline && !_departureReleaseVerified && now >= departureDeadline)
           || (_runLostUtc is { } runLost && now - runLost >= RunLossConfirmationDelay);

    /// <summary>
    /// Reports (once per stop) a drive that still signals running (IN4) once the stop window
    /// (<see cref="RoofControllerOptionsV4.DriveStopConfirmationTimeout"/>, else
    /// <see cref="RoofControllerOptionsV4.AtSpeedConfirmationTimeout"/>) has elapsed since the stop. Diagnostic only: relays
    /// are already off, so only an independent hardware stop can act on it.
    /// </summary>
    private void CheckDriveStoppedAfterStop_NoLock(DateTimeOffset now)
    {
        if (DriveStopCheckDeadline_NoLock() is not { } deadline
            || now < deadline
            || _rawIn4 != true)
        {
            return;
        }

        var stoppedAt = _lastMotionStopUtc!.Value;

        _driveRunningAfterStopReported = true;
        _lastError = $"Drive still reports running (IN4) {(now - stoppedAt).TotalSeconds:0.#} s after the relays were released.";
        _logger.LogCritical("Drive still reports running (IN4) {Seconds:0.#}s after stop ({Reason}); relays read back off. Check the drive and use the independent hardware stop",
            (now - stoppedAt).TotalSeconds, _lastMotionStopReason);
    }

    /// <summary>When the drive-stop check is due, or null when it is off or already reported for the last stop.</summary>
    private DateTimeOffset? DriveStopCheckDeadline_NoLock() =>
        (_options.DriveStopConfirmationTimeout ?? _options.AtSpeedConfirmationTimeout) is { } window
        && _lastMotionStopUtc is { } stoppedAt
        && !_driveRunningAfterStopReported
            ? stoppedAt + window
            : null;

    /// <summary>The delay the supervision loop would wait now. Lets tests drive the loop's schedule on a manual clock.</summary>
    internal TimeSpan GetSupervisionDelay()
    {
        lock (_syncLock)
        {
            return ComputeSupervisionDelay_NoLock(Now);
        }
    }

    private TimeSpan ComputeSupervisionDelay_NoLock(DateTimeOffset now)
    {
        if (_commandedMotion == RoofMotionDirection.None)
        {
            // Wake at the drive-stop check rather than up to one idle interval after it. A check that is overdue while IN4
            // still reports running runs at once: the cycle at the deadline can take its time before its reads, and a
            // timer can wake a fraction of a millisecond early. A passed check with IN4 stopped never reports, so it
            // keeps the idle interval.
            if (DriveStopCheckDeadline_NoLock() is { } stopCheck && (stopCheck > now || _rawIn4 == true) && stopCheck - now < IdleSupervisionInterval)
            {
                return stopCheck - now < MinimumSupervisionDelay ? MinimumSupervisionDelay : stopCheck - now;
            }

            return IdleSupervisionInterval;
        }

        var delay = _options.EnablePeriodicVerificationWhileMoving ? _options.PeriodicVerificationInterval : MovingFallbackSupervisionInterval;

        void Consider(DateTimeOffset? deadline)
        {
            if (deadline is { } value && value - now < delay)
            {
                delay = value - now;
            }
        }

        Consider(_watchdogDeadlineUtc);
        Consider(_leaseDeadlineUtc);
        if (!_atSpeedConfirmed)
        {
            Consider(_atSpeedDeadlineUtc);
        }

        if (!_departureReleaseVerified)
        {
            if (_releaseObservedUtc is { } releaseObserved)
            {
                Consider(releaseObserved + _options.LimitSwitchDebounce);
            }

            Consider(_departureDeadlineUtc);
        }

        if (_runLostUtc is { } runLost)
        {
            Consider(runLost + RunLossConfirmationDelay);
        }

        return delay < MinimumSupervisionDelay ? MinimumSupervisionDelay : delay;
    }

    private void StartSupervision_NoLock()
    {
        if (!EnableBackgroundSupervision || _supervisionTask is not null)
        {
            return;
        }

        _supervisionCts = new CancellationTokenSource();
        var token = _supervisionCts.Token;
        _supervisionTask = Task.Run(() => SupervisionLoopAsync(token));
    }

    private void StopSupervision_NoLock()
    {
        // Continuations are forced to yield, so cancelling under the lock never runs loop code inline.
        _supervisionCts?.Cancel();
    }

    /// <summary>Wakes the supervision loop so it recomputes its delay (for example after motion starts).</summary>
    private void WakeSupervision_NoLock()
    {
        _supervisionWakeCount++;
        _supervisionWakeCts?.Cancel();
    }

    /// <summary>How many times the supervision loop has been woken early. Lets tests follow the loop's schedule.</summary>
    internal long SupervisionWakeCount
    {
        get
        {
            lock (_syncLock)
            {
                return _supervisionWakeCount;
            }
        }
    }

    private async Task SupervisionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay;
            CancellationTokenSource wake;

            // The loop never waits for the lock without a bound (#220, §17 AA5): when a blocked HAT access holds it, the
            // next cycle comes after the idle interval and its own bounded wait queues the protective stop.
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(_syncLock, StopLockWait, ref lockTaken);
                if (lockTaken && (_disposed || _shuttingDown))
                {
                    return;
                }

                delay = lockTaken ? ComputeSupervisionDelay_NoLock(Now) : TimeSpan.Zero;
                wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (lockTaken)
                {
                    var previous = _supervisionWakeCts;
                    _supervisionWakeCts = wake;
                    previous?.Dispose();
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_syncLock);
                }
            }

            await Task.Delay(delay, _timeProvider, wake.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            var wokenEarly = wake.IsCancellationRequested;
            if (!lockTaken)
            {
                // Never handed to the controller (the lock was not taken), so nothing else disposes it.
                wake.Dispose();
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (wokenEarly)
            {
                // Woken early (motion started); recompute the delay without an extra read.
                continue;
            }

            try
            {
                RunSupervisionCycle(StopLockWait);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supervision cycle failed");
            }
        }
    }

    #endregion
}

/// <summary>What a fenced relay sequence did.</summary>
internal enum RelayWriteOutcome
{
    /// <summary>Every step's read-back matched.</summary>
    Verified,

    /// <summary>A write failed or a read-back did not match; every relay was driven off.</summary>
    Failed,

    /// <summary>A relays-off request after the start's admission dropped its ON; every relay was driven off.</summary>
    Fenced
}
