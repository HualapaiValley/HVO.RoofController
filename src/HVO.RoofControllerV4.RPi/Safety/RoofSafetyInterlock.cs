using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>What the safety interlock decided for one Open, before the controller's own checks.</summary>
/// <param name="Refusal">The refusal, or null when Open may go on to the controller.</param>
/// <param name="Caller">Who asked, as audit lines name them.</param>
/// <param name="Safety">The safety conditions the decision was made on.</param>
/// <param name="Overridden">True when Open goes on only because the caller overrode the conditions (Warn mode).</param>
public sealed record RoofSafetyOpenDecision(RoofControllerException? Refusal, string Caller, RoofSafetyStatus Safety, bool Overridden)
{
    /// <summary>
    /// The conditions the command history (#206) notes for this Open: the state when it was not Safe, null when it was.
    /// Null as well for an Open the controller accepted while nothing watches the conditions (the interlock Off, no
    /// safety source configured, no override), whose Unknown says nothing (#216). Unsafe, Unknown in another mode or from
    /// a configured source, an override and every refusal keep the state.
    /// </summary>
    /// <param name="accepted">True when the controller accepted Open.</param>
    public string? HistorySafetyState(bool accepted)
        => Safety.State == RoofSafetyState.Safe
            || (accepted && !Overridden
                && Safety is { State: RoofSafetyState.Unknown, InterlockMode: RoofSafetyInterlockMode.Off, Sources.Count: 0 })
            ? null
            : Safety.State.ToString();
}

/// <summary>
/// The safety conditions' part in Open (<see cref="RoofSafetyOptions.InterlockMode"/>): Off reports and logs, Warn
/// refuses unless overridden by a caller with the override role (audit-logged), Block refuses. Only Open is checked:
/// Close and Stop never are. The controller's own interlocks still check every Open this lets through, so what Open did
/// is logged once the controller has answered (<see cref="ReportOutcome"/>): an override is audited only when Open was
/// accepted. The mode and the override role are the ones <see cref="IRoofSafetyService.GetCurrent"/> reports, so what is
/// enforced is what every client is shown.
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
    /// Checks an Open request from <paramref name="user"/> against the safety conditions now. The decision's
    /// <see cref="RoofSafetyOpenDecision.Refusal"/> is null when Open may go on to the controller; otherwise it is
    /// <see cref="RoofControllerErrorCode.SafetyOverrideRequired"/> or <see cref="RoofControllerErrorCode.SafetyInterlockActive"/>,
    /// and is logged here. Pass the decision to <see cref="ReportOutcome"/> once the controller has answered.
    /// </summary>
    /// <param name="user">The caller, who already holds the Operator role that Open needs.</param>
    /// <param name="resource">The request, for the override role's policy (a scoped key's scopes depend on it).</param>
    /// <param name="overrideRequested">True when the request asks to open even though the conditions are not safe.</param>
    public async Task<RoofSafetyOpenDecision> CheckOpenAsync(ClaimsPrincipal user, object? resource, bool overrideRequested)
    {
        ArgumentNullException.ThrowIfNull(user);
        var safety = _safety.GetCurrent();
        var caller = RoofPrincipalFactory.DescribeCaller(user);
        if (safety.State == RoofSafetyState.Safe)
        {
            return new RoofSafetyOpenDecision(null, caller, safety, Overridden: false);
        }

        var state = safety.State.ToString().ToLowerInvariant();
        var reasons = Reasons(safety);
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
                    return Refuse(new RoofControllerException(
                        RoofControllerErrorCode.SafetyOverrideRequired,
                        $"The safety conditions are {state} ({reasons}). To open anyway, send Open again with overrideSafety=true; " +
                        $"{DescribeOverrideRole(overrideRole)} may override, and the override is logged. Close and Stop are not affected."));
                }

                if (!await MayOverrideAsync(user, resource, overrideRole).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "AUDIT safety override by {Caller} refused: the override needs the {OverrideRole} role (conditions {SafetyState}: {Reasons})",
                        caller, overrideRole, safety.State, reasons);
                    return Refuse(new RoofControllerException(
                        RoofControllerErrorCode.SafetyInterlockActive,
                        $"The safety conditions are {state} ({reasons}), and {DescribeOverrideRole(overrideRole)} may override them. Close and Stop are not affected."));
                }

                return new RoofSafetyOpenDecision(null, caller, safety, Overridden: true);

            case RoofSafetyInterlockMode.Block:
                _logger.LogWarning(
                    "Open by {Caller} refused by the safety interlock (Block{Override}): conditions {SafetyState}: {Reasons}",
                    caller, overrideRequested ? ", override not accepted" : string.Empty, safety.State, reasons);
                return Refuse(new RoofControllerException(
                    RoofControllerErrorCode.SafetyInterlockActive,
                    $"The safety interlock refuses Open while the safety conditions are {state} ({reasons}). " +
                    "It takes no override in this mode. Close and Stop are not affected."));

            default:
                // Off: advisory. ReportOutcome logs it once the controller has accepted Open.
                return new RoofSafetyOpenDecision(null, caller, safety, Overridden: false);
        }

        RoofSafetyOpenDecision Refuse(RoofControllerException refusal) => new(refusal, caller, safety, Overridden: false);
    }

    /// <summary>
    /// Logs what an Open the interlock let through did, once the controller has answered it: an accepted override as
    /// <c>AUDIT safety override</c>, an override the controller refused as having had no effect, and an Open accepted
    /// while the conditions were not Safe with the interlock off. Nothing for a refusal of the interlock's own (logged
    /// when it was made) or while the conditions were Safe.
    /// </summary>
    /// <param name="decision">The decision <see cref="CheckOpenAsync"/> made for this Open.</param>
    /// <param name="controllerError">Why the controller refused Open, or null when it accepted it.</param>
    public void ReportOutcome(RoofSafetyOpenDecision decision, Exception? controllerError)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Refusal is not null || decision.Safety.State == RoofSafetyState.Safe)
        {
            return;
        }

        var reasons = Reasons(decision.Safety);
        if (decision.Overridden)
        {
            if (controllerError is null)
            {
                _logger.LogWarning(
                    "AUDIT safety override: Open by {Caller} accepted while the conditions are {SafetyState} (interlock Warn): {Reasons}",
                    decision.Caller, decision.Safety.State, reasons);
            }
            else
            {
                _logger.LogInformation(
                    "Open by {Caller} with a safety override was refused by the controller ({Code}), so the override had no effect (conditions {SafetyState}: {Reasons})",
                    decision.Caller, (controllerError as RoofControllerException)?.Code ?? RoofControllerErrorCode.Unknown, decision.Safety.State, reasons);
            }
        }
        else if (controllerError is null && decision.Safety.InterlockMode == RoofSafetyInterlockMode.Off)
        {
            _logger.LogInformation(
                "Open by {Caller} accepted while the safety conditions are {SafetyState} (interlock Off, advisory only): {Reasons}",
                decision.Caller, decision.Safety.State, reasons);
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

    private static string Reasons(RoofSafetyStatus safety) => string.Join("; ", safety.Reasons);

    private static string DescribeOverrideRole(RoofSafetyOverrideRole role)
        => role == RoofSafetyOverrideRole.Operator ? "anyone who can open the roof" : "only an admin";
}
