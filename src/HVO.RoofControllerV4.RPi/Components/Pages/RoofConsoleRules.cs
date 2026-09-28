using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>A safety notification derived from two consecutive status snapshots.</summary>
internal sealed record RoofSafetyAlert(string Title, string Message);

/// <summary>What the console knows about the most recent Stop request.</summary>
public enum RoofStopOutcome
{
    None,
    Sent,
    Acknowledged,
    RelayUnverified,
    Failed
}

/// <summary>
/// Pure presentation rules for the operator console: snapshot ordering, safety-alert detection, wording for stop
/// reasons and error codes, and lease-renewal timing. Kept free of component state so it can be unit tested.
/// </summary>
internal static class RoofConsoleRules
{
    internal static readonly TimeSpan MinimumLeaseRenewalDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaximumLeaseRenewalDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when <paramref name="candidate"/> may replace <paramref name="current"/>. Older versions are discarded;
    /// equal versions are accepted because controllers that predate versioning report 0 for every snapshot. A new
    /// controller instance restarts its sequence, so a change of instance id is always accepted.
    /// </summary>
    internal static bool ShouldApply(RoofStatusResponse? current, RoofStatusResponse candidate)
    {
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
    internal static RoofSafetyAlert? DetectSafetyAlert(RoofStatusResponse? previous, RoofStatusResponse next)
    {
        if (previous is null)
        {
            return null;
        }

        if (next.IsFaultLatched
            && (!previous.IsFaultLatched || previous.LatchedFaultReason != next.LatchedFaultReason))
        {
            var reason = next.LatchedFaultReason ?? next.LastStopReason;
            return new RoofSafetyAlert("Fault latched", $"{DescribeStopReason(reason)}. Motion is blocked until the fault is cleared.");
        }

        var stoppedBySafety = IsSafetyStopReason(next.LastStopReason)
            && (next.LastStopReason != previous.LastStopReason || (previous.IsMoving && !next.IsMoving));
        if (stoppedBySafety)
        {
            return new RoofSafetyAlert("Safety stop", DescribeStopReason(next.LastStopReason));
        }

        if (!next.IsFaultLatched && next.Status == RoofControllerStatus.Error && previous.Status != RoofControllerStatus.Error)
        {
            return new RoofSafetyAlert("Controller error", string.IsNullOrWhiteSpace(next.LastError)
                ? "The controller reported an error state."
                : next.LastError!);
        }

        return null;
    }

    internal static bool IsSafetyStopReason(RoofControllerStopReason reason) => reason switch
    {
        RoofControllerStopReason.EmergencyStop => true,
        RoofControllerStopReason.SafetyWatchdogTimeout => true,
        RoofControllerStopReason.InputReadFailure => true,
        RoofControllerStopReason.ContradictoryLimitInputs => true,
        RoofControllerStopReason.StartLimitReasserted => true,
        RoofControllerStopReason.DriveFault => true,
        RoofControllerStopReason.RelayVerificationFailed => true,
        RoofControllerStopReason.OperatorLeaseExpired => true,
        RoofControllerStopReason.DriveNotRunning => true,
        RoofControllerStopReason.DepartureLimitNotReleased => true,
        _ => false
    };

    internal static string DescribeStopReason(RoofControllerStopReason reason) => reason switch
    {
        RoofControllerStopReason.None => "No stop recorded",
        RoofControllerStopReason.NormalStop => "Stopped by operator",
        RoofControllerStopReason.LimitSwitchReached => "Limit switch reached",
        RoofControllerStopReason.EmergencyStop => "Emergency stop",
        RoofControllerStopReason.StopButtonPressed => "Stop button pressed",
        RoofControllerStopReason.SafetyWatchdogTimeout => "Safety watchdog timed out",
        RoofControllerStopReason.SystemDisposal => "Controller shut down",
        RoofControllerStopReason.InputReadFailure => "Safety inputs could not be read",
        RoofControllerStopReason.ContradictoryLimitInputs => "Open and closed limits both active",
        RoofControllerStopReason.StartLimitReasserted => "Starting limit reasserted after release",
        RoofControllerStopReason.DriveFault => "Drive fault input active",
        RoofControllerStopReason.RelayVerificationFailed => "Relay register verification failed",
        RoofControllerStopReason.OperatorLeaseExpired => "Operator lease expired",
        RoofControllerStopReason.DriveNotRunning => "Drive not reporting running (IN4)",
        RoofControllerStopReason.HostShutdown => "Host shutting down",
        RoofControllerStopReason.DepartureLimitNotReleased => "Starting limit did not release",
        _ => reason.ToString()
    };

    internal static string DescribeErrorCode(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.NotInitialized => "The controller has not finished initializing.",
        RoofControllerErrorCode.ShuttingDown => "The controller is shutting down and accepts no new motion.",
        RoofControllerErrorCode.HardwareUnavailable => "The relay hardware is unavailable.",
        RoofControllerErrorCode.RelayStateUnverified => "The relay register could not be verified. Confirm at the roof that the motor has stopped.",
        RoofControllerErrorCode.FaultLatched => "A fault is latched. Resolve the cause, then clear the fault.",
        RoofControllerErrorCode.InterlockActive => "A safety interlock refused the command.",
        RoofControllerErrorCode.OperationInProgress => "Another operation is in progress.",
        RoofControllerErrorCode.LeaseNotActive => "No leased motion is active.",
        RoofControllerErrorCode.ConfigurationVersionConflict => "The configuration changed since it was read.",
        RoofControllerErrorCode.ConfigurationRejected => "The configuration was rejected as unsafe.",
        RoofControllerErrorCode.InvalidRequest => "The request was invalid.",
        _ => "The controller reported an unexpected error."
    };

    /// <summary>Operator-facing text for a failed command. Messages of unexpected exceptions are not shown.</summary>
    internal static string DescribeFailure(Exception? error)
    {
        if (error is RoofControllerException roofError)
        {
            return $"{DescribeErrorCode(roofError.Code)} [{roofError.Code}]";
        }

        return $"{DescribeErrorCode(RoofControllerErrorCode.Unknown)} See the server log for details.";
    }

    internal static RoofControllerErrorCode? GetErrorCode(Exception? error) => (error as RoofControllerException)?.Code;

    /// <summary>
    /// Classifies a completed Stop request. A stop that the controller accepted but whose relay register could not be read
    /// back is reported separately so the operator knows to confirm at the roof.
    /// </summary>
    internal static (RoofStopOutcome Outcome, string Message) ClassifyStop(bool succeeded, Exception? error, RoofStatusResponse? snapshot)
    {
        if (succeeded)
        {
            return snapshot?.RelayRegisterState switch
            {
                RoofRelayRegisterState.Unverified => (RoofStopOutcome.RelayUnverified,
                    "Stop acknowledged, but the relay register could not be verified. Confirm at the roof that the motor has stopped."),
                RoofRelayRegisterState.Verified => (RoofStopOutcome.Acknowledged, "Stop acknowledged. Relay register verified de-energized."),
                _ => (RoofStopOutcome.Acknowledged, "Stop acknowledged by the controller.")
            };
        }

        if (GetErrorCode(error) == RoofControllerErrorCode.RelayStateUnverified)
        {
            return (RoofStopOutcome.RelayUnverified,
                $"Stop sent, but the relay register could not be verified. Confirm at the roof that the motor has stopped. [{RoofControllerErrorCode.RelayStateUnverified}]");
        }

        return (RoofStopOutcome.Failed, $"Stop failed: {DescribeFailure(error)}");
    }

    /// <summary>
    /// The commanded direction. Controllers that predate <see cref="RoofStatusResponse.CommandedMotion"/> leave it at
    /// None while moving, so the moving status is used instead.
    /// </summary>
    internal static RoofMotionDirection GetCommandedMotion(RoofStatusResponse status)
    {
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

    internal static string DescribePosition(RoofControllerStatus status) => status switch
    {
        RoofControllerStatus.Open => "Open",
        RoofControllerStatus.Closed => "Closed",
        RoofControllerStatus.Opening => "Opening",
        RoofControllerStatus.Closing => "Closing",
        RoofControllerStatus.Stopped => "Stopped",
        RoofControllerStatus.PartiallyOpen => "Partially open",
        RoofControllerStatus.PartiallyClose => "Partially closed",
        RoofControllerStatus.Error => "Error",
        RoofControllerStatus.NotInitialized => "Not initialized",
        _ => "Unknown"
    };

    /// <summary>
    /// Delay before renewing an operator lease: a third of the remaining time, bounded so a short lease is still renewed
    /// promptly and a long one is not left unrenewed for too long. Null when no lease applies.
    /// </summary>
    internal static TimeSpan? GetLeaseRenewalDelay(double? leaseSecondsRemaining)
    {
        if (leaseSecondsRemaining is not { } remaining || double.IsNaN(remaining) || remaining <= 0)
        {
            return null;
        }

        var delay = TimeSpan.FromSeconds(remaining / 3);
        if (delay < MinimumLeaseRenewalDelay)
        {
            return MinimumLeaseRenewalDelay;
        }

        return delay > MaximumLeaseRenewalDelay ? MaximumLeaseRenewalDelay : delay;
    }

    /// <summary>
    /// Maps an in-process health report to the same shape the <c>/health</c> endpoint serializes. Exception messages are
    /// only included for administrators.
    /// </summary>
    internal static HealthReportPayload ToPayload(HealthReport report, bool includeExceptions)
    {
        return new HealthReportPayload
        {
            Status = report.Status.ToString(),
            TotalDuration = report.TotalDuration.ToString(),
            Checks = report.Entries.Select(entry => new HealthCheckEntry
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                Data = SerializeData(entry.Value.Data),
                Duration = entry.Value.Duration.ToString(),
                Exception = entry.Value.Exception is null
                    ? null
                    : includeExceptions ? entry.Value.Exception.Message : "An exception was recorded. Administrators can view the details.",
                Tags = entry.Value.Tags.ToArray()
            }).ToList()
        };
    }

    private static JsonElement? SerializeData(IReadOnlyDictionary<string, object> data)
    {
        if (data.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(data);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToElement(data.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString()));
        }
    }
}
