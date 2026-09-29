using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Presentation rules for the operator console. The rules and wording shared by every client (snapshot ordering, safety
/// alerts, stop reasons, error codes, lease timing and Stop results) live in <see cref="RoofStatusRules"/>,
/// <see cref="RoofText"/> and <see cref="RoofStopText"/>; this class applies them to the in-process controller.
/// </summary>
internal static class RoofConsoleRules
{
    internal static readonly TimeSpan MinimumLeaseRenewalDelay = RoofStatusRules.MinimumLeaseRenewalDelay;
    internal static readonly TimeSpan MaximumLeaseRenewalDelay = RoofStatusRules.MaximumLeaseRenewalDelay;

    /// <inheritdoc cref="RoofStatusRules.ShouldApply"/>
    internal static bool ShouldApply(RoofStatusResponse? current, RoofStatusResponse candidate) => RoofStatusRules.ShouldApply(current, candidate);

    /// <inheritdoc cref="RoofStatusRules.DetectSafetyAlert"/>
    internal static RoofSafetyAlert? DetectSafetyAlert(RoofStatusResponse? previous, RoofStatusResponse next) => RoofStatusRules.DetectSafetyAlert(previous, next);

    internal static bool IsSafetyStopReason(RoofControllerStopReason reason) => RoofText.IsSafetyStopReason(reason);

    internal static string DescribeStopReason(RoofControllerStopReason reason) => RoofText.DescribeStopReason(reason);

    internal static string DescribeErrorCode(RoofControllerErrorCode code) => RoofText.DescribeErrorCode(code);

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
        => RoofStopText.Classify(succeeded, snapshot, GetErrorCode(error), DescribeFailure(error));

    /// <inheritdoc cref="RoofStatusRules.GetCommandedMotion"/>
    internal static RoofMotionDirection GetCommandedMotion(RoofStatusResponse status) => RoofStatusRules.GetCommandedMotion(status);

    internal static string DescribePosition(RoofControllerStatus status) => RoofText.DescribePosition(status);

    /// <inheritdoc cref="RoofStatusRules.GetLeaseRenewalDelay"/>
    internal static TimeSpan? GetLeaseRenewalDelay(double? leaseSecondsRemaining) => RoofStatusRules.GetLeaseRenewalDelay(leaseSecondsRemaining);

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
