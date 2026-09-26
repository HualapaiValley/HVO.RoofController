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
        SystemDisposal = 6
    }
}
