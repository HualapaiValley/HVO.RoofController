using System;

namespace HVO.RoofControllerV4.Common.Models
{
    /// <summary>
    /// Event args carrying a snapshot of the roof status and telemetry.
    /// </summary>
    public sealed class RoofStatusChangedEventArgs : EventArgs
    {
        public RoofStatusResponse Status { get; }

        public RoofStatusChangedEventArgs(RoofStatusResponse status)
        {
            Status = status;
        }
    }
}
