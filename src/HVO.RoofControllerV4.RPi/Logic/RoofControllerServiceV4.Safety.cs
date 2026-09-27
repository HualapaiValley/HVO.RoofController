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
        lock (_syncLock)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

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
    }

    #endregion

    #region Safety evaluation

    /// <summary>
    /// Applies the safety rules to the cached inputs. While moving: contradictory limits, destination limit, departure
    /// limit reassertion and drive fault stop motion, and the drive run input (IN4) is tracked. While idle: an active drive
    /// fault or contradictory limits latch.
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
            if (DriveFaultActive_NoLock == true)
            {
                Latch_NoLock(RoofControllerStopReason.DriveFault, "Drive fault input (IN3) is active.");
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
    /// Latches a safety fault. The first latched reason is kept, except that a relay verification failure always takes
    /// precedence because relay state is then unknown.
    /// </summary>
    private void Latch_NoLock(RoofControllerStopReason reason, string message)
    {
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
        Interlocked.Increment(ref _stopSequenceCount);
        var now = Now;
        var direction = _commandedMotion;
        var wasMoving = direction != RoofMotionDirection.None;

        CancelMotionSupervision_NoLock();
        _commandedMotion = RoofMotionDirection.None;

        if (wasMoving)
        {
            _lastMotionDirection = direction;
            _lastStopReason = reason;
            _lastMotionStopUtc = now;
            _driveRunningAfterStopReported = false;
        }

        var verified = AllRelaysOff_NoLock();

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
        lock (_syncLock)
        {
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
            StopMotion_NoLock(RoofControllerStopReason.SafetyWatchdogTimeout,
                $"Safety watchdog stopped motion after {_options.SafetyWatchdogTimeout.TotalSeconds:0.#} s without reaching the destination limit.");
            FinishMutation_NoLock();
        }
    }

    #endregion

    #region Relay transactions

    private static int RelayBit(int relayId) => 1 << (relayId - 1);

    private static string FormatMask(int? mask) => mask is { } value ? $"0x{value:X2}" : "unreadable";

    private bool TryWriteRelay_NoLock(int relayId, bool on)
    {
        try
        {
            var result = _hat.SetRelay(relayId, on);
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
    private int? TryReadRelayMask_NoLock()
    {
        Exception? error = null;
        try
        {
            var result = _hat.GetRelaysMask();
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
        lock (_hatTransactionLock)
        {
            TryWriteRelay_NoLock(_options.OpenRelayId, false);
            TryWriteRelay_NoLock(_options.CloseRelayId, false);
            TryWriteRelay_NoLock(_options.StopRelayId, false);
            TryWriteRelay_NoLock(_options.ClearFaultRelayId, false);

            var mask = TryReadRelayMask_NoLock();
            if (mask != 0)
            {
                _logger.LogWarning("All-off read-back was {Mask}; writing the whole relay register to zero", FormatMask(mask));
                try
                {
                    var result = _hat.SetRelaysMask(0);
                    if (!result.IsSuccessful)
                    {
                        _logger.LogWarning(result.Error, "Relay register zero write failed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Relay register zero write threw");
                }

                mask = TryReadRelayMask_NoLock();
            }

            var verified = mask == 0;
            RecordRelayState_NoLock(verified, mask);
            return verified;
        }
    }

    /// <summary>
    /// The single entry point for relay combinations. Valid requests are all-off, STOP permit only, STOP + open and
    /// STOP + close. Requests with both directions, or a direction without the STOP permit, are refused: all relays are
    /// driven off and false is returned. Returns true only when each step's register read-back matched.
    /// </summary>
    protected bool SetRelayStatesAtomically(bool stopRelay, bool openRelay, bool closeRelay)
    {
        lock (_syncLock)
        {
            if (openRelay && closeRelay)
            {
                _logger.LogCritical("Refused relay request with both Open and Close asserted; driving all relays off");
                AllRelaysOff_NoLock();
                return false;
            }

            if ((openRelay || closeRelay) && !stopRelay)
            {
                _logger.LogCritical("Refused relay request with a direction but no STOP permit; driving all relays off");
                AllRelaysOff_NoLock();
                return false;
            }

            if (!stopRelay)
            {
                return AllRelaysOff_NoLock();
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
                if (!TryWriteRelay_NoLock(_options.StopRelayId, true))
                {
                    return AbortEnergize_NoLock("STOP permit write failed", mask);
                }

                mask = TryReadRelayMask_NoLock();
                if (mask is null || (mask.Value & stopBit) == 0 || (mask.Value & ~requested) != 0)
                {
                    return AbortEnergize_NoLock("STOP permit not verified", mask);
                }

                if (!openRelay && !closeRelay)
                {
                    RecordRelayState_NoLock(true, mask);
                    return true;
                }

                // 3. Assert the direction.
                var directionRelay = openRelay ? _options.OpenRelayId : _options.CloseRelayId;
                var expected = requested;
                if (!TryWriteRelay_NoLock(directionRelay, true))
                {
                    return AbortEnergize_NoLock("direction write failed", mask);
                }

                mask = TryReadRelayMask_NoLock();
                if (mask != expected)
                {
                    return AbortEnergize_NoLock($"direction not verified (expected 0x{expected:X2})", mask);
                }

                RecordRelayState_NoLock(true, mask);
                return true;
            }
        }
    }

    private bool AbortEnergize_NoLock(string reason, int? mask)
    {
        _logger.LogCritical("Relay energize sequence aborted: {Reason}; register {Mask}. Driving all relays off", reason, FormatMask(mask));
        // The energize request failed whatever the all-off outcome; the relay state reflects the all-off read-back.
        AllRelaysOff_NoLock();
        return false;
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
            var result = _hat.GetAllDigitalInputs();
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
        _lastError = $"Safety input read failed ({_consecutiveInputReadFailures} consecutive): {error?.Message ?? "unknown error"}";
        _logger.LogWarning(error, "Safety input read failed ({Failures} consecutive)", _consecutiveInputReadFailures);
        return false;
    }

    #endregion

    #region Supervision

    /// <summary>
    /// One supervision pass: retries an unverified relay state, reads the inputs, applies the safety rules, compares the
    /// relay register with the commanded state and enforces the watchdog, lease and at-speed deadlines.
    /// Runs on the background loop; tests call it directly.
    /// </summary>
    internal void RunSupervisionCycle()
    {
        lock (_syncLock)
        {
            if (_disposed || _shuttingDown || !_initialized)
            {
                return;
            }

            var now = Now;

            if (_relayRegisterState == RoofRelayRegisterState.Unverified && !_clearFaultInProgress && _commandedMotion == RoofMotionDirection.None)
            {
                if (AllRelaysOff_NoLock())
                {
                    _logger.LogWarning("Relay register all-off re-verified by supervision; the RelayVerificationFailed latch remains until cleared");
                }
            }

            if (ReadInputs_NoLock())
            {
                Evaluate_NoLock(now);
            }
            else if (_commandedMotion != RoofMotionDirection.None && _consecutiveInputReadFailures >= _options.MaxConsecutiveInputReadFailures)
            {
                StopMotion_NoLock(RoofControllerStopReason.InputReadFailure,
                    $"Safety inputs could not be read {_consecutiveInputReadFailures} consecutive times while moving.");
            }

            CheckRelayRegister_NoLock();
            CheckDeadlines_NoLock(now);
            FinishMutation_NoLock();
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
            AllRelaysOff_NoLock();
            Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
        }
    }

    /// <summary>
    /// A periodic relay register read failed. Below <see cref="MaxConsecutiveRelayReadFailures"/> the last verified
    /// state is kept and the failure reported. At the threshold, commanded motion stops and latches
    /// <see cref="RoofControllerStopReason.RelayVerificationFailed"/>; when idle, the all-off sequence runs again and
    /// latches only if it cannot verify. Status and readiness report unhealthy relay reads until a read succeeds.
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
                    $"The {limitName} limit did not release within {_options.DepartureReleaseTimeout?.TotalSeconds:0.#} s of the motion command; the roof may be jammed or moving the wrong way.");
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
        if ((_options.DriveStopConfirmationTimeout ?? _options.AtSpeedConfirmationTimeout) is not { } window
            || _lastMotionStopUtc is not { } stoppedAt
            || _driveRunningAfterStopReported
            || now - stoppedAt < window
            || _rawIn4 != true)
        {
            return;
        }

        _driveRunningAfterStopReported = true;
        _lastError = $"Drive still reports running (IN4) {(now - stoppedAt).TotalSeconds:0.#} s after the relays were released.";
        _logger.LogCritical("Drive still reports running (IN4) {Seconds:0.#}s after stop ({Reason}); relays read back off. Check the drive and use the independent hardware stop",
            (now - stoppedAt).TotalSeconds, _lastStopReason);
    }

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
            lock (_syncLock)
            {
                if (_disposed || _shuttingDown)
                {
                    return;
                }

                delay = ComputeSupervisionDelay_NoLock(Now);
                var previous = _supervisionWakeCts;
                wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                _supervisionWakeCts = wake;
                previous?.Dispose();
            }

            await Task.Delay(delay, _timeProvider, wake.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (wake.IsCancellationRequested)
            {
                // Woken early (motion started); recompute the delay without an extra read.
                continue;
            }

            try
            {
                RunSupervisionCycle();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supervision cycle failed");
            }
        }
    }

    #endregion
}
