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

        if (options.SafetyWatchdogTimeout <= TimeSpan.Zero)
        {
            AddFailure("SafetyWatchdogTimeout must be greater than zero.");
        }

        if (options.PeriodicVerificationInterval <= TimeSpan.Zero)
        {
            AddFailure("PeriodicVerificationInterval must be greater than zero.");
        }
        else if (options.SafetyWatchdogTimeout > TimeSpan.Zero && options.PeriodicVerificationInterval > options.SafetyWatchdogTimeout)
        {
            AddFailure("PeriodicVerificationInterval must be less than or equal to SafetyWatchdogTimeout.");
        }

        if (options.EnablePeriodicVerificationWhileMoving && !options.EnableDigitalInputPolling)
        {
            AddFailure("EnablePeriodicVerificationWhileMoving requires EnableDigitalInputPolling to also be enabled.");
        }

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
