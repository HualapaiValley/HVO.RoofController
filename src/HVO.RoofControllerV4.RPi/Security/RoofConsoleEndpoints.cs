using System;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>What <c>POST /console/stop</c> reports: a <see cref="RoofStopOutcome"/> name and a message for the operator.</summary>
public sealed record ConsoleStopResponse(string Outcome, string Message);

/// <summary>
/// <c>POST /console/stop</c>: a Stop for the browser console that does not need the Blazor circuit. The reconnect dialog
/// posts it with <c>fetch</c> while the SignalR connection is down (<c>wwwroot/js/console-stop.js</c>). It accepts only
/// the console cookie, applies the Stop policy, and requires the antiforgery token that <c>App.razor</c> renders into the
/// dialog's form. The Origin check covers it as well.
/// </summary>
public static class RoofConsoleEndpoints
{
    public static IEndpointRouteBuilder MapRoofConsoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Antiforgery is validated in StopAsync so a stale page gets a message the dialog can show instead of a bare 400.
        endpoints.MapPost(RoofControllerSecurityDefaults.ConsoleStopPath, StopAsync)
            .RequireAuthorization(new AuthorizeAttribute(RoofControllerSecurityDefaults.StopPolicy)
            {
                AuthenticationSchemes = RoofControllerSecurityDefaults.CookieScheme
            })
            .DisableAntiforgery()
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> StopAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        IRoofControllerServiceV4 roofController,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(RoofConsoleEndpoints).FullName!);
        var caller = RoofPrincipalFactory.DescribeCaller(http.User);

        if (!await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Console stop refused for {KeyName} from {RemoteIp}: missing or invalid antiforgery token",
                caller,
                http.Connection.RemoteIpAddress);
            return Results.Json(
                new ConsoleStopResponse(
                    nameof(RoofStopOutcome.Failed),
                    "Stop was not sent because this page is out of date. Reload the page, or use the stop control at the roof."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        logger.LogInformation("Console stop (no circuit) from {KeyName} at {RemoteIp}", caller, http.Connection.RemoteIpAddress);

        Result<RoofControllerStatus> result;
        try
        {
            result = roofController.Stop(RoofControllerStopReason.NormalStop);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Console stop failed");
            return Results.Json(
                new ConsoleStopResponse(
                    nameof(RoofStopOutcome.Failed),
                    RoofStopText.Failed(RoofConsoleRules.DescribeFailure(ex))),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        RoofStatusResponse? snapshot = null;
        try
        {
            snapshot = roofController.GetCurrentStatusSnapshot();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read status after console stop");
        }

        if (!result.IsSuccessful)
        {
            logger.LogWarning(result.Error, "Console stop reported a failure");
        }

        var (outcome, message) = RoofConsoleRules.ClassifyStop(result.IsSuccessful, result.Error, snapshot);
        return Results.Json(
            new ConsoleStopResponse(outcome.ToString(), message),
            statusCode: outcome == RoofStopOutcome.Acknowledged ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
