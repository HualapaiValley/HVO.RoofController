namespace HVO.RoofControllerV4.Common.Models
{
    /// <summary>
    /// Represents the current operational status of the roof controller system.
    /// </summary>
    public enum RoofControllerStatus
    {
        /// <summary>
        /// The roof controller status is unknown or has not been determined.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// The roof controller has not been initialized and is not ready for operation.
        /// </summary>
        NotInitialized = 1,

        /// <summary>
        /// The roof is fully closed and secured.
        /// </summary>
        Closed = 2,

        /// <summary>
        /// The roof is currently in the process of closing.
        /// </summary>
        Closing = 3,

        /// <summary>
        /// The roof is fully open and ready for observation.
        /// </summary>
        Open = 4,

        /// <summary>
        /// The roof is currently in the process of opening.
        /// </summary>
        Opening = 5,

        /// <summary>
        /// All roof movement has been stopped, either manually or automatically.
        /// </summary>
        Stopped = 6,

        /// <summary>
        /// The roof is partially open (stopped between fully closed and fully open positions).
        /// This occurs when an opening operation is interrupted before reaching the open limit switch.
        /// </summary>
        PartiallyOpen = 7,

        /// <summary>
        /// The roof is partially closed (stopped between fully open and fully closed positions).
        /// This occurs when a closing operation is interrupted before reaching the closed limit switch.
        /// </summary>
        PartiallyClose = 8,

        /// <summary>
        /// An error condition has been detected in the roof controller system.
        /// </summary>
        Error = 99
    }
}