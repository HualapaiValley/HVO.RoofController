using System.Net;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>What <c>POST /stop</c> reports: a <see cref="RoofStopOutcome"/> name and the shared message for it.</summary>
public sealed record WebStopResponse(string Outcome, string Message);

/// <summary>
/// <c>POST /stop</c>: every page's Stop (<c>App.razor</c>, sent by <c>wwwroot/js/stop.js</c>), with or without the page's
/// live connection. It needs the antiforgery token rendered into the page's Stop form and nothing else: the person's
/// session when there is one, still used after it ended (the web UI's Stop key sends Stop for them then); the person's
/// Stop pass once the sign-in cookie has gone (<see cref="WebStopPass"/>: the Stop key, on their behalf); and no
/// credential for anyone else (the controller decides, with <c>RoofControllerSecurity:AllowAnonymousStop</c>). The
/// origin check covers it as well, and <see cref="WebStopLimiter"/> limits each person, and each address's signed-out pages.
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
        WebStopPass stopPass,
        WebStopLimiter limiter,
        RoofControllerConnector connector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebStopEndpoint).FullName!);
        var remote = http.Connection.RemoteIpAddress;
        var local = http.Connection.LocalIpAddress;

        // The cookie of a session that ended still reaches here (WebAuthentication.ValidatePrincipalAsync). Once it has
        // gone, the person's Stop pass names them.
        var authentication = await http.AuthenticateAsync(WebAuthentication.Scheme);
        var ticket = WebAuthentication.ReadTicket(authentication.Principal, authentication.Properties);
        var pass = ticket is null ? stopPass.Read(http) : null;
        var name = ticket?.Name ?? pass?.Name ?? SignedOutPage;
        if (!await IsAntiforgeryValidAsync(http, antiforgery, pass))
        {
            // The token names who the page was rendered for. With no cookie now, the page was signed in and no longer is.
            // Nothing is sent, so the refusal counts against no one's Stops; only how often it is logged is limited.
            if (limiter.TryAcquire(WebStopLimiter.ForRefusals(remote, local)))
            {
                logger.LogWarning("Web stop refused for {Name} from {RemoteIp}: missing or stale antiforgery token", name, remote);
            }

            return ticket is null
                ? Answer(RoofStopOutcome.Failed, RoofStopText.PageSignedOut, StatusCodes.Status401Unauthorized)
                : Answer(RoofStopOutcome.Failed, RoofStopText.PageOutOfDate, StatusCodes.Status400BadRequest);
        }

        // A person is counted by their session, so others at the same address cannot use up their Stops.
        var sender = (ticket?.SessionId ?? pass?.SessionId) is { } sessionId
            ? WebStopLimiter.ForSession(sessionId)
            : WebStopLimiter.ForAddress(remote, local);
        if (!limiter.TryAcquire(sender))
        {
            if (limiter.TryAcquire(WebStopLimiter.ForTooMany(sender)))
            {
                logger.LogWarning("Web stop refused for {Name} from {RemoteIp}: more than the allowed Stops", name, remote);
            }

            return Answer(RoofStopOutcome.Failed, RoofStopText.Failed(WebStopTexts.TooMany), StatusCodes.Status429TooManyRequests);
        }

        RoofStopResult result;
        try
        {
            if (ticket is not null)
            {
                logger.LogInformation("Web stop from {Name} at {RemoteIp}", ticket.Name, remote);
                result = store.TryFind(ticket.SessionId, out var session)
                    ? await session.StopAsync(CancellationToken.None)
                    : await StopForTicketAsync(ticket, stopKey, connector);
            }
            else if (pass is not null && stopKey.Value is { } key)
            {
                logger.LogInformation("Web stop from {Name} at {RemoteIp}, signed out: sent with the Stop key", pass.Name, remote);
                result = await StopForPassAsync(pass, key, connector);
            }
            else
            {
                logger.LogInformation("Web stop from a signed-out page at {RemoteIp}", remote);
                using var anonymous = connector.Create(credential: null, WebSession.RequestTimeout);
                result = await anonymous.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web stop failed");
            return Answer(RoofStopOutcome.Failed, RoofStopText.Failed(RoofText.DescribeFailure(ex)), StatusCodes.Status500InternalServerError);
        }

        if (result.IsAcknowledged)
        {
            logger.LogInformation("Web stop for {Name}: {Outcome}", name, result.Outcome);
        }
        else
        {
            logger.LogWarning(result.Error, "Web stop for {Name}: {Outcome}", name, result.Outcome);
        }

        return Answer(result.Outcome, result.Message, result.IsAcknowledged ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private const string SignedOutPage = "a signed-out page";

    // A page rendered while the person was signed in carries a token for them. When their sign-in cookie has gone, their
    // Stop pass stands in for it; a page rendered since (the sign-in page) carries a token for nobody.
    private static async Task<bool> IsAntiforgeryValidAsync(HttpContext http, IAntiforgery antiforgery, WebStopPassHolder? pass)
    {
        if (await antiforgery.IsRequestValidAsync(http))
        {
            return true;
        }

        if (pass is null)
        {
            return false;
        }

        var user = http.User;
        http.User = WebAuthentication.CreatePrincipal(pass);
        try
        {
            return await antiforgery.IsRequestValidAsync(http);
        }
        finally
        {
            http.User = user;
        }
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

    // A person whose sign-in cookie has gone: the Stop key alone, naming them. A refusal means the key was refused.
    private static async Task<RoofStopResult> StopForPassAsync(WebStopPassHolder pass, string stopKey, RoofControllerConnector connector)
    {
        var credential = RoofIdentityContract.IsValidName(pass.Name) ? new RoofApiKeyCredential(stopKey, pass.Name) : new RoofApiKeyCredential(stopKey);
        using var client = connector.Create(credential, WebSession.RequestTimeout);
        var result = await client.StopAsync(CancellationToken.None);
        return result.Error is RoofApiException { StatusCode: HttpStatusCode.Unauthorized }
            ? result with { Message = RoofStopText.PageSignedOut }
            : result;
    }

    private static IResult Answer(RoofStopOutcome outcome, string message, int statusCode)
        => Results.Json(new WebStopResponse(outcome.ToString(), message), statusCode: statusCode);
}
