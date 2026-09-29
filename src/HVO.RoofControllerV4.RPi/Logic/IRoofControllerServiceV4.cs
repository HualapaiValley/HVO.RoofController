using System;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

public interface IRoofControllerServiceV4
{
        /// <summary>
        /// Event raised when status or watchdog telemetry changes. Snapshots are delivered in order on a background
        /// dispatcher (never on the caller's thread and never under the controller lock); handlers must marshal to their
        /// own context and must not block. Handler exceptions are logged and do not affect delivery to other handlers.
        /// </summary>
        event EventHandler<RoofStatusChangedEventArgs>? StatusChanged;

        /// <summary>
        /// Gets a value indicating whether the roof controller has been initialized and is ready for operation.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Gets a value indicating whether the controller is currently bound to physical I²C hardware instead of simulation.
        /// </summary>
        bool IsUsingPhysicalHardware { get; }

        /// <summary>
        /// Gets a value indicating whether physical limit switch inputs are currently being ignored by the controller.
        /// </summary>
        bool IsIgnoringPhysicalLimitSwitches { get; }

        /// <summary>
        /// Gets the current operational status of the roof controller.
        /// </summary>
        RoofControllerStatus Status { get; }

        /// <summary>
        /// True exactly while motion is commanded (Opening or Closing relays requested). This reflects the commanded state,
        /// not the displayed <see cref="Status"/> and not physical movement.
        /// </summary>
        bool IsMoving { get; }

        /// <summary>
        /// Gets the reason for the last stop operation.
        /// </summary>
        RoofControllerStopReason LastStopReason { get; }

        /// <summary>
        /// UTC timestamp of the last status transition.
        /// </summary>
        DateTimeOffset? LastTransitionUtc { get; }

        /// <summary>
        /// Gets a value indicating whether the safety watchdog is currently active (roof in motion and timer running).
        /// </summary>
        bool IsWatchdogActive { get; }

        /// <summary>
        /// Gets the number of seconds remaining on the watchdog timer, or null if not active.
        /// </summary>
        double? WatchdogSecondsRemaining { get; }

        /// <summary>
        /// True when the drive signals it is at commanded speed (AtSpeed/Run, IN4 logical state).
        /// </summary>
        bool IsAtSpeed { get; }

        /// <summary>
        /// Returns a coherent snapshot of status, taken under the controller lock, for UI/API/health consumption.
        /// Prefer this over reading individual properties, which can change between reads.
        /// </summary>
        RoofStatusResponse GetCurrentStatusSnapshot();

        /// <summary>
        /// Returns a snapshot of the current configuration options applied to the controller.
        /// </summary>
        RoofControllerOptionsV4 GetConfigurationSnapshot();

