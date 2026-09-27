namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Bounds shared by the server-side validator, the API request model and the clients.
/// </summary>
public static class RoofControllerLimits
{
    /// <summary>Minimum safety watchdog (absolute movement cap), in seconds.</summary>
    public const double MinSafetyWatchdogTimeoutSeconds = 5;

    /// <summary>Maximum safety watchdog (absolute movement cap), in seconds.</summary>
    public const double MaxSafetyWatchdogTimeoutSeconds = 600;

    /// <summary>Minimum digital input poll interval, in milliseconds.</summary>
    public const double MinDigitalInputPollIntervalMilliseconds = 5;

    /// <summary>Maximum digital input poll interval, in milliseconds.</summary>
    public const double MaxDigitalInputPollIntervalMilliseconds = 1000;

    /// <summary>Minimum periodic verification interval, in milliseconds.</summary>
    public const double MinPeriodicVerificationIntervalMilliseconds = 50;

    /// <summary>Maximum periodic verification interval, in milliseconds.</summary>
    public const double MaxPeriodicVerificationIntervalMilliseconds = 5000;

    /// <summary>Maximum limit switch debounce, in milliseconds.</summary>
    public const double MaxLimitSwitchDebounceMilliseconds = 500;

    /// <summary>Minimum clear-fault pulse, in milliseconds.</summary>
    public const int MinClearFaultPulseMilliseconds = 50;

    /// <summary>Maximum clear-fault pulse, in milliseconds.</summary>
    public const int MaxClearFaultPulseMilliseconds = 2000;

    /// <summary>Default clear-fault pulse, in milliseconds.</summary>
    public const int DefaultClearFaultPulseMilliseconds = 250;

    /// <summary>Minimum consecutive in-motion input read failures tolerated before a safety stop.</summary>
    public const int MinConsecutiveInputReadFailures = 1;

    /// <summary>Maximum consecutive in-motion input read failures tolerated before a safety stop.</summary>
    public const int MaxConsecutiveInputReadFailures = 10;

    /// <summary>Minimum operator lease, in seconds, when the lease is enabled.</summary>
    public const double MinOperatorLeaseSeconds = 2;

    /// <summary>Maximum operator lease, in seconds, when the lease is enabled.</summary>
    public const double MaxOperatorLeaseSeconds = 120;

    /// <summary>Minimum at-speed (IN4) confirmation window, in seconds, when enabled.</summary>
    public const double MinAtSpeedConfirmationSeconds = 0.5;

    /// <summary>Maximum at-speed (IN4) confirmation window, in seconds, when enabled.</summary>
    public const double MaxAtSpeedConfirmationSeconds = 30;

    /// <summary>Minimum drive stop (IN4 low after stop) confirmation window, in seconds, when set.</summary>
    public const double MinDriveStopConfirmationSeconds = 0.5;

    /// <summary>Maximum drive stop (IN4 low after stop) confirmation window, in seconds, when set.</summary>
    public const double MaxDriveStopConfirmationSeconds = 60;

    /// <summary>Minimum departure-release timeout, in seconds, when enabled.</summary>
    public const double MinDepartureReleaseSeconds = 0.5;

    /// <summary>Maximum departure-release timeout, in seconds, when enabled.</summary>
    public const double MaxDepartureReleaseSeconds = 60;
}
