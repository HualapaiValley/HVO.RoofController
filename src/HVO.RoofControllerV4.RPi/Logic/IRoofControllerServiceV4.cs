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
        /// Gets a value indicating whether the roof is currently moving (opening or closing).
        /// This property returns true when the roof is actively in motion and not at a limit switch position.
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
        /// Applies a configuration update to the controller service.
        /// </summary>
        /// <param name="updatedOptions">The validated configuration values to apply.</param>
        /// <returns>A result containing the effective configuration when successful.</returns>
        Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions);

        /// <summary>
        /// Applies a configuration update only if the current configuration version equals <paramref name="expectedVersion"/>.
        /// Fails with <see cref="RoofControllerErrorCode.ConfigurationVersionConflict"/> on mismatch, and with
        /// <see cref="RoofControllerErrorCode.ConfigurationRejected"/> when the options are unsafe for the current hardware mode.
        /// The update is transactional: on failure the previous options, timers and subscriptions remain in effect.
        /// </summary>
        Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions, long expectedVersion);

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
        /// pulse, and stops motion with <see cref="RoofControllerStopReason.HostShutdown"/>. The result fails with
        /// <see cref="RoofControllerErrorCode.RelayStateUnverified"/> when the de-energized state could not be verified.
        /// Safe to call more than once.
        /// </summary>
        Task<Result<RoofStatusResponse>> ShutdownAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Initializes the roof controller hardware and prepares it for operation.
        /// </summary>
        /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous initialization operation. The task result contains true if initialization succeeded; otherwise, false.</returns>
        Task<Result<bool>> Initialize(CancellationToken cancellationToken);

        /// <summary>
        /// Immediately stops all roof movement operations with a specified reason.
        /// </summary>
        /// <param name="reason">The reason for stopping the operation.</param>
        /// <returns>A result indicating whether the stop operation succeeded.</returns>
        Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop);

        /// <summary>
        /// Initiates the roof opening sequence.
        /// </summary>
        /// <returns>A result containing the updated roof controller status.</returns>
        Result<RoofControllerStatus> Open();

        /// <summary>
        /// Initiates the roof closing sequence.
        /// </summary>
        /// <returns>A result containing the updated roof controller status.</returns>
        Result<RoofControllerStatus> Close();

        /// <summary>
        /// Refresh internal cached status from hardware; when forceHardwareRead is true a direct I2C read is performed regardless of cached event values.
        /// </summary>
        /// <param name="forceHardwareRead">If true forces direct hardware read.</param>
        void RefreshStatus(bool forceHardwareRead = false);
        
        /// <summary>
        /// Pulses the clear-fault relay to reset fault conditions on the motor controller asynchronously.
        /// Releases internal lock during the delay period.
        /// </summary>
        /// <param name="pulseMs">Duration to hold the clear-fault relay active.</param>
        /// <param name="cancellationToken">Cancellation token to abort pulse wait.</param>
        /// <returns>A task result indicating whether the clear-fault pulse completed.</returns>
        Task<Result<bool>> ClearFault(int pulseMs = 250, CancellationToken cancellationToken = default);
 
        // DigitalInput1..4 events removed; use named alias events below

        // Public input-change events removed; the service exposes protected virtual hooks instead
 
}
