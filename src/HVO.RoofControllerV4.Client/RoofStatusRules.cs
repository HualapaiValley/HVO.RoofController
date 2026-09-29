using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>A safety notification derived from two consecutive status snapshots.</summary>
public sealed record RoofSafetyAlert(string Title, string Message);

/// <summary>
/// Client-side rules for status snapshots: ordering, safety-alert detection, the commanded direction and lease-renewal
/// timing. Pure functions, so every client applies them the same way.
/// </summary>
public static class RoofStatusRules
{
    public static readonly TimeSpan MinimumLeaseRenewalDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaximumLeaseRenewalDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when <paramref name="candidate"/> may replace <paramref name="current"/>. Older versions are discarded;
    /// equal versions are accepted because controllers that predate versioning report 0 for every snapshot. A new
    /// controller instance restarts its sequence, so a change of instance id is always accepted.
    /// </summary>
    public static bool ShouldApply(RoofStatusResponse? current, RoofStatusResponse candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (current is null)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(candidate.ControllerInstanceId)
            && !string.Equals(candidate.ControllerInstanceId, current.ControllerInstanceId, StringComparison.Ordinal))
        {
            return true;
        }

        return candidate.StatusVersion >= current.StatusVersion;
    }

    /// <summary>
    /// Returns an alert when <paramref name="next"/> enters a safety state that <paramref name="previous"/> was not in:
    /// a newly latched fault (or a new latch reason), a safety stop reason, or the Error status on controllers that do
    /// not report latching. Nothing is raised for the first snapshot, for repeats of the same state, or when a fault
    /// clears.
    /// </summary>
    public static RoofSafetyAlert? DetectSafetyAlert(RoofStatusResponse? previous, RoofStatusResponse next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (previous is null)
        {
            return null;
        }

        if (next.IsFaultLatched
            && (!previous.IsFaultLatched || previous.LatchedFaultReason != next.LatchedFaultReason))
        {
            var reason = next.LatchedFaultReason ?? next.LastStopReason;
            return new RoofSafetyAlert("Fault latched", $"{RoofText.DescribeStopReason(reason)}. Motion is blocked until the fault is cleared.");
        }

        var stoppedBySafety = RoofText.IsSafetyStopReason(next.LastStopReason)
            && (next.LastStopReason != previous.LastStopReason || (previous.IsMoving && !next.IsMoving));
        if (stoppedBySafety)
        {
            return new RoofSafetyAlert("Safety stop", RoofText.DescribeStopReason(next.LastStopReason));
        }

        if (!next.IsFaultLatched && next.Status == RoofControllerStatus.Error && previous.Status != RoofControllerStatus.Error)
        {
            return new RoofSafetyAlert("Controller error", string.IsNullOrWhiteSpace(next.LastError)
                ? "The controller reported an error state."
                : next.LastError!);
        }

        return null;
    }

    /// <summary>
    /// The commanded direction. Controllers that predate <see cref="RoofStatusResponse.CommandedMotion"/> leave it at
    /// None while moving, so the moving status is used instead.
    /// </summary>
    public static RoofMotionDirection GetCommandedMotion(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.CommandedMotion != RoofMotionDirection.None || !status.IsMoving)
        {
            return status.CommandedMotion;
        }

        return status.Status switch
        {
            RoofControllerStatus.Opening => RoofMotionDirection.Opening,
            RoofControllerStatus.Closing => RoofMotionDirection.Closing,
            _ => RoofMotionDirection.None
        };
    }

    /// <summary>
    /// Delay before renewing an operator lease: a third of the remaining time, bounded so a short lease is still renewed
    /// promptly and a long one is not left unrenewed for too long. Null when no lease applies.
    /// </summary>
    public static TimeSpan? GetLeaseRenewalDelay(double? leaseSecondsRemaining)
    {
        if (leaseSecondsRemaining is not { } remaining || double.IsNaN(remaining) || remaining <= 0)
        {
            return null;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(remaining, TimeSpan.MaxValue.TotalSeconds) / 3);
        if (delay < MinimumLeaseRenewalDelay)
        {
            return MinimumLeaseRenewalDelay;
        }

        return delay > MaximumLeaseRenewalDelay ? MaximumLeaseRenewalDelay : delay;
    }
}
