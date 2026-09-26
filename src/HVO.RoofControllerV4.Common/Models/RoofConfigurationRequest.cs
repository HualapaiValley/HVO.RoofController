using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Full replacement of the remotely editable controller configuration. Every safety-relevant field is required:
/// an omitted value is rejected rather than defaulted, so a partial body can never silently disable limits or faults.
/// </summary>
public sealed record class RoofConfigurationRequest : IValidatableObject
{
    /// <summary>
    /// The <see cref="RoofConfigurationResponse.Version"/> the caller last read. The update is rejected with
    /// <see cref="RoofControllerErrorCode.ConfigurationVersionConflict"/> when the configuration changed since.
    /// </summary>
    [Required]
    public long? ExpectedVersion { get; init; }

    /// <summary>
    /// Must be true when the request changes relay mapping, limit-switch polarity, fault polarity or
    /// <see cref="IgnorePhysicalLimitSwitches"/>. These are physical maintenance changes, not routine operation.
    /// </summary>
    public bool ConfirmSafetyCriticalChange { get; init; }

    [Required]
    public double? SafetyWatchdogTimeoutSeconds { get; init; }

    [Required]
    public int? OpenRelayId { get; init; }

    [Required]
    public int? CloseRelayId { get; init; }

    [Required]
    public int? ClearFaultRelayId { get; init; }

    [Required]
    public int? StopRelayId { get; init; }

    [Required]
    public bool? EnableDigitalInputPolling { get; init; }

    [Required]
    public double? DigitalInputPollIntervalMilliseconds { get; init; }

    [Required]
    public bool? EnablePeriodicVerificationWhileMoving { get; init; }

    [Required]
    public double? PeriodicVerificationIntervalSeconds { get; init; }

    [Required]
    public bool? UseNormallyClosedLimitSwitches { get; init; }

    [Required]
    public double? LimitSwitchDebounceMilliseconds { get; init; }

    [Required]
    public bool? IgnorePhysicalLimitSwitches { get; init; }

    [Required]
    public bool? FaultInputActiveHigh { get; init; }

    [Required]
    public int? MaxConsecutiveInputReadFailures { get; init; }

    /// <summary>Renewable operator lease in seconds; null disables the lease.</summary>
    public double? OperatorLeaseTimeoutSeconds { get; init; }

    /// <summary>At-speed (IN4) confirmation window in seconds; null disables the interlock.</summary>
    public double? AtSpeedConfirmationTimeoutSeconds { get; init; }

