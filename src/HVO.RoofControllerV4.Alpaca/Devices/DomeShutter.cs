using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>The Alpaca <c>ShutterStatus</c> values (ASCOM <c>ShutterState</c>).</summary>
public enum DomeShutterState
{
    Open = 0,
    Closed = 1,
    Opening = 2,
    Closing = 3,
    Error = 4,
}

/// <summary>
/// How the roof's status reads as an Alpaca Dome's shutter: Open, Closed, Opening and Closing as they are, and every
/// other state Error (4), so a roof that is not moving is never reported as moving.
/// </summary>
public static class DomeShutter
{
    /// <summary>The shutter state for <paramref name="status"/>.</summary>
    public static DomeShutterState StateOf(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // A start that waited out the direction-change delay and was refused when its wait ended reads Error until the
        // next start, Stop or ClearFault (#220, N6), whatever the roof's position: the client sees its command failed.
        if (status.IsContinuationRefused && !status.IsMoving && !status.IsStartPending)
        {
            return DomeShutterState.Error;
        }

        return status.Status switch
        {
            RoofControllerStatus.Open => DomeShutterState.Open,
            RoofControllerStatus.Closed => DomeShutterState.Closed,
            RoofControllerStatus.Opening => DomeShutterState.Opening,
            RoofControllerStatus.Closing => DomeShutterState.Closing,
            // Error, Stopped, PartiallyOpen, PartiallyClose, NotInitialized, Unknown, and any state added later.
            _ => DomeShutterState.Error,
        };
    }

    /// <summary>
    /// Whether the roof is moving: the controller is driving it, a start it accepted waits out the direction-change delay
    /// (#220, N6: it reads Opening or Closing meanwhile), or the drive still reports running (IN4) after a stop while the
    /// roof coasts to a halt.
    /// </summary>
    public static bool IsSlewing(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsMoving || status.IsStartPending || status.CommandedMotion != RoofMotionDirection.None || status.IsAtSpeed;
    }
}
