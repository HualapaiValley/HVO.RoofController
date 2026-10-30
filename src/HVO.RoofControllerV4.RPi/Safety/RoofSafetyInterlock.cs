using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>
/// The safety conditions' part in Open (<see cref="RoofSafetyOptions.InterlockMode"/>): Off reports and logs, Warn
/// refuses unless overridden by a caller with the override role (audit-logged), Block refuses. Only Open is checked:
/// Close and Stop never are. The controller's own interlocks still check every Open this lets through. The mode and the
/// override role are the ones <see cref="IRoofSafetyService.GetCurrent"/> reports, so what is enforced is what every
/// client is shown.
/// </summary>
public sealed class RoofSafetyInterlock
{
    private readonly IRoofSafetyService _safety;
    private readonly IAuthorizationService _authorization;
    private readonly ILogger<RoofSafetyInterlock> _logger;

    public RoofSafetyInterlock(
        IRoofSafetyService safety,
        IAuthorizationService authorization,
        ILogger<RoofSafetyInterlock>? logger = null)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _logger = logger ?? NullLogger<RoofSafetyInterlock>.Instance;
    }

    /// <summary>
    /// Checks an Open request from <paramref name="user"/> against the safety conditions now. Null when Open may go on to
    /// the controller; otherwise the refusal, <see cref="RoofControllerErrorCode.SafetyOverrideRequired"/> or
    /// <see cref="RoofControllerErrorCode.SafetyInterlockActive"/>.
    /// </summary>
    /// <param name="user">The caller, who already holds the Operator role that Open needs.</param>
    /// <param name="resource">The request, for the override role's policy (a scoped key's scopes depend on it).</param>
    /// <param name="overrideRequested">True when the request asks to open even though the conditions are not safe.</param>
    public async Task<RoofControllerException?> CheckOpenAsync(ClaimsPrincipal user, object? resource, bool overrideRequested)
    {
        ArgumentNullException.ThrowIfNull(user);
        var safety = _safety.GetCurrent();
        if (safety.State == RoofSafetyState.Safe)
        {
            return null;
        }

        var caller = RoofPrincipalFactory.DescribeCaller(user);
        var state = safety.State.ToString().ToLowerInvariant();
        var reasons = string.Join("; ", safety.Reasons);
        // Only the Admin role is narrower than Open's own Operator policy; anything else reported counts as Admin.
        var overrideRole = safety.OverrideRole == RoofControllerApiContract.OperatorRole ? RoofSafetyOverrideRole.Operator : RoofSafetyOverrideRole.Admin;
        switch (safety.InterlockMode)
        {
            case RoofSafetyInterlockMode.Warn:
                if (!overrideRequested)
                {
                    _logger.LogInformation(
                        "Open by {Caller} refused by the safety interlock (Warn, no override): conditions {SafetyState}: {Reasons}",
                        caller, safety.State, reasons);
                    return new RoofControllerException(
                        RoofControllerErrorCode.SafetyOverrideRequired,
                        $"The safety conditions are {state} ({reasons}). To open anyway, send Open again with overrideSafety=true; " +
                        $"{DescribeOverrideRole(overrideRole)} may override, and the override is logged. Close and Stop are not affected.");
                }

                if (!await MayOverrideAsync(user, resource, overrideRole).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "AUDIT safety override by {Caller} refused: the override needs the {OverrideRole} role (conditions {SafetyState}: {Reasons})",
                        caller, overrideRole, safety.State, reasons);
                    return new RoofControllerException(
                        RoofControllerErrorCode.SafetyInterlockActive,
                        $"The safety conditions are {state} ({reasons}), and {DescribeOverrideRole(overrideRole)} may override them. Close and Stop are not affected.");
                }

                _logger.LogWarning(
                    "AUDIT safety override: Open by {Caller} while the conditions are {SafetyState} (interlock Warn): {Reasons}",
                    caller, safety.State, reasons);
                return null;

            case RoofSafetyInterlockMode.Block:
                _logger.LogWarning(
                    "Open by {Caller} refused by the safety interlock (Block{Override}): conditions {SafetyState}: {Reasons}",
                    caller, overrideRequested ? ", override not accepted" : string.Empty, safety.State, reasons);
                return new RoofControllerException(
                    RoofControllerErrorCode.SafetyInterlockActive,
                    $"The safety interlock refuses Open while the safety conditions are {state} ({reasons}). " +
                    "It takes no override in this mode. Close and Stop are not affected.");

            default:
                _logger.LogInformation(
                    "Open by {Caller} while the safety conditions are {SafetyState} (interlock Off, advisory only): {Reasons}",
                    caller, safety.State, reasons);
                return null;
        }
    }

    private async Task<bool> MayOverrideAsync(ClaimsPrincipal user, object? resource, RoofSafetyOverrideRole role)
    {
        if (role == RoofSafetyOverrideRole.Operator)
        {
            // Open itself needs the Operator policy, which the caller has passed.
            return true;
        }

        var result = await _authorization.AuthorizeAsync(user, resource, RoofControllerSecurityDefaults.AdminPolicy).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static string DescribeOverrideRole(RoofSafetyOverrideRole role)
        => role == RoofSafetyOverrideRole.Operator ? "anyone who can open the roof" : "only an admin";
}
