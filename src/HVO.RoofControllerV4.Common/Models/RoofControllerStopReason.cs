namespace HVO.RoofControllerV4.Common.Models
{
    /// <summary>
    /// Represents the reason why a roof controller operation was stopped.
    /// Used for logging and status tracking purposes.
    /// </summary>
    public enum RoofControllerStopReason
    {
        /// <summary>
        /// No specific reason provided, typically used for disposal or cleanup operations.
        /// </summary>
        None = 0,

        /// <summary>
        /// Normal stop command issued by user or system.
        /// </summary>
        NormalStop = 1,

        /// <summary>
        /// Operation stopped because a limit switch was reached.
        /// </summary>
        LimitSwitchReached = 2,

        /// <summary>
        /// Emergency stop triggered by safety systems.
        /// </summary>
        EmergencyStop = 3,

        /// <summary>
        /// Stop button was pressed by user.
        /// </summary>
        StopButtonPressed = 4,

        /// <summary>
        /// Safety watchdog timer expired, triggering automatic stop.
        /// </summary>
        SafetyWatchdogTimeout = 5,

        /// <summary>
        /// Operation stopped due to system disposal or shutdown.
        /// </summary>
        SystemDisposal = 6,

        /// <summary>
        /// Safety inputs (limits / fault) could not be read, or were stale, for longer than the configured tolerance.
        /// </summary>
        InputReadFailure = 7,

        /// <summary>
        /// Both limit switches reported active at the same time (wiring or switch failure).
        /// </summary>
        ContradictoryLimitInputs = 8,

        /// <summary>
        /// The limit the roof started from was released and then reasserted while moving away from it.
        /// </summary>
        StartLimitReasserted = 9,

        /// <summary>
        /// The drive fault input (IN3) reported an active fault.
        /// </summary>
        DriveFault = 10,

        /// <summary>
        /// A relay write could not be verified by register read-back.
        /// </summary>
        RelayVerificationFailed = 11,

        /// <summary>
        /// The optional renewable operator lease expired before it was renewed.
        /// </summary>
        OperatorLeaseExpired = 12,

        /// <summary>
        /// The drive did not report running (IN4) within the configured confirmation window, or stopped reporting it
        /// while moving without the destination limit.
        /// </summary>
        DriveNotRunning = 13,

        /// <summary>
        /// The host application is shutting down.
        /// </summary>
        HostShutdown = 14,

        /// <summary>
        /// Motion started on a limit and that limit did not release within the configured departure-release timeout.
        /// </summary>
        DepartureLimitNotReleased = 15
    }
}
