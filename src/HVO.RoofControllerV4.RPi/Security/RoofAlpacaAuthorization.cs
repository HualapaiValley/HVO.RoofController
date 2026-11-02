using System.Net;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Marks an endpoint the External Alpaca account (<see cref="RoofAlpacaContract.CommanderRole"/>) may use: its shutter
/// routes and the status reads. Every other endpoint refuses it with 403 (<see cref="RoofAlpacaCommanderFilter"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AlpacaCommanderAllowedAttribute : Attribute;

/// <summary>
/// For an Alpaca command's routes (the ticket, the Close, the Open): a refusal of the caller's credential is reported
/// (#220, §6, Y3): for a Close (its ticket or itself) it raises the UIs' "not authorised" alert, naming the Alpaca client
/// the server reported, never the key; for an Open it is only logged.
/// </summary>
/// <remarks>
/// The command is the endpoint's own (<see cref="Command"/>), never taken from the request's path: only the ticket
/// endpoint, which names none, reads its <c>kind</c> (PR #232 review S3).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AlpacaCommandAuthReportAttribute : Attribute
{
    /// <summary>The ticket endpoint: the command is the one its <c>kind</c> names.</summary>
    public AlpacaCommandAuthReportAttribute()
    {
    }

    /// <summary>An endpoint for one command, <paramref name="command"/>.</summary>
    public AlpacaCommandAuthReportAttribute(RoofAlpacaCommandKind command) => Command = command;

    /// <summary>The endpoint's command; null for the ticket endpoint.</summary>
    public RoofAlpacaCommandKind? Command { get; }
}

/// <summary>
/// Deny by default for the External Alpaca account (#220): a request authenticated with its key reaches only the
/// endpoints marked <see cref="AlpacaCommanderAllowedAttribute"/>; every other one (Stop, fault clear, grants,
/// settings, leases, restart, keys, the ordinary Open and Close, the status hub) answers 403. Runs after routing and
/// authentication, before authorization.
/// </summary>
public sealed class RoofAlpacaCommanderFilter(RequestDelegate next, ILogger<RoofAlpacaCommanderFilter> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.IsInRole(RoofAlpacaContract.CommanderRole)
            && context.GetEndpoint()?.Metadata.GetMetadata<AlpacaCommanderAllowedAttribute>() is null)
        {
            logger.LogWarning("Refused the External Alpaca account's key for {Method} {Path}: it may use only its own routes and the status",
                context.Request.Method, context.Request.Path.Value);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}

/// <summary>
/// Refuses every API key with 403 <c>CredentialNotAllowed</c>: only a person's interactive session (a password or PIN
/// session from the web UI, the terminal or the kiosk) may grant an override, clear the Stop hold, hold the operator lease
/// or acknowledge an alert (#220). The UI is taken from the session type, never a request field.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RoofPersonSessionOnlyAttribute : Attribute, IActionFilter, IOrderedFilter
{
    /// <summary>Before <c>[ApiController]</c>'s model-state filter (order -2000).</summary>
    public int Order => -3000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!RoofPrincipalFactory.IsPersonSession(context.HttpContext.User) && context.Controller is ControllerBase controller)
        {
            context.Result = RoofProblemResults.Create(
                controller,
                RoofControllerErrorCode.CredentialNotAllowed,
                "Only a person's session in the web UI, the terminal or the kiosk may do this; no API key may.");
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>
/// Reports an Alpaca command whose credential is refused (missing, wrong, or the wrong account), then answers as
/// authorization always does: a Close's raises the "not authorised" alert; an Open's is logged, not alerted.
/// </summary>
public sealed class RoofAlpacaAuthorizationResultHandler(RoofAlpacaCommands alpaca, IOptionsMonitor<RoofAlpacaCommandOptions> options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && context.GetEndpoint()?.Metadata.GetMetadata<AlpacaCommandAuthReportAttribute>() is { } report
            && (report.Command ?? TicketKind(context)) is { } kind)
        {
            alpaca.ReportAuthFailure(kind, RoofAlpacaClientMetadata.From(context, options.CurrentValue));
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>The command a refused ticket request named in its <c>kind</c>; null when it names none.</summary>
    private static RoofAlpacaCommandKind? TicketKind(HttpContext context)
    {
        var kind = context.Request.Query["kind"].ToString();
        return string.Equals(kind, nameof(RoofAlpacaCommandKind.Close), StringComparison.OrdinalIgnoreCase) ? RoofAlpacaCommandKind.Close
            : string.Equals(kind, nameof(RoofAlpacaCommandKind.Open), StringComparison.OrdinalIgnoreCase) ? RoofAlpacaCommandKind.Open
            : null;
    }
}

/// <summary>The Alpaca client metadata the Alpaca server forwards, believed only from a trusted Alpaca server address.</summary>
public static class RoofAlpacaClientMetadata
{
    /// <summary>
    /// What the Alpaca server reports about its client: <see cref="RoofAlpacaClient.None"/> unless the TCP peer is a
    /// trusted Alpaca server address. Never used for authorization; recorded as reported.
    /// </summary>
    public static RoofAlpacaClient From(HttpContext context, RoofAlpacaCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsTrustedPeer(context.Connection.RemoteIpAddress))
        {
            return RoofAlpacaClient.None;
        }

        var headers = context.Request.Headers;
        var method = headers[RoofAlpacaContract.AuthMethodHeader].ToString();
        return new RoofAlpacaClient(
            Number(headers[RoofAlpacaContract.ClientIdHeader]),
            Number(headers[RoofAlpacaContract.ClientTransactionIdHeader]),
            IPAddress.TryParse(headers[RoofAlpacaContract.ClientAddressHeader].ToString(), out var address) ? RoofAlpacaCommandOptions.Normalize(address).ToString() : null,
            method is "header" or "basic" ? method : null);
    }

    private static uint? Number(Microsoft.Extensions.Primitives.StringValues value)
        => value.Count == 1 && uint.TryParse(value[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
}
