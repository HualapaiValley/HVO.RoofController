using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>How Open or Close is drawn (<see cref="RoofUiPalette"/>'s motion state members).</summary>
public enum RoofActionState
{
    /// <summary>Offered: the shared command rules allow it now.</summary>
    Available,

    /// <summary>A live status shows the roof moving in this button's direction.</summary>
    Running,

    /// <summary>Not offered now (a limit, a fault, a stale status, the role, a command on its way); the reason is shown.</summary>
    Disabled
}

/// <summary>
/// The states every client draws Stop, Open and Close in, so the kiosk, the Mac app, the web UI and the terminal UI agree.
/// Colour only: Stop is never disabled in any state, and whether Open or Close may be pressed stays
/// <see cref="RoofCommandRules"/>' decision (and the controller's). Each state also has words beside it: the position,
/// the commanded motion and the reason a command is not offered.
/// </summary>
public static class RoofActionStates
{
    /// <summary>
    /// True when Stop is drawn bright (<see cref="RoofUiPalette.StopButtonActive"/>): the newest status shows the roof
    /// moving or commanded to move. A stale status that last showed motion keeps Stop bright, since the roof may still be
    /// moving; with no status at all Stop is drawn idle, and acts the same.
    /// </summary>
    public static bool IsStopActive(RoofStatusResponse? status) => status is not null && Moving(status) != RoofMotionDirection.None;

    /// <summary>
    /// Open's (<see cref="RoofMotionDirection.Opening"/>) or Close's state: <see cref="RoofActionState.Running"/> while a
    /// live status shows the roof moving that way; otherwise <see cref="RoofActionState.Available"/> when
    /// <paramref name="blockReason"/> (from <see cref="RoofCommandRules"/>) is null, and
    /// <see cref="RoofActionState.Disabled"/> when it is not. A stale status is never shown as running.
    /// </summary>
    public static RoofActionState ForMotion(RoofMotionDirection direction, RoofStatusResponse? status, bool isStale, string? blockReason)
    {
        if (direction is not (RoofMotionDirection.Opening or RoofMotionDirection.Closing))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Only Opening or Closing is a motion button.");
        }

        return !isStale && status is not null && Moving(status) == direction ? RoofActionState.Running
            : blockReason is null ? RoofActionState.Available
            : RoofActionState.Disabled;
    }

    // The commanded motion, or else the direction the position says the roof is travelling in.
    private static RoofMotionDirection Moving(RoofStatusResponse status) => status.CommandedMotion switch
    {
        RoofMotionDirection.Opening or RoofMotionDirection.Closing => status.CommandedMotion,
        _ => status.Status switch
        {
            RoofControllerStatus.Opening => RoofMotionDirection.Opening,
            RoofControllerStatus.Closing => RoofMotionDirection.Closing,
            _ => RoofMotionDirection.None
        }
    };
}