    /// <summary>
    /// Builds the options this request describes. Callers must have validated the request first.
    /// Local-only settings (such as <see cref="RoofControllerOptionsV4.AllowIgnoringLimitSwitchesOnPhysicalHardware"/>)
    /// are copied from <paramref name="current"/> and can never be changed remotely.
    /// </summary>
    public RoofControllerOptionsV4 ToOptions(RoofControllerOptionsV4 current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(SafetyWatchdogTimeoutSeconds!.Value),
            OpenRelayId = OpenRelayId!.Value,
            CloseRelayId = CloseRelayId!.Value,
            ClearFaultRelayId = ClearFaultRelayId!.Value,
            StopRelayId = StopRelayId!.Value,
            EnableDigitalInputPolling = EnableDigitalInputPolling!.Value,
            DigitalInputPollInterval = TimeSpan.FromMilliseconds(DigitalInputPollIntervalMilliseconds!.Value),
            EnablePeriodicVerificationWhileMoving = EnablePeriodicVerificationWhileMoving!.Value,
            PeriodicVerificationInterval = TimeSpan.FromSeconds(PeriodicVerificationIntervalSeconds!.Value),
            UseNormallyClosedLimitSwitches = UseNormallyClosedLimitSwitches!.Value,
            LimitSwitchDebounce = TimeSpan.FromMilliseconds(LimitSwitchDebounceMilliseconds!.Value),
            IgnorePhysicalLimitSwitches = IgnorePhysicalLimitSwitches!.Value,
            FaultInputActiveHigh = FaultInputActiveHigh!.Value,
            MaxConsecutiveInputReadFailures = MaxConsecutiveInputReadFailures!.Value,
            OperatorLeaseTimeout = OperatorLeaseTimeoutSeconds is { } lease ? TimeSpan.FromSeconds(lease) : null,
            AtSpeedConfirmationTimeout = AtSpeedConfirmationTimeoutSeconds is { } atSpeed ? TimeSpan.FromSeconds(atSpeed) : null
        };
    }

    /// <summary>
    /// True when applying this request to <paramref name="current"/> changes a physical-maintenance setting.
    /// </summary>
    public bool ChangesSafetyCriticalSettings(RoofControllerOptionsV4 current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return OpenRelayId != current.OpenRelayId
            || CloseRelayId != current.CloseRelayId
            || ClearFaultRelayId != current.ClearFaultRelayId
            || StopRelayId != current.StopRelayId
            || UseNormallyClosedLimitSwitches != current.UseNormallyClosedLimitSwitches
            || FaultInputActiveHigh != current.FaultInputActiveHigh
            || IgnorePhysicalLimitSwitches != current.IgnorePhysicalLimitSwitches;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Presence is enforced by [Required]; only range-check values that are present.
        if (SafetyWatchdogTimeoutSeconds is { } watchdog
            && (double.IsNaN(watchdog) || watchdog < RoofControllerLimits.MinSafetyWatchdogTimeoutSeconds || watchdog > RoofControllerLimits.MaxSafetyWatchdogTimeoutSeconds))
        {
            yield return new ValidationResult(
                $"Safety watchdog timeout must be between {RoofControllerLimits.MinSafetyWatchdogTimeoutSeconds} and {RoofControllerLimits.MaxSafetyWatchdogTimeoutSeconds} seconds.",
                [nameof(SafetyWatchdogTimeoutSeconds)]);
        }

        if (DigitalInputPollIntervalMilliseconds is { } poll
            && (double.IsNaN(poll) || poll < RoofControllerLimits.MinDigitalInputPollIntervalMilliseconds || poll > RoofControllerLimits.MaxDigitalInputPollIntervalMilliseconds))
        {
            yield return new ValidationResult(
                $"Digital input poll interval must be between {RoofControllerLimits.MinDigitalInputPollIntervalMilliseconds} and {RoofControllerLimits.MaxDigitalInputPollIntervalMilliseconds} ms.",
                [nameof(DigitalInputPollIntervalMilliseconds)]);
        }

        if (PeriodicVerificationIntervalSeconds is { } periodic
            && (double.IsNaN(periodic) || periodic * 1000 < RoofControllerLimits.MinPeriodicVerificationIntervalMilliseconds || periodic * 1000 > RoofControllerLimits.MaxPeriodicVerificationIntervalMilliseconds))
        {
            yield return new ValidationResult(
                $"Periodic verification interval must be between {RoofControllerLimits.MinPeriodicVerificationIntervalMilliseconds / 1000} and {RoofControllerLimits.MaxPeriodicVerificationIntervalMilliseconds / 1000} seconds.",
                [nameof(PeriodicVerificationIntervalSeconds)]);
        }

        if (LimitSwitchDebounceMilliseconds is { } debounce
            && (double.IsNaN(debounce) || debounce < 0 || debounce > RoofControllerLimits.MaxLimitSwitchDebounceMilliseconds))
        {
            yield return new ValidationResult(
                $"Limit switch debounce must be between 0 and {RoofControllerLimits.MaxLimitSwitchDebounceMilliseconds} ms.",
                [nameof(LimitSwitchDebounceMilliseconds)]);
        }

        if (EnableDigitalInputPolling == false && EnablePeriodicVerificationWhileMoving == false)
        {
            yield return new ValidationResult(
                "At least one of digital input polling or periodic verification while moving must be enabled; otherwise limits and faults are never supervised in software.",
                [nameof(EnableDigitalInputPolling), nameof(EnablePeriodicVerificationWhileMoving)]);
        }

        if (MaxConsecutiveInputReadFailures is { } failures
            && (failures < RoofControllerLimits.MinConsecutiveInputReadFailures || failures > RoofControllerLimits.MaxConsecutiveInputReadFailures))
        {
            yield return new ValidationResult(
                $"Max consecutive input read failures must be between {RoofControllerLimits.MinConsecutiveInputReadFailures} and {RoofControllerLimits.MaxConsecutiveInputReadFailures}.",
                [nameof(MaxConsecutiveInputReadFailures)]);
        }

        if (OperatorLeaseTimeoutSeconds is { } lease
            && (double.IsNaN(lease) || lease < RoofControllerLimits.MinOperatorLeaseSeconds || lease > RoofControllerLimits.MaxOperatorLeaseSeconds))
        {
            yield return new ValidationResult(
                $"Operator lease must be between {RoofControllerLimits.MinOperatorLeaseSeconds} and {RoofControllerLimits.MaxOperatorLeaseSeconds} seconds when set.",
                [nameof(OperatorLeaseTimeoutSeconds)]);
        }

        if (AtSpeedConfirmationTimeoutSeconds is { } atSpeed
            && (double.IsNaN(atSpeed) || atSpeed < RoofControllerLimits.MinAtSpeedConfirmationSeconds || atSpeed > RoofControllerLimits.MaxAtSpeedConfirmationSeconds))
        {
            yield return new ValidationResult(
                $"At-speed confirmation must be between {RoofControllerLimits.MinAtSpeedConfirmationSeconds} and {RoofControllerLimits.MaxAtSpeedConfirmationSeconds} seconds when set.",
                [nameof(AtSpeedConfirmationTimeoutSeconds)]);
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

        var relayIds = GetRelayMappings().Where(mapping => mapping.relayId.HasValue).Select(mapping => mapping.relayId).ToArray();
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

    private IEnumerable<(int? relayId, string propertyName)> GetRelayMappings()
    {
        yield return (OpenRelayId, nameof(OpenRelayId));
        yield return (CloseRelayId, nameof(CloseRelayId));
        yield return (ClearFaultRelayId, nameof(ClearFaultRelayId));
        yield return (StopRelayId, nameof(StopRelayId));
    }
}
