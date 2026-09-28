using System.Diagnostics;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

// Public commands: initialize, open/close, stop, lease renewal, clear-fault, configuration, shutdown and disposal.
public partial class RoofControllerServiceV4
{
    #region Initialize

    /// <inheritdoc />
    public Task<Result<bool>> Initialize(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (_shuttingDown || _disposed)
            {
                return Task.FromResult(RejectShuttingDown_NoLock<bool>());
            }

            if (_initialized)
            {
                return Task.FromResult(Reject_NoLock<bool>(RoofControllerErrorCode.InvalidRequest, "The roof controller is already initialized."));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(Result<bool>.Failure(new OperationCanceledException(cancellationToken)));
            }

            // 1. De-energize everything (including the clear-fault relay) and verify the register.
            if (!AllRelaysOff_NoLock())
            {
                const string message = "Initialization could not verify the relay register all-off state.";
                Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
                _logger.LogCritical("{Message} Register {Mask}. The controller will not accept motion commands.", message, FormatMask(_relayRegisterMask));
                FinishMutation_NoLock();
                return Task.FromResult(Reject_NoLock<bool>(RoofControllerErrorCode.RelayStateUnverified, message));
            }

            // 2. Refuse invalid or unsafe configuration.
            var validation = _optionsValidator.Validate(null, _options);
            if (validation.Failed)
            {
                var message = "Roof controller configuration is invalid: " + validation.FailureMessage;
                _logger.LogCritical("{Message}", message);
                FinishMutation_NoLock();
                return Task.FromResult(Reject_NoLock<bool>(RoofControllerErrorCode.ConfigurationRejected, message));
            }

            if (IgnoringLimitsOnHardwareWithoutConsent(_options))
            {
                const string message = "IgnorePhysicalLimitSwitches is set on physical hardware without AllowIgnoringLimitSwitchesOnPhysicalHardware; refusing to initialize.";
                _logger.LogCritical("{Message}", message);
                FinishMutation_NoLock();
                return Task.FromResult(Reject_NoLock<bool>(RoofControllerErrorCode.ConfigurationRejected, message));
            }

            // 3. Fresh read of the safety inputs.
            if (!ReadInputs_NoLock())
            {
                FinishMutation_NoLock();
                return Task.FromResult(Reject_NoLock<bool>(RoofControllerErrorCode.HardwareUnavailable,
                    "Safety inputs could not be read during initialization."));
            }

            _initialized = true;
            ApplyHardwareSettings_NoLock();
            ApplyIndicatorLedModes_NoLock();

            // 4. A fault present at startup (IN3 active, contradictory limits) latches here.
            Evaluate_NoLock(Now);
            if (_faultLatched)
            {
                _logger.LogWarning("Roof controller initialized with a latched safety fault: {Reason}. Motion is refused until ClearFault succeeds.", _latchedFaultReason);
            }

            if (_options.IgnorePhysicalLimitSwitches)
            {
                _logger.LogWarning("Roof controller initialized with physical limit switches IGNORED (hardware backed: {Hardware}).", _hat.IsHardwareBacked);
            }

            StartSupervision_NoLock();
            FinishMutation_NoLock();
            _logger.LogInformation("Roof controller initialized. Status={Status} Relay={RelayState}", _status, _relayRegisterState);
            return Task.FromResult(Result<bool>.Success(true));
        }
    }

    private bool IgnoringLimitsOnHardwareWithoutConsent(RoofControllerOptionsV4 options)
        => options.IgnorePhysicalLimitSwitches && _hat.IsHardwareBacked && !options.AllowIgnoringLimitSwitchesOnPhysicalHardware;

    #endregion

    #region Motion commands

    /// <inheritdoc />
    public Result<RoofControllerStatus> Open() => ExecuteMotionCommand("open", RoofMotionDirection.Opening);

    /// <inheritdoc />
    public Result<RoofControllerStatus> Close() => ExecuteMotionCommand("close", RoofMotionDirection.Closing);

    private Result<RoofControllerStatus> ExecuteMotionCommand(string command, RoofMotionDirection direction)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RoofControllerTelemetry.StartCommand(command);
        Result<RoofControllerStatus> result;
        try
        {
            result = StartMotion(direction);
        }
        catch (Exception ex)
        {
            lock (_syncLock)
            {
                _logger.LogError(ex, "Unexpected error while starting {Direction}; stopping", direction);
                if (_commandedMotion != RoofMotionDirection.None)
                {
                    StopMotion_NoLock(RoofControllerStopReason.EmergencyStop, $"Unexpected error while starting {direction}: {ex.Message}");
                }

                FinishMutation_NoLock();
                result = Result<RoofControllerStatus>.Failure(Rejection_NoLock(RoofControllerErrorCode.Unknown, $"Unexpected error while starting {direction}.", ex));
            }
        }

        RoofControllerTelemetry.CompleteCommand(activity, command, result.IsSuccessful, startTimestamp);
        return result;
    }

    private Result<RoofControllerStatus> StartMotion(RoofMotionDirection direction)
    {
        lock (_syncLock)
        {
            if (_shuttingDown || _disposed)
            {
                return RejectShuttingDown_NoLock<RoofControllerStatus>();
            }

            if (!_initialized)
            {
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.NotInitialized, "The roof controller is not initialized.");
            }

            if (_clearFaultInProgress)
            {
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.OperationInProgress, "A clear-fault pulse is in progress.");
            }

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
                CheckDeadlines_NoLock(now);

                // The inputs re-read at an input deadline can show the destination limit: the roof arrived, so a
                // repeat has nothing left to do.
                if (repeat && _commandedMotion == RoofMotionDirection.None && _lastStopReason == RoofControllerStopReason.LimitSwitchReached
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

                // Only the lease expired, or the destination limit was reached, under a reversal: the roof is now
                // stopped (verified) and the reversal is a start.
            }

            // A repeat of the current command renews the operator lease only. It never extends the watchdog, which is
            // an absolute cap measured from motion start.
            if (_commandedMotion == direction)
            {
                if (_options.OperatorLeaseTimeout is { } leaseTimeout && _leaseDeadlineUtc is not null)
                {
                    _leaseDeadlineUtc = now + leaseTimeout;
                }

                FinishMutation_NoLock();
                return Result<RoofControllerStatus>.Success(_status);
            }

            // Reversal: stop (verified) before anything else.
            if (_commandedMotion != RoofMotionDirection.None)
            {
                if (!StopMotion_NoLock(RoofControllerStopReason.NormalStop, null))
                {
                    FinishMutation_NoLock();
                    return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified,
                        "Reversal refused: the stop before reversing could not verify the relay register.");
                }
            }

            // Every start requires a fresh, successful input read.
            if (!ReadInputs_NoLock())
            {
                FinishMutation_NoLock();
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.HardwareUnavailable,
                    "Safety inputs could not be read; motion refused.");
            }

            // A reversal's stop and this read take time, and IN4 can drop within them: measure the stop delay, and this
            // move's timing, from the read.
            now = Now;
            Evaluate_NoLock(now);
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
            if (_options.AtSpeedConfirmationTimeout is not null && _rawIn4 == true)
            {
                FinishMutation_NoLock();
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.InterlockActive,
                    "The drive still reports running (IN4); motion refused until it stops.");
            }

            // Already at the destination: nothing to energize.
            var destinationActive = direction == RoofMotionDirection.Opening ? OpenLimitActive_NoLock : ClosedLimitActive_NoLock;
            if (destinationActive == true)
            {
                _logger.LogInformation("{Direction} requested but the destination limit is already active; no motion", direction);
                FinishMutation_NoLock();
                return Result<RoofControllerStatus>.Success(_status);
            }

            // Arm all supervision before energizing.
            var departureLimit = direction == RoofMotionDirection.Opening ? ClosedLimitActive_NoLock : OpenLimitActive_NoLock;
            _motionGeneration++;
            _commandedMotion = direction;
            _lastMotionDirection = direction;
            _motionStartUtc = now;
            _lastMotionStopUtc = null;
            _driveStopDelayPending = null;
            _lastError = null;
            ArmWatchdog_NoLock(now);
            _leaseDeadlineUtc = _options.OperatorLeaseTimeout is { } lease ? now + lease : null;
            _atSpeedDeadlineUtc = _options.AtSpeedConfirmationTimeout is { } atSpeed ? now + atSpeed : null;
            _atSpeedConfirmed = false;
            _runLostUtc = null;
            _driveStoppedAtStart = _rawIn4 == false;
            _startedAtLimit = departureLimit == true;
            _departureReleaseVerified = departureLimit != true;
            _releaseObservedUtc = null;
            _departureDeadlineUtc = departureLimit == true && _options.DepartureReleaseTimeout is { } departure ? now + departure : null;

            if (!SetRelayStatesAtomically(stopRelay: true, openRelay: direction == RoofMotionDirection.Opening, closeRelay: direction == RoofMotionDirection.Closing))
            {
                StopMotion_NoLock(RoofControllerStopReason.RelayVerificationFailed,
                    $"The relay register did not verify while energizing {direction}; motion aborted.");
                FinishMutation_NoLock();
                return Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified,
                    $"{direction} aborted: the relay register did not verify the requested state.");
            }

            _logger.LogInformation("Roof {Direction} started. Watchdog={Watchdog}s Lease={Lease} AtSpeedTimeout={AtSpeed} DepartingFromLimit={Departing} DepartureTimeout={DepartureTimeout}",
                direction, _options.SafetyWatchdogTimeout.TotalSeconds, _options.OperatorLeaseTimeout, _options.AtSpeedConfirmationTimeout, departureLimit == true,
                _departureDeadlineUtc is null ? null : _options.DepartureReleaseTimeout);

            // Edges may have been missed between the read and the relay writes; the supervision cycle re-reads promptly.
            FinishMutation_NoLock();
            WakeSupervision_NoLock();
            return Result<RoofControllerStatus>.Success(_status);
        }
    }

    /// <inheritdoc />
    public Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RoofControllerTelemetry.StartCommand("stop");
        Result<RoofControllerStatus> result;
        lock (_syncLock)
        {
            if (_disposed)
            {
                result = RejectShuttingDown_NoLock<RoofControllerStatus>();
            }
            else
            {
                // A stop preempts a clear-fault pulse; the all-off sequence below also drops the clear-fault relay.
                _clearFaultCts?.Cancel();

                // A deadline that has already passed stops the motion first, so the recorded stop reason (and any latch)
                // is the deadline's rather than the operator's. The stop below then re-asserts and verifies all-off.
                if (_commandedMotion != RoofMotionDirection.None)
                {
                    CheckDeadlines_NoLock(Now);
                }

                var verified = _commandedMotion != RoofMotionDirection.None
                    ? StopMotion_NoLock(reason, null)
                    : StopIdle_NoLock(reason);
                FinishMutation_NoLock();

                result = verified
                    ? Result<RoofControllerStatus>.Success(_status)
                    : Reject_NoLock<RoofControllerStatus>(RoofControllerErrorCode.RelayStateUnverified,
                        "Stop could not verify the relay register all-off state. Use the independent hardware stop.");
            }
        }

        RoofControllerTelemetry.CompleteCommand(activity, "stop", result.IsSuccessful, startTimestamp);
        return result;
    }

    /// <inheritdoc />
    public Result<RoofStatusResponse> RenewLease()
    {
        lock (_syncLock)
        {
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
                $"Motion had passed a deadline ({_lastStopReason}) and the stop could not verify the relay register. Use the independent hardware stop.");
        }

        if (_faultLatched)
        {
            return Reject_NoLock<T>(RoofControllerErrorCode.FaultLatched,
                $"Motion had passed a safety deadline ({_lastStopReason}); motion stopped and the fault is latched.");
        }

        if (_lastStopReason == RoofControllerStopReason.LimitSwitchReached)
        {
            return Reject_NoLock<T>(RoofControllerErrorCode.LeaseNotActive, "Motion had already ended at the destination limit.");
        }

        return Reject_NoLock<T>(RoofControllerErrorCode.LeaseNotActive, "The operator lease had already expired; motion stopped.");
    }

    #endregion

    #region Clear fault

    /// <inheritdoc />
    public async Task<Result<bool>> ClearFault(int pulseMs = RoofControllerLimits.DefaultClearFaultPulseMilliseconds, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RoofControllerTelemetry.StartCommand("clear-fault");
        var (result, outcome) = await ClearFaultCoreAsync(pulseMs, cancellationToken).ConfigureAwait(false);
        RoofControllerTelemetry.RecordClearFault(outcome);
        RoofControllerTelemetry.CompleteCommand(activity, "clear-fault", result.IsSuccessful, startTimestamp);
        return result;
    }

    private async Task<(Result<bool> Result, string Outcome)> ClearFaultCoreAsync(int pulseMs, CancellationToken cancellationToken)
    {
        if (pulseMs < RoofControllerLimits.MinClearFaultPulseMilliseconds || pulseMs > RoofControllerLimits.MaxClearFaultPulseMilliseconds)
        {
            lock (_syncLock)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.InvalidRequest,
                    $"Clear-fault pulse must be between {RoofControllerLimits.MinClearFaultPulseMilliseconds} and {RoofControllerLimits.MaxClearFaultPulseMilliseconds} ms."), "rejected");
            }
        }

        if (_shuttingDown || _disposed)
        {
            lock (_syncLock)
            {
                return (RejectShuttingDown_NoLock<bool>(), "rejected");
            }
        }

        // Serializes clear-fault requests without blocking: a concurrent request is refused, never queued.
        if (!_clearFaultGate.Wait(0))
        {
            lock (_syncLock)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.OperationInProgress, "Another clear-fault pulse is in progress."), "rejected");
            }
        }

        try
        {
            return await RunClearFaultPulseAsync(pulseMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Independent of the relay release below; the gate is never disposed, so this cannot throw after disposal.
            _clearFaultGate.Release();
        }
    }

    private async Task<(Result<bool> Result, string Outcome)> RunClearFaultPulseAsync(int pulseMs, CancellationToken cancellationToken)
    {
        CancellationTokenSource pulseCts;
        bool pulseFailed;
        lock (_syncLock)
        {
            if (_shuttingDown || _disposed)
            {
                return (RejectShuttingDown_NoLock<bool>(), "rejected");
            }

            if (!_initialized)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.NotInitialized, "The roof controller is not initialized."), "rejected");
            }

            if (_commandedMotion != RoofMotionDirection.None)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.OperationInProgress, "ClearFault is refused while the roof is moving; stop first."), "rejected");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return (Result<bool>.Failure(new OperationCanceledException(cancellationToken)), "canceled");
            }

            if (!AllRelaysOff_NoLock())
            {
                const string message = "ClearFault could not verify the relay register all-off state before pulsing.";
                Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
                _logger.LogCritical("{Message} Register {Mask}.", message, FormatMask(_relayRegisterMask));
                FinishMutation_NoLock();
                return (Reject_NoLock<bool>(RoofControllerErrorCode.RelayStateUnverified, message), "relay_unverified");
            }

            pulseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _clearFaultCts = pulseCts;
            _clearFaultInProgress = true;
            pulseFailed = !AssertClearFaultRelay_NoLock();
            if (pulseFailed)
            {
                _logger.LogError("Clear-fault relay (RLY{Relay}) could not be verified asserted; releasing", _options.ClearFaultRelayId);
            }
            else
            {
                _logger.LogInformation("Clear-fault relay (RLY{Relay}) asserted for {PulseMs} ms", _options.ClearFaultRelayId, pulseMs);
            }

            FinishMutation_NoLock();
        }

        var preempted = false;
        var releaseFailed = false;
        try
        {
            if (!pulseFailed)
            {
                // Never awaited under the lock; cancellation (Stop, shutdown, caller) ends the pulse early.
                await Task.Delay(TimeSpan.FromMilliseconds(pulseMs), _timeProvider, pulseCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
                preempted = pulseCts.IsCancellationRequested;
            }
        }
        finally
        {
            lock (_syncLock)
            {
                _clearFaultInProgress = false;
                _clearFaultCts = null;

                // Disposal has already driven every relay (including the clear-fault relay) off.
                if (!_disposed && !ReleaseClearFaultRelay_NoLock())
                {
                    releaseFailed = true;
                    const string message = "The clear-fault relay could not be verified released.";
                    Latch_NoLock(RoofControllerStopReason.RelayVerificationFailed, message);
                    _logger.LogCritical("{Message} Register {Mask}. Relay state is UNVERIFIED.", message, FormatMask(_relayRegisterMask));
                }

                FinishMutation_NoLock();
            }

            pulseCts.Dispose();
        }

        lock (_syncLock)
        {
            if (pulseFailed || releaseFailed)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.RelayStateUnverified,
                    "The clear-fault relay pulse could not be verified; the fault latch is unchanged."), "relay_unverified");
            }

            if (_shuttingDown || _disposed)
            {
                return (RejectShuttingDown_NoLock<bool>(), "preempted");
            }

            if (preempted)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.OperationInProgress,
                    "The clear-fault pulse was preempted by a stop or cancellation; the fault latch is unchanged."), "preempted");
            }

            if (!ReadInputs_NoLock())
            {
                FinishMutation_NoLock();
                return (Reject_NoLock<bool>(RoofControllerErrorCode.HardwareUnavailable,
                    "Safety inputs could not be read after the clear-fault pulse; the fault latch is unchanged."), "inputs_unavailable");
            }

            Evaluate_NoLock(Now);
            if (DriveFaultActive_NoLock == true || LimitsContradictory_NoLock)
            {
                FinishMutation_NoLock();
                var detail = DriveFaultActive_NoLock == true ? "the drive fault input (IN3) is still active" : "both limit switches still report active";
                return (Reject_NoLock<bool>(RoofControllerErrorCode.InterlockActive,
                    $"Fault not cleared: {detail}. The fault latch is unchanged."), "fault_active");
            }

            if (_relayRegisterState != RoofRelayRegisterState.Verified)
            {
                FinishMutation_NoLock();
                return (Reject_NoLock<bool>(RoofControllerErrorCode.RelayStateUnverified,
                    "The relay register is not verified all-off; the fault latch is unchanged."), "relay_unverified");
            }

            if (_faultLatched)
            {
                _logger.LogWarning("Safety fault latch cleared by ClearFault (was {Reason})", _latchedFaultReason);
            }

            _faultLatched = false;
            _latchedFaultReason = null;
            _lastError = null;
            FinishMutation_NoLock();
            return (Result<bool>.Success(true), "cleared");
        }
    }

    #endregion

    #region Configuration

    /// <inheritdoc />
    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions)
        => UpdateConfigurationCore(updatedOptions, expectedVersion: null);

    /// <inheritdoc />
    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions, long expectedVersion)
        => UpdateConfigurationCore(updatedOptions, expectedVersion);

    private Result<RoofControllerOptionsV4> UpdateConfigurationCore(RoofControllerOptionsV4? updatedOptions, long? expectedVersion)
    {
        lock (_syncLock)
        {
            if (updatedOptions is null)
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.InvalidRequest, "Configuration is required.");
            }

            if (_shuttingDown || _disposed)
            {
                return RejectShuttingDown_NoLock<RoofControllerOptionsV4>();
            }

            if (expectedVersion is { } expected && expected != _configurationVersion)
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.ConfigurationVersionConflict,
                    $"Configuration version {expected} does not match the current version {_configurationVersion}.");
            }

            if (_commandedMotion != RoofMotionDirection.None || _clearFaultInProgress)
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.OperationInProgress,
                    "Configuration cannot be changed while the roof is moving or a clear-fault pulse is in progress.");
            }

            // The consent flag is local-only configuration; the remote update cannot change it.
            var candidate = updatedOptions with
            {
                AllowIgnoringLimitSwitchesOnPhysicalHardware = _options.AllowIgnoringLimitSwitchesOnPhysicalHardware
            };

            var validation = _optionsValidator.Validate(null, candidate);
            if (validation.Failed)
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.InvalidRequest,
                    "Configuration is invalid: " + validation.FailureMessage);
            }

            if (IgnoringLimitsOnHardwareWithoutConsent(candidate))
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.ConfigurationRejected,
                    "IgnorePhysicalLimitSwitches cannot be enabled on physical hardware unless AllowIgnoringLimitSwitchesOnPhysicalHardware is set in local configuration.");
            }

            var previous = _options;
            try
            {
                _options = candidate;
                if (_initialized)
                {
                    ApplyHardwareSettings_NoLock();
                }
            }
            catch (Exception ex)
            {
                _options = previous;
                try
                {
                    if (_initialized)
                    {
                        ApplyHardwareSettings_NoLock();
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Restoring the previous hardware settings failed after a configuration error");
                }

                _logger.LogError(ex, "Applying the configuration update failed; the previous configuration remains in effect");
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.Unknown,
                    "Applying the configuration failed; the previous configuration remains in effect.", ex);
            }

            _configurationVersion++;
            _logger.LogInformation("Roof controller configuration updated to version {Version}. Watchdog={Watchdog}s Polling={Polling} Periodic={Periodic} IgnoreLimits={IgnoreLimits}",
                _configurationVersion, _options.SafetyWatchdogTimeout.TotalSeconds, _options.EnableDigitalInputPolling,
                _options.EnablePeriodicVerificationWhileMoving, _options.IgnorePhysicalLimitSwitches);

            if (_initialized)
            {
                ReadInputs_NoLock();
                Evaluate_NoLock(Now);
            }

            FinishMutation_NoLock();
            return Result<RoofControllerOptionsV4>.Success(_options with { });
        }
    }

    private void ApplyHardwareSettings_NoLock()
    {
        _hat.DigitalInputPollInterval = _options.DigitalInputPollInterval;
        UpdateDigitalInputSubscriptions_NoLock();
    }

    private void UpdateDigitalInputSubscriptions_NoLock()
    {
        UnsubscribeInputs_NoLock();
        if (!_options.EnableDigitalInputPolling)
        {
            return;
        }

        if (!_options.IgnorePhysicalLimitSwitches)
        {
            _hatIn1Handler = (_, isHigh) => OnForwardLimitSwitchChanged(isHigh);
            _hatIn2Handler = (_, isHigh) => OnReverseLimitSwitchChanged(isHigh);
            _hat.DigitalInput1Changed += _hatIn1Handler;
            _hat.DigitalInput2Changed += _hatIn2Handler;
        }

        _hatIn3Handler = (_, isHigh) => OnFaultNotificationChanged(isHigh);
        _hatIn4Handler = (_, isHigh) => OnAtSpeedChanged(isHigh);
        _hat.DigitalInput3Changed += _hatIn3Handler;
        _hat.DigitalInput4Changed += _hatIn4Handler;
    }

    private void UnsubscribeInputs_NoLock()
    {
        try
        {
            if (_hatIn1Handler is not null) _hat.DigitalInput1Changed -= _hatIn1Handler;
            if (_hatIn2Handler is not null) _hat.DigitalInput2Changed -= _hatIn2Handler;
            if (_hatIn3Handler is not null) _hat.DigitalInput3Changed -= _hatIn3Handler;
            if (_hatIn4Handler is not null) _hat.DigitalInput4Changed -= _hatIn4Handler;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unsubscribe from HAT input events");
        }
        finally
        {
            _hatIn1Handler = null;
            _hatIn2Handler = null;
            _hatIn3Handler = null;
            _hatIn4Handler = null;
        }
    }

    #endregion

    #region Shutdown and disposal

    /// <inheritdoc />
    public async Task<Result<RoofStatusResponse>> ShutdownAsync(CancellationToken cancellationToken)
    {
        Task<bool> retry;
        lock (_syncLock)
        {
            if (_disposed)
            {
                return _relayRegisterState == RoofRelayRegisterState.Unverified
                    ? Result<RoofStatusResponse>.Failure(Rejection_NoLock(RoofControllerErrorCode.RelayStateUnverified,
                        "The controller was disposed with an unverified relay register state."))
                    : Result<RoofStatusResponse>.Success(BuildSnapshot_NoLock(Now, forKey: false));
            }

            if (_shutdownStopRetryTask is { IsCompleted: false } running)
            {
                // An earlier call's retry is still driving the relays off: join it rather than start another.
                retry = running;
            }
            else
            {
                var firstCall = !_shuttingDown;
                _shuttingDown = true;
                _clearFaultCts?.Cancel();

                var verified = _commandedMotion != RoofMotionDirection.None
                    ? StopMotion_NoLock(RoofControllerStopReason.HostShutdown, null)
                    : StopIdle_NoLock(RoofControllerStopReason.HostShutdown);

                StopSupervision_NoLock();
                UnsubscribeInputs_NoLock();

                if (verified)
                {
                    FinishMutation_NoLock();
                    if (firstCall)
                    {
                        _logger.LogInformation("Roof controller shutting down; relay register verified all-off");
                    }

                    return Result<RoofStatusResponse>.Success(BuildSnapshot_NoLock(Now, forKey: false));
                }

                // Supervision has stopped, so nothing else would re-drive the relays off before the process exits.
                _lastError = $"Shutdown could not verify the relay register all-off state; retrying every {ShutdownStopRetryInterval.TotalMilliseconds:0} ms for up to {ShutdownStopRetryWindow.TotalSeconds:0} s. Use the independent hardware stop.";
                _logger.LogCritical("Shutdown could not verify the relay register all-off state (register {Mask}); retrying the all-off sequence every {Interval} for up to {Window}. Use the independent hardware stop.",
                    FormatMask(_relayRegisterMask), ShutdownStopRetryInterval, ShutdownStopRetryWindow);
                FinishMutation_NoLock();
                retry = StartShutdownStopRetry_NoLock();
            }
        }

        bool verifiedByRetry;
        try
        {
            verifiedByRetry = await retry.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_syncLock)
            {
                return Result<RoofStatusResponse>.Failure(Rejection_NoLock(RoofControllerErrorCode.RelayStateUnverified,
                    "Shutdown could not verify the relay register all-off state; the all-off sequence is still being retried in the background. Use the independent hardware stop."));
            }
        }

        lock (_syncLock)
        {
            return verifiedByRetry
                ? Result<RoofStatusResponse>.Success(BuildSnapshot_NoLock(Now, forKey: false))
                : Result<RoofStatusResponse>.Failure(Rejection_NoLock(RoofControllerErrorCode.RelayStateUnverified,
                    "Shutdown could not verify the relay register all-off state. Use the independent hardware stop."));
        }
    }

    /// <summary>
    /// Starts the bounded all-off retry after an unverified shutdown stop. The retry runs synchronously up to its first
    /// delay, so its timer exists before this returns; it never awaits while holding the lock.
    /// </summary>
    private Task<bool> StartShutdownStopRetry_NoLock()
    {
        _shutdownStopRetryCts?.Dispose();
        _shutdownStopRetryCts = new CancellationTokenSource();
        _shutdownStopRetryTask = RetryShutdownStopAsync(Now + ShutdownStopRetryWindow, _shutdownStopRetryCts.Token);
        return _shutdownStopRetryTask;
    }

    /// <summary>
    /// Re-runs the all-off sequence every <see cref="ShutdownStopRetryInterval"/> until the relay register verifies, the
    /// window passes, or disposal cancels it. Motion stays rejected throughout (the controller is shutting down), and the
    /// <see cref="RoofControllerStopReason.RelayVerificationFailed"/> latch is kept even when a retry verifies.
    /// Returns true when the register verified all-off.
    /// </summary>
    private async Task<bool> RetryShutdownStopAsync(DateTimeOffset deadline, CancellationToken token)
    {
        var attempts = 0;
        while (true)
        {
            await Task.Delay(ShutdownStopRetryInterval, _timeProvider, token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);

            lock (_syncLock)
            {
                if (_relayRegisterState == RoofRelayRegisterState.Verified)
                {
                    // Another stop sequence (an operator Stop, or disposal) verified the register.
                    return true;
                }

                if (_disposed || token.IsCancellationRequested)
                {
                    return false;
                }

                attempts++;
                if (AllRelaysOff_NoLock())
                {
                    _lastError = $"The shutdown stop verified the relay register all-off after {attempts} retries; the relay verification fault stays latched.";
                    _logger.LogWarning("Shutdown stop retry {Attempt} verified the relay register all-off", attempts);
                    FinishMutation_NoLock();
                    return true;
                }

                if (Now >= deadline)
                {
                    _lastError = $"Shutdown could not verify the relay register all-off state after {attempts} retries; relay state is unknown. Use the independent hardware stop.";
                    _logger.LogCritical("Shutdown stop retry gave up after {Attempts} attempts over {Window}; relay register {Mask} is UNVERIFIED. Use the independent hardware stop.",
                        attempts, ShutdownStopRetryWindow, FormatMask(_relayRegisterMask));
                    FinishMutation_NoLock();
                    return false;
                }

                FinishMutation_NoLock();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !BeginDispose(out var pending))
        {
            return;
        }

        // A StatusChanged handler that disposes the service must not wait for its own dispatcher.
        if (t_onStatusDispatcherThread)
        {
            return;
        }

        try
        {
            if (!Task.WaitAll(pending, DisposeWaitTimeout))
            {
                _logger.LogWarning("Roof controller background tasks did not finish within {Timeout}", DisposeWaitTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Roof controller background task faulted during disposal");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (BeginDispose(out var pending) && !t_onStatusDispatcherThread)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(DisposeWaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Roof controller background tasks did not finish within {Timeout}", DisposeWaitTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Roof controller background task faulted during disposal");
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Publishes the disposed state first (so no new command is admitted), then stops and verifies all relays off.
    /// Returns false when there is nothing to wait for: disposal already happened or is deferred, or the controller lock
    /// could not be acquired within <see cref="DisposeWaitTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The lock is held across synchronous HAT I/O, which cannot be interrupted if an I2C transfer wedges. Disposal then
    /// stops waiting rather than block the host's teardown: it marks the controller shutting down (no new command is
    /// admitted), logs Critical and hands the disposal to <see cref="DeferredDisposalTask"/>, which takes the lock when
    /// the blocked call returns and runs the same all-off stop. The call that was blocked (a reversal, say) may energize
    /// relays when it resumes, so the controller is not marked disposed until that stop has run.
    /// </remarks>
    private bool BeginDispose(out Task[] pending)
    {
        pending = [];
        if (_disposed || Volatile.Read(ref _deferredDisposalTask) is not null)
        {
            return false;
        }

        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_syncLock, DisposeWaitTimeout, ref lockTaken);
            if (!lockTaken)
            {
                _shuttingDown = true;
                var deferred = new Task(RunDeferredDisposal);
                if (Interlocked.CompareExchange(ref _deferredDisposalTask, deferred, null) is null)
                {
                    _logger.LogCritical("Disposal could not acquire the controller lock within {Timeout}: a call is still blocked in HAT I/O. The all-off stop will run when the call returns; until then relay state is unknown, so use the independent hardware stop.",
                        DisposeWaitTimeout);
                    deferred.Start(TaskScheduler.Default);
                }

                return false;
            }

            return !_disposed && DisposeCore_NoLock(out pending);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_syncLock);
            }
        }
    }

    /// <summary>The disposal that <see cref="BeginDispose"/> deferred because the lock was held, or null.</summary>
    internal Task? DeferredDisposalTask => Volatile.Read(ref _deferredDisposalTask);

    private void RunDeferredDisposal()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            DisposeCore_NoLock(out _);
            _logger.LogWarning("Deferred disposal ran after the blocked HAT call returned; relay register {Mask} is {State}.",
                FormatMask(_relayRegisterMask), _relayRegisterState);
        }
    }

    private bool DisposeCore_NoLock(out Task[] pending)
    {
        _shuttingDown = true;
        _disposed = true;
        _clearFaultCts?.Cancel();
        _shutdownStopRetryCts?.Cancel();

        var verified = _commandedMotion != RoofMotionDirection.None
            ? StopMotion_NoLock(RoofControllerStopReason.SystemDisposal, null)
            : StopIdle_NoLock(RoofControllerStopReason.SystemDisposal);
        if (!verified)
        {
            _logger.LogCritical("Disposal could not verify the relay register all-off state (register {Mask}). Use the independent hardware stop.",
                FormatMask(_relayRegisterMask));
        }

        StopSupervision_NoLock();
        UnsubscribeInputs_NoLock();
        FinishMutation_NoLock();
        _statusChannel.Writer.TryComplete();

        // Disposal made its own all-off attempt above; the cancelled shutdown retry ends at its next wake.
        pending = new[] { _supervisionTask, _shutdownStopRetryTask, _statusDispatcherTask }.OfType<Task>().ToArray();
        return true;
    }

    #endregion
}
