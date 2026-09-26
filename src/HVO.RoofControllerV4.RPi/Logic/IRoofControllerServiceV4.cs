using System;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

public interface IRoofControllerServiceV4
{
        /// <summary>
        /// Event raised when status or watchdog telemetry changes.
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
        /// Returns a current snapshot of status for UI/API consumption.
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
        /// True if the underlying service has been disposed (not available for use).
        /// </summary>
        bool IsServiceDisposed { get; }

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
