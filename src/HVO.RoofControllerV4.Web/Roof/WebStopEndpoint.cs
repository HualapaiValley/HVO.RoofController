using System.Net;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>What <c>POST /stop</c> reports: a <see cref="RoofStopOutcome"/> name and the shared message for it.</summary>
public sealed record WebStopResponse(string Outcome, string Message);

/// <summary>
/// <c>POST /stop</c>: every page's Stop (<c>App.razor</c>, sent by <c>wwwroot/js/stop.js</c>), with or without the page's
/// live connection. It needs the antiforgery token rendered into the page's Stop form and nothing else: the person's
/// session when there is one, still used after it ended (the web UI's Stop key sends Stop for them then), and no
/// credential for a page that was signed out when it was rendered (the controller decides, with
/// <c>RoofControllerSecurity:AllowAnonymousStop</c>). The origin check covers it as well.
/// </summary>
public static class WebStopEndpoint
{
    public static IEndpointRouteBuilder MapWebStopEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Antiforgery is checked in StopAsync so an old page gets a message it can show instead of a bare 400.
        endpoints.MapPost(WebAuthentication.StopPostPath, StopAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> StopAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        WebSessionStore store,
        WebStopKey stopKey,
        RoofControllerConnector connector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebStopEndpoint).FullName!);
        var remote = http.Connection.RemoteIpAddress;

        // The cookie of a session that ended still reaches here (WebAuthentication.ValidatePrincipalAsync).
        var authentication = await http.AuthenticateAsync(WebAuthentication.Scheme);
        var ticket = WebAuthentication.ReadTicket(authentication.Principal, authentication.Properties);
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            // The token names who the page was rendered for. With no cookie now, the page was signed in and no longer is.
            logger.LogWarning(
                "Web stop refused for {Name} from {RemoteIp}: missing or stale antiforgery token",
                ticket?.Name ?? "a signed-out page",
                remote);
            return ticket is null
                ? Answer(RoofStopOutcome.Failed, RoofStopText.PageSignedOut, StatusCodes.Status401Unauthorized)
                : Answer(RoofStopOutcome.Failed, RoofStopText.PageOutOfDate, StatusCodes.Status400BadRequest);
        }

        RoofStopResult result;
        try
        {
            if (ticket is null)
            {
                logger.LogInformation("Web stop from a signed-out page at {RemoteIp}", remote);
                using var anonymous = connector.Create(credential: null, WebSession.RequestTimeout);
                result = await anonymous.StopAsync(CancellationToken.None);
            }
            else
            {
                logger.LogInformation("Web stop from {Name} at {RemoteIp}", ticket.Name, remote);
                result = store.TryFind(ticket.SessionId, out var session)
                    ? await session.StopAsync(CancellationToken.None)
                    : await StopForTicketAsync(ticket, stopKey, connector);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web stop failed");
            return Answer(RoofStopOutcome.Failed, RoofStopText.Failed(RoofText.DescribeFailure(ex)), StatusCodes.Status500InternalServerError);
        }

        if (result.IsAcknowledged)
        {
            logger.LogInformation("Web stop for {Name}: {Outcome}", ticket?.Name ?? "a signed-out page", result.Outcome);
        }
        else
        {
            logger.LogWarning(result.Error, "Web stop for {Name}: {Outcome}", ticket?.Name ?? "a signed-out page", result.Outcome);
        }

        return Answer(result.Outcome, result.Message, result.IsAcknowledged ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    // A session the web UI no longer remembers (it expired from memory): Stop is sent with the cookie's session and the
    // Stop key, as for a remembered one.
    private static async Task<RoofStopResult> StopForTicketAsync(WebTicket ticket, WebStopKey stopKey, RoofControllerConnector connector)
    {
        var session = new RoofSessionCredential(ticket.Token, ticket.Name, ticket.Role, ticket.SessionId, ticket.ExpiresUtc);
        using var client = connector.Create(new RoofWebCredential(session, stopKey.Value), WebSession.RequestTimeout);
        var result = await client.StopAsync(CancellationToken.None);
        return result.Error is RoofApiException { StatusCode: HttpStatusCode.Unauthorized }
            ? result with { Message = RoofStopText.PageSignedOut }
            : result;
    }

    private static IResult Answer(RoofStopOutcome outcome, string message, int statusCode)
        => Results.Json(new WebStopResponse(outcome.ToString(), message), statusCode: statusCode);
}
