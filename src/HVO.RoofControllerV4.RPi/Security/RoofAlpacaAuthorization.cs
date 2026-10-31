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
/// For an Alpaca Close's routes (the ticket for a Close, the Close itself): a refusal of the caller's credential raises
/// the UIs' "not authorised" alert (#220, §6), naming the Alpaca client the server reported, never the key.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AlpacaCloseAuthAlertAttribute : Attribute;

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
/// Raises the "not authorised" alert when an Alpaca Close's credential is refused (missing, wrong, or the wrong account),
/// then answers as authorization always does. A Close only: an Open's refusal is logged, not alerted.
/// </summary>
public sealed class RoofAlpacaAuthorizationResultHandler(RoofAlpacaCommands alpaca, IOptionsMonitor<RoofAlpacaCommandOptions> options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && context.GetEndpoint()?.Metadata.GetMetadata<AlpacaCloseAuthAlertAttribute>() is not null
            && IsCloseRequest(context))
        {
            alpaca.ReportAuthFailure(RoofAlpacaCommandKind.Close, RoofAlpacaClientMetadata.From(context, options.CurrentValue));
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool IsCloseRequest(HttpContext context)
        => !context.Request.Path.Value!.EndsWith(RoofAlpacaContract.TicketRoute, StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.Request.Query["kind"], nameof(RoofAlpacaCommandKind.Close), StringComparison.OrdinalIgnoreCase);
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
