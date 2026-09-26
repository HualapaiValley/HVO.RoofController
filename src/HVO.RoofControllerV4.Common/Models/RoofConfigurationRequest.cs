using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace HVO.RoofControllerV4.Common.Models;

public sealed record class RoofConfigurationRequest : IValidatableObject
{
    public double SafetyWatchdogTimeoutSeconds { get; init; }

    public int OpenRelayId { get; init; }

    public int CloseRelayId { get; init; }

    public int ClearFaultRelayId { get; init; }

    public int StopRelayId { get; init; }

    public bool EnableDigitalInputPolling { get; init; }

    public double DigitalInputPollIntervalMilliseconds { get; init; }

    public bool EnablePeriodicVerificationWhileMoving { get; init; }

    public double PeriodicVerificationIntervalSeconds { get; init; }

    public bool UseNormallyClosedLimitSwitches { get; init; }

    public double LimitSwitchDebounceMilliseconds { get; init; }

    public bool IgnorePhysicalLimitSwitches { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SafetyWatchdogTimeoutSeconds <= 0)
        {
            yield return new ValidationResult(
                "Safety watchdog timeout must be greater than zero.",
                [nameof(SafetyWatchdogTimeoutSeconds)]);
        }

        if (DigitalInputPollIntervalMilliseconds <= 0)
        {
            yield return new ValidationResult(
                "Digital input poll interval must be greater than zero.",
                [nameof(DigitalInputPollIntervalMilliseconds)]);
        }

        if (PeriodicVerificationIntervalSeconds <= 0)
        {
            yield return new ValidationResult(
                "Periodic verification interval must be greater than zero.",
                [nameof(PeriodicVerificationIntervalSeconds)]);
        }

        if (!EnableDigitalInputPolling && EnablePeriodicVerificationWhileMoving)
        {
            yield return new ValidationResult(
                "Periodic verification requires digital input polling to be enabled.",
                [nameof(EnablePeriodicVerificationWhileMoving)]);
        }

        foreach (var (relayId, propertyName) in GetRelayMappings())
        {
            if (relayId is < 1 or > 4)
            {
                yield return new ValidationResult(
                    "Relay identifiers must be between 1 and 4.",
                    [propertyName]);
            }
        }

        var relayIds = GetRelayMappings().Select(mapping => mapping.relayId).ToArray();
        if (relayIds.Distinct().Count() != relayIds.Length)
        {
            yield return new ValidationResult(
                "Relay identifiers (Open, Close, ClearFault, Stop) must be unique.",
                new[]
                {
                    nameof(OpenRelayId),
                    nameof(CloseRelayId),
                    nameof(ClearFaultRelayId),
                    nameof(StopRelayId)
                });
        }
    }

    private IEnumerable<(int relayId, string propertyName)> GetRelayMappings()
    {
        yield return (OpenRelayId, nameof(OpenRelayId));
        yield return (CloseRelayId, nameof(CloseRelayId));
        yield return (ClearFaultRelayId, nameof(ClearFaultRelayId));
        yield return (StopRelayId, nameof(StopRelayId));
    }
}
