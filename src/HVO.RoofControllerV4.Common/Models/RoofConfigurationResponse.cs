using System;

namespace HVO.RoofControllerV4.Common.Models;

public sealed record class RoofConfigurationResponse
{
    /// <summary>
    /// Configuration version. Send it back as <see cref="RoofConfigurationRequest.ExpectedVersion"/> when updating.
    /// </summary>
    public long Version { get; init; }

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

    public bool FaultInputActiveHigh { get; init; }

    public int MaxConsecutiveInputReadFailures { get; init; }

    public double? OperatorLeaseTimeoutSeconds { get; init; }

    public double? AtSpeedConfirmationTimeoutSeconds { get; init; }

    /// <summary>Local-only safeguard; reported for visibility, never changed through the API.</summary>
    public bool AllowIgnoringLimitSwitchesOnPhysicalHardware { get; init; }

    public int RestartOnFailureWaitTimeSeconds { get; init; }
}