        /// <summary>
        /// Applies a configuration update against the current version (no version check). Otherwise identical to the
        /// versioned overload: validated, transactional, refused while moving or during a clear-fault pulse
        /// (<see cref="RoofControllerErrorCode.OperationInProgress"/>). Invalid options fail with
        /// <see cref="RoofControllerErrorCode.InvalidRequest"/>. The local-only settings
        /// (<c>AllowIgnoringLimitSwitchesOnPhysicalHardware</c>, <c>DriveStopConfirmationTimeout</c> and
        /// <c>DepartureReleaseTimeout</c>) are never changed by this overload.
        /// </summary>
        /// <param name="updatedOptions">The configuration values to apply.</param>
        /// <returns>A result containing the effective configuration when successful.</returns>
        Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions);

        /// <summary>
        /// Applies a configuration update only if the current configuration version equals <paramref name="expectedVersion"/>.
        /// Fails with <see cref="RoofControllerErrorCode.ConfigurationVersionConflict"/> on mismatch, and with
        /// <see cref="RoofControllerErrorCode.ConfigurationRejected"/> when the options are unsafe for the current hardware mode.
        /// The update is transactional: on failure the previous options, timers and subscriptions remain in effect. The
        /// local-only settings are never changed by this overload.
        /// </summary>
        Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions, long expectedVersion);

        /// <summary>
        /// Applies options the settings store has already checked: the same validation, motion check and transaction as
        /// <see cref="UpdateConfiguration(RoofControllerOptionsV4)"/>, with no version check. The local-only settings
        /// are applied only when <paramref name="includeLocalOnlySettings"/> is true (the caller holds a local
        /// credential, or the values come from the controller's own files); otherwise the current ones are kept.
        /// </summary>
        Result<RoofControllerOptionsV4> ApplyConfiguration(RoofControllerOptionsV4 updatedOptions, bool includeLocalOnlySettings);

        /// <summary>
        /// Returns the configuration and its version, read atomically.
        /// </summary>
        RoofControllerConfigurationState GetConfigurationState();

        /// <summary>
        /// True if the underlying service has been disposed (not available for use).
        /// </summary>
        bool IsServiceDisposed { get; }

        /// <summary>
        /// True once shutdown or disposal has begun. No new motion or clear-fault pulse is admitted after this becomes true.
        /// </summary>
        bool IsShuttingDown { get; }

        /// <summary>
        /// Renews the optional operator lease for the motion in progress. Never starts motion.
        /// Fails with <see cref="RoofControllerErrorCode.LeaseNotActive"/> when no leased motion is active.
        /// </summary>
        Result<RoofStatusResponse> RenewLease();

        /// <summary>
        /// Begins host shutdown: publishes the shutting-down state so no new command is admitted, cancels any clear-fault
        /// pulse, stops supervision, and stops motion with <see cref="RoofControllerStopReason.HostShutdown"/>.
        /// </summary>
        /// <remarks>
        /// <para>When the all-off state cannot be verified, a bounded background retry re-runs the all-off sequence every
        /// 500 ms for up to 15 s, or until disposal. Motion stays rejected throughout, and the relay verification fault
        /// stays latched even if a retry verifies. The returned task completes when the register verifies, when the retry
        /// gives up, or when <paramref name="cancellationToken"/> is cancelled (the retry then continues in the
        /// background). It fails with <see cref="RoofControllerErrorCode.RelayStateUnverified"/> unless the register
        /// verified all-off.</para>
        /// <para>The first stop attempt runs synchronously on the calling thread and performs HAT I/O under the controller
        /// lock, so a caller that must bound the call has to offload it. Safe to call more than once; a call made while a
        /// retry is running joins that retry.</para>
        /// </remarks>
        Task<Result<RoofStatusResponse>> ShutdownAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Drives every relay off (including clear-fault) and verifies the register, validates the configuration, reads the
        /// safety inputs, subscribes to input edges and starts background supervision.
        /// </summary>
        /// <remarks>
        /// Fails with <see cref="RoofControllerErrorCode.ShuttingDown"/>, <see cref="RoofControllerErrorCode.InvalidRequest"/>
        /// (already initialized), <see cref="RoofControllerErrorCode.RelayStateUnverified"/> (all-off not verified; latched),
        /// <see cref="RoofControllerErrorCode.ConfigurationRejected"/> (invalid options, or limit switches ignored on physical
        /// hardware without local consent) or <see cref="RoofControllerErrorCode.HardwareUnavailable"/> (inputs unreadable).
        /// A fault present at startup (drive fault input active, contradictory limits) does not fail initialization: it is
        /// latched and reported through the snapshot and health check.
        /// </remarks>
        /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
        /// <returns>True when initialization succeeded.</returns>
        Task<Result<bool>> Initialize(CancellationToken cancellationToken);

        /// <summary>
        /// Stops all motion: drives every relay off and verifies the register read-back. Always admitted (before
        /// initialization, while a fault is latched and during shutdown); only a disposed controller refuses it
        /// (<see cref="RoofControllerErrorCode.ShuttingDown"/>). Preempts a clear-fault pulse. Does not clear a latched fault.
        /// </summary>
        /// <remarks>
        /// When the all-off state cannot be verified the result fails with
        /// <see cref="RoofControllerErrorCode.RelayStateUnverified"/>, the snapshot shows an unverified relay register and
        /// <see cref="RoofControllerStatus.Error"/>, and <see cref="RoofControllerStopReason.RelayVerificationFailed"/> is
        /// latched while <see cref="LastStopReason"/> keeps the original cause. A stop while idle re-verifies all-off and
        /// does not change <see cref="LastStopReason"/>. Read-back proves the HAT register, not the relay contacts.
        /// </remarks>
        /// <param name="reason">The reason for stopping the operation.</param>
        /// <returns>The resulting status when the all-off state was verified.</returns>
        Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop);

        /// <summary>
        /// Starts opening. Requires a fresh input read and an unlatched controller; the watchdog, optional operator lease
        /// and optional at-speed interlock are armed before the relays are energized. Repeating Open while opening renews
        /// the lease but never extends the watchdog. Opening while closing performs a verified stop first. Succeeds without
        /// energizing when the open limit is already active.
        /// </summary>
        /// <remarks>
        /// Fails with ShuttingDown, NotInitialized, OperationInProgress (clear-fault pulse), FaultLatched, InterlockActive
        /// (drive fault input or contradictory limits), HardwareUnavailable (inputs unreadable) or RelayStateUnverified.
        /// </remarks>
        /// <returns>A result containing the updated roof controller status.</returns>
        Result<RoofControllerStatus> Open();

        /// <summary>
        /// Starts closing. Same rules and failure codes as <see cref="Open"/>.
        /// </summary>
        /// <returns>A result containing the updated roof controller status.</returns>
        Result<RoofControllerStatus> Close();

        /// <summary>
        /// Re-evaluates the safety rules and republishes status. Reads the inputs directly when <paramref name="forceHardwareRead"/>
        /// is true, when input polling is disabled, or when the cached inputs are not healthy (stale or failed reads).
        /// </summary>
        /// <param name="forceHardwareRead">If true forces direct hardware read.</param>
        void RefreshStatus(bool forceHardwareRead = false);
        
        /// <summary>
        /// Pulses the clear-fault relay (RLY3) to reset the drive, then clears the latched safety fault only when the
        /// inputs read healthy, the drive fault input (IN3) is inactive, the limits are consistent and the relay register
        /// is verified all-off.
        /// </summary>
        /// <remarks>
        /// <para>The pulse must be between <see cref="RoofControllerLimits.MinClearFaultPulseMilliseconds"/> and
        /// <see cref="RoofControllerLimits.MaxClearFaultPulseMilliseconds"/> ms (otherwise InvalidRequest). Refused while
        /// moving or while another pulse runs (OperationInProgress; concurrent requests are refused, not queued).</para>
        /// <para>A Stop, shutdown or cancellation preempts the pulse (OperationInProgress) and the relay is released. If the
        /// release cannot be verified the result is RelayStateUnverified and RelayVerificationFailed is latched. When the
        /// drive fault is still active afterwards the result is InterlockActive and the latch is kept. Inputs unreadable
        /// after the pulse: HardwareUnavailable. A clear-fault request is never reported as an emergency stop.</para>
        /// </remarks>
        /// <param name="pulseMs">Duration to hold the clear-fault relay active.</param>
        /// <param name="cancellationToken">Cancellation token that ends the pulse early.</param>
        /// <returns>True when the latch was cleared.</returns>
        Task<Result<bool>> ClearFault(int pulseMs = 250, CancellationToken cancellationToken = default);
}
