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

            // The relays were off before this start (or their state was unknown): every start waits out the
            // direction-change delay from here, as after any stop.
            _lastDriveOffUtc = Now;
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

            // A HAT access past its time limit may still switch a relay: nothing is cleared until it has returned and the
            // relays have been written off and verified (#220, §19 M1, §20 N3).
            if (_hatIo.IsOverrun)
            {
                return (Reject_NoLock<bool>(RoofControllerErrorCode.RelayStateUnverified,
                    "A HAT access is still running past its time limit; the relays cannot be verified until it returns, so the fault is not cleared yet."), "rejected");
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
            _continuationRefused = false;
            FinishMutation_NoLock();
            return (Result<bool>.Success(true), "cleared");
        }
    }

    #endregion

    #region Configuration

    /// <inheritdoc />
    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions)
        => UpdateConfigurationCore(updatedOptions, expectedVersion: null, includeLocalOnlySettings: false);

    /// <inheritdoc />
    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions, long expectedVersion)
        => UpdateConfigurationCore(updatedOptions, expectedVersion, includeLocalOnlySettings: false);

    /// <inheritdoc />
    public Result<RoofControllerOptionsV4> ApplyConfiguration(RoofControllerOptionsV4 updatedOptions, bool includeLocalOnlySettings)
        => UpdateConfigurationCore(updatedOptions, expectedVersion: null, includeLocalOnlySettings);

    private Result<RoofControllerOptionsV4> UpdateConfigurationCore(
        RoofControllerOptionsV4? updatedOptions,
        long? expectedVersion,
        bool includeLocalOnlySettings)
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

            if (_commandedMotion != RoofMotionDirection.None || _pendingStart is not null || _clearFaultInProgress)
            {
                return Reject_NoLock<RoofControllerOptionsV4>(RoofControllerErrorCode.OperationInProgress,
                    "Configuration cannot be changed while the roof is moving (or a start waits out the direction-change delay) or a clear-fault pulse is in progress.");
            }

            // The consent flag and the drive-stop and departure windows are local-only configuration: only a caller the
            // settings store has checked for a local credential may change them.
            var candidate = includeLocalOnlySettings
                ? updatedOptions with { }
                : updatedOptions with
                {
                    AllowIgnoringLimitSwitchesOnPhysicalHardware = _options.AllowIgnoringLimitSwitchesOnPhysicalHardware,
                    DriveStopConfirmationTimeout = _options.DriveStopConfirmationTimeout,
                    DepartureReleaseTimeout = _options.DepartureReleaseTimeout,
                    DirectionChangeDelay = _options.DirectionChangeDelay
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
        // The intent and the relay fence go up before shutdown waits for the lock (#220, PR #230 rereview F11): a start
        // that passed its checks and holds the lock never energizes, and nothing new is admitted meanwhile.
        var firstCall = !_shuttingDown;
        if (!_disposed)
        {
            _shuttingDown = true;
            FenceRelaysOff();
        }

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
                _shuttingDown = true;
                _clearFaultCts?.Cancel();
                CancelPendingStart_NoLock("The controller is shutting down; nothing moved for this start.");

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

            if (!_disposed && _hatIo.IsOverrun)
            {
                // A HAT access past its time limit may still switch a relay when it returns: the all-off stop waits for it.
                _shuttingDown = true;
                var deferred = new Task(() =>
                {
                    while (_hatIo.IsOverrun)
                    {
                        Thread.Sleep(20);
                    }

                    RunDeferredDisposal();
                }, TaskCreationOptions.LongRunning);
                if (Interlocked.CompareExchange(ref _deferredDisposalTask, deferred, null) is null)
                {
                    _logger.LogCritical("Disposal found a HAT access still running past its time limit. The all-off stop will run when it returns; until then relay state is unknown, so use the independent hardware stop.");
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
        CancelPendingStart_NoLock("The controller is shutting down; nothing moved for this start.");

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

        // The HAT's lane outlives this controller (its thread idles a while), so it must not keep it.
        _hatIo.Recovered -= OnHatIoRecovered;
        FinishMutation_NoLock();
        _statusChannel.Writer.TryComplete();

        // Disposal made its own all-off attempt above; the cancelled shutdown retry ends at its next wake.
        pending = new[] { _supervisionTask, _shutdownStopRetryTask, _statusDispatcherTask }.OfType<Task>().ToArray();
        return true;
    }

    #endregion
}
