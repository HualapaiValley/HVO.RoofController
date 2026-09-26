using System;

namespace HVO.RoofControllerV4.Common.Models;

public sealed record class RoofConfigurationResponse
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

    public int RestartOnFailureWaitTimeSeconds { get; init; }
}
