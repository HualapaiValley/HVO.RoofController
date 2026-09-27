using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Common.Models;

public sealed class RoofControllerOptionsV4Validator : IValidateOptions<RoofControllerOptionsV4>
{
    public ValidateOptionsResult Validate(string? name, RoofControllerOptionsV4 options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options instance is null");
        }

        List<string>? failures = null;

        void AddFailure(string message)
        {
            failures ??= new List<string>();
            failures.Add(message);
        }

        static bool IsRelayIdValid(int relayId) => relayId is >= 1 and <= 4;

        if (!IsRelayIdValid(options.OpenRelayId))
        {
            AddFailure("OpenRelayId must be between 1 and 4 (inclusive).");
        }

        if (!IsRelayIdValid(options.CloseRelayId))
        {
            AddFailure("CloseRelayId must be between 1 and 4 (inclusive).");
        }

        if (!IsRelayIdValid(options.ClearFaultRelayId))
        {
            AddFailure("ClearFaultRelayId must be between 1 and 4 (inclusive).");
        }

        if (!IsRelayIdValid(options.StopRelayId))
        {
            AddFailure("StopRelayId must be between 1 and 4 (inclusive).");
        }

        var relayIds = new HashSet<int> { options.OpenRelayId, options.CloseRelayId, options.ClearFaultRelayId, options.StopRelayId };
        if (relayIds.Count != 4)
        {
            AddFailure("Relay identifiers (Open, Close, ClearFault, Stop) must be unique.");
        }

        static bool InRange(TimeSpan value, double minMilliseconds, double maxMilliseconds)
            => value.TotalMilliseconds >= minMilliseconds && value.TotalMilliseconds <= maxMilliseconds;

        // The watchdog is the absolute movement cap. An unbounded value can exceed the timer's maximum interval,
        // so arming would fail after the relays were energized.
        if (!InRange(options.SafetyWatchdogTimeout, RoofControllerLimits.MinSafetyWatchdogTimeoutSeconds * 1000, RoofControllerLimits.MaxSafetyWatchdogTimeoutSeconds * 1000))
        {
            AddFailure($"SafetyWatchdogTimeout must be between {RoofControllerLimits.MinSafetyWatchdogTimeoutSeconds} and {RoofControllerLimits.MaxSafetyWatchdogTimeoutSeconds} seconds.");
        }

        if (!InRange(options.DigitalInputPollInterval, RoofControllerLimits.MinDigitalInputPollIntervalMilliseconds, RoofControllerLimits.MaxDigitalInputPollIntervalMilliseconds))
        {
            AddFailure($"DigitalInputPollInterval must be between {RoofControllerLimits.MinDigitalInputPollIntervalMilliseconds} and {RoofControllerLimits.MaxDigitalInputPollIntervalMilliseconds} ms.");
        }

        // Sub-millisecond intervals truncate to zero in the periodic loop and silently disable verification.
        if (!InRange(options.PeriodicVerificationInterval, RoofControllerLimits.MinPeriodicVerificationIntervalMilliseconds, RoofControllerLimits.MaxPeriodicVerificationIntervalMilliseconds))
        {
            AddFailure($"PeriodicVerificationInterval must be between {RoofControllerLimits.MinPeriodicVerificationIntervalMilliseconds} and {RoofControllerLimits.MaxPeriodicVerificationIntervalMilliseconds} ms.");
        }
        else if (options.PeriodicVerificationInterval >= options.SafetyWatchdogTimeout)
        {
            AddFailure("PeriodicVerificationInterval must be shorter than SafetyWatchdogTimeout.");
        }

        if (!InRange(options.LimitSwitchDebounce, 0, RoofControllerLimits.MaxLimitSwitchDebounceMilliseconds))
        {
            AddFailure($"LimitSwitchDebounce must be between 0 and {RoofControllerLimits.MaxLimitSwitchDebounceMilliseconds} ms.");
        }

