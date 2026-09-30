using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's own wording. The roof, Stop and the settings use the wording every client shares; the words that differ in
/// the Mac app are <see cref="KioskWording"/>'s.
/// </summary>
public static class KioskText
{
    /// <summary>A block reason before the console started.</summary>
    public const string Starting = "the kiosk is starting";

    /// <summary>A block reason while the kiosk is locked.</summary>
    public const string Locked = "the kiosk is locked: unlock it with a PIN";

    public const string Unreachable = "The controller cannot be reached.";

    public const string StopStillTried = "Stop is still tried; its answer shows whether it arrived.";

    public const string NoStatusYet = "No status from the controller yet.";

    public const string LockedNotice = "Locked.";

    public const string LeaseDroppedOnLock =
        "The kiosk was locked, so it stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.";

    public const string SessionEnded = "The controller ended the PIN session, so the kiosk is locked.";

    public const string SessionEndedWithLease =
        "The controller ended the PIN session, so the kiosk is locked and stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.";

    /// <summary>"Operator" for <see cref="RoofControllerApiContract.OperatorRole"/>: a role as a person reads it.</summary>
    public static string DescribeRole(string? role) => role switch
    {
        RoofControllerApiContract.AdminRole => "Admin",
        RoofControllerApiContract.OperatorRole => "Operator",
        RoofControllerApiContract.ViewerRole => "Viewer",
        null or "" => "none",
        _ => role
    };

    /// <summary>"2 min", "90 s" or "1 h": a whole number of the largest unit that fits.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value >= TimeSpan.FromHours(1) && value.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return $"{(value.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture)} h";
        }

        if (value >= TimeSpan.FromMinutes(1) && value.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return $"{(value.Ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture)} min";
        }

        return RoofStatusText.Seconds(value.TotalSeconds);
    }

    /// <summary>
    /// The banner for a controller that cannot be reached: since when the status shown is only the last known state
    /// (<paramref name="staleSince"/>), or null when there is none yet.
    /// </summary>
    public static string DescribeUnreachable(DateTimeOffset? staleSince)
        => staleSince is { } since
            ? $"{Unreachable} No status since {RoofStatusText.Time(since)}: showing the last known state. {StopStillTried}"
            : $"{Unreachable} There is no status yet. {StopStillTried}";
}