        // Periodic verification is the software fallback when input edges are missed or polling is disabled,
        // so it is valid on its own. With neither enabled, no software limit or fault stop exists.
        if (!options.EnableDigitalInputPolling && !options.EnablePeriodicVerificationWhileMoving)
        {
            AddFailure("At least one of EnableDigitalInputPolling or EnablePeriodicVerificationWhileMoving must be enabled; otherwise limits and faults are never supervised in software.");
        }

        if (options.MaxConsecutiveInputReadFailures < RoofControllerLimits.MinConsecutiveInputReadFailures
            || options.MaxConsecutiveInputReadFailures > RoofControllerLimits.MaxConsecutiveInputReadFailures)
        {
            AddFailure($"MaxConsecutiveInputReadFailures must be between {RoofControllerLimits.MinConsecutiveInputReadFailures} and {RoofControllerLimits.MaxConsecutiveInputReadFailures}.");
        }

        if (options.OperatorLeaseTimeout is { } lease)
        {
            if (!InRange(lease, RoofControllerLimits.MinOperatorLeaseSeconds * 1000, RoofControllerLimits.MaxOperatorLeaseSeconds * 1000))
            {
                AddFailure($"OperatorLeaseTimeout must be between {RoofControllerLimits.MinOperatorLeaseSeconds} and {RoofControllerLimits.MaxOperatorLeaseSeconds} seconds when set.");
            }
            else if (lease >= options.SafetyWatchdogTimeout)
            {
                AddFailure("OperatorLeaseTimeout must be shorter than SafetyWatchdogTimeout; the lease is renewable, the watchdog is the absolute cap.");
            }
        }

        if (options.AtSpeedConfirmationTimeout is { } atSpeed)
        {
            if (!InRange(atSpeed, RoofControllerLimits.MinAtSpeedConfirmationSeconds * 1000, RoofControllerLimits.MaxAtSpeedConfirmationSeconds * 1000))
            {
                AddFailure($"AtSpeedConfirmationTimeout must be between {RoofControllerLimits.MinAtSpeedConfirmationSeconds} and {RoofControllerLimits.MaxAtSpeedConfirmationSeconds} seconds when set.");
            }
            else if (atSpeed >= options.SafetyWatchdogTimeout)
            {
                AddFailure("AtSpeedConfirmationTimeout must be shorter than SafetyWatchdogTimeout.");
            }
        }

        if (options.DriveStopConfirmationTimeout is { } driveStop
            && !InRange(driveStop, RoofControllerLimits.MinDriveStopConfirmationSeconds * 1000, RoofControllerLimits.MaxDriveStopConfirmationSeconds * 1000))
        {
            AddFailure($"DriveStopConfirmationTimeout must be between {RoofControllerLimits.MinDriveStopConfirmationSeconds} and {RoofControllerLimits.MaxDriveStopConfirmationSeconds} seconds when set.");
        }

        if (options.DepartureReleaseTimeout is { } departure)
        {
            if (!InRange(departure, RoofControllerLimits.MinDepartureReleaseSeconds * 1000, RoofControllerLimits.MaxDepartureReleaseSeconds * 1000))
            {
                AddFailure($"DepartureReleaseTimeout must be between {RoofControllerLimits.MinDepartureReleaseSeconds} and {RoofControllerLimits.MaxDepartureReleaseSeconds} seconds when set.");
            }
            else if (departure >= options.SafetyWatchdogTimeout)
            {
                AddFailure("DepartureReleaseTimeout must be shorter than SafetyWatchdogTimeout.");
            }
            else if (departure <= options.LimitSwitchDebounce)
            {
                AddFailure("DepartureReleaseTimeout must be longer than LimitSwitchDebounce; the release is verified only after the debounce.");
            }
        }

        // Hardware-mode checks (for example IgnorePhysicalLimitSwitches on real hardware) depend on runtime state
        // and are enforced by the controller service, not here.

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
