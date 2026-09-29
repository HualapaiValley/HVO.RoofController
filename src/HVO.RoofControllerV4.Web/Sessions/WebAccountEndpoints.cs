using System.Net;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Sign-in and sign-out. The sign-in page (static, needs no live connection) posts a plain form with the name, the
/// password, the page to go back to and the antiforgery token; the web UI signs in at the controller and keeps the
/// session in the person's cookie. Every problem is sent back to the sign-in page as a message, never as a bare error.
/// </summary>
public static class WebAccountEndpoints
{
    private const int MaximumReturnUrlLength = 2048;

    public static IEndpointRouteBuilder MapWebAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Antiforgery is checked in SignInAsync, so a stale form goes back to the sign-in page rather than a bare 400.
        endpoints.MapPost(WebAuthentication.SignInPostPath, SignInAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

        // Signing out only ends the caller's own session. The cookie is SameSite=Strict and the origin check covers
        // every form post, so no antiforgery token is needed (a page left open for hours can still sign out).
        endpoints.MapPost(WebAuthentication.SignOutPostPath, SignOutAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// <paramref name="returnUrl"/> when it is a page of this site ("/..."), otherwise "/". Refuses absolute and
    /// protocol-relative addresses ("//host", "/\host"), control characters, backslashes, the account endpoints, the
    /// sign-in page itself, and the camera streams (never a page to go back to).
    /// </summary>
    public static string GetSafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var candidate = returnUrl.Trim();
        if (candidate.Length > MaximumReturnUrlLength
            || candidate[0] != '/'
            || (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
            || candidate.Any(c => char.IsControl(c) || c == '\\'))
        {
            return "/";
        }

        var path = candidate.Split('?', '#')[0];
        return path.Equals("/account", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/account/", StringComparison.OrdinalIgnoreCase) && !path.Equals(ChangePasswordPath, StringComparison.OrdinalIgnoreCase))
            || path.Equals(WebAuthentication.SignInPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(WebAuthentication.SignInPath + "/", StringComparison.OrdinalIgnoreCase)
            || path.Equals(WebCameraEndpoint.PathPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(WebCameraEndpoint.PathPrefix + "/", StringComparison.OrdinalIgnoreCase)
            ? "/"
            : candidate;
    }

    /// <summary>The page where a signed-in person changes their password (the one account page that is a page).</summary>
    public const string ChangePasswordPath = "/account/password";

    /// <summary>The sign-in page with a message, keeping the page to go back to.</summary>
    public static string BuildSignInUrl(string? message, string safeReturnUrl)
    {
        var query = new Dictionary<string, string?>();
        if (message is not null)
        {
            query[WebAuthentication.MessageQuery] = message;
        }

        if (safeReturnUrl != "/")
        {
            query[WebAuthentication.ReturnUrlField] = safeReturnUrl;
        }

        return QueryHelpers.AddQueryString(WebAuthentication.SignInPath, query);
    }

    private static async Task<IResult> SignInAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        WebSignInLimiter limiter,
        WebSessionStore store,
        WebStopPass stopPass,
        RoofControllerConnector connector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebAccountEndpoints).FullName!);
        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.HasFormContentType)
        {
            return Results.Redirect(BuildSignInUrl(SignInMessages.Expired, "/"));
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var returnUrl = GetSafeReturnUrl(form[WebAuthentication.ReturnUrlField].ToString());
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            logger.LogWarning("Web sign-in refused from {RemoteIp}: missing or stale antiforgery token", remote);
            return Results.Redirect(BuildSignInUrl(SignInMessages.Expired, returnUrl));
        }

        var name = form[WebAuthentication.NameField].ToString().Trim();
        var password = form[WebAuthentication.PasswordField].ToString();
        if (name.Length == 0 || password.Length == 0)
        {
            return Results.Redirect(BuildSignInUrl(SignInMessages.Missing, returnUrl));
        }

        if (!limiter.TryAcquire(remote))
        {
            logger.LogWarning("Web sign-in refused from {RemoteIp}: more than the allowed sign-ins a minute", remote);
            return Results.Redirect(BuildSignInUrl(SignInMessages.TooMany, returnUrl));
        }

        RoofSessionCredential credential;
        try
        {
            using var client = connector.Create(credential: null, WebSession.RequestTimeout);
            credential = await client.Auth.SignInAsync(name, password, http.RequestAborted);
        }
        catch (RoofApiException ex)
        {
            var message = ex.Code switch
            {
                RoofControllerErrorCode.SignInFailed => SignInMessages.Failed,
                RoofControllerErrorCode.SignInLockedOut => SignInMessages.LockedOut,
                RoofControllerErrorCode.SignInBusy => SignInMessages.Busy,
                _ when ex.StatusCode == HttpStatusCode.TooManyRequests => SignInMessages.Busy,
                _ when ex.StatusCode == HttpStatusCode.Unauthorized => SignInMessages.Failed,
                _ when (int)ex.StatusCode >= 500 => SignInMessages.Unreachable,
                _ => SignInMessages.Refused
            };
            logger.LogWarning("Web sign-in for {Name} from {RemoteIp} refused by the controller: {Refusal}", name, remote, ex.Message);
            return Results.Redirect(BuildSignInUrl(message, returnUrl));
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or RoofProtocolException)
        {
            logger.LogWarning("Web sign-in for {Name} from {RemoteIp} failed: {Failure}", name, remote, RoofText.DescribeFailure(ex));
            return Results.Redirect(BuildSignInUrl(SignInMessages.Unreachable, returnUrl));
        }

        WebSession session;
        try
        {
            session = store.Open(credential);
        }
        catch (ArgumentException)
        {
            // A session the web UI cannot use (a role it does not know): ended at once, so it is not left open.
            logger.LogWarning("Web sign-in for {Name} from {RemoteIp} refused: the controller gave role {Role}, which the web UI does not know", name, remote, credential.Role);
            await EndQuietlyAsync(connector, credential, logger);
            return Results.Redirect(BuildSignInUrl(SignInMessages.Refused, returnUrl));
        }

        await http.SignInAsync(WebAuthentication.Scheme, WebAuthentication.CreatePrincipal(session), WebAuthentication.CreateProperties(session));
        stopPass.Issue(http, session);
        logger.LogInformation("Web sign-in for {Name} ({Role}) from {RemoteIp}", session.Name, session.Role, remote);
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, WebSessionStore store, WebStopPass stopPass, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebAccountEndpoints).FullName!);
        var result = await http.AuthenticateAsync(WebAuthentication.Scheme);

        // Signing out also ends Stop from this browser (the Stop pass); a session that ended by itself keeps it. The answer
        // goes before the session ends: the person's live pages, this one too, go to the sign-in page as soon as it ends,
        // and a browser that leaves this page before the answer arrives drops it, with the removal of the Stop pass.
        await http.SignOutAsync(WebAuthentication.Scheme);
        stopPass.Remove(http);
        http.Response.Redirect(BuildSignInUrl(SignInMessages.SignedOut, "/"));
        http.Response.ContentLength = 0;
        await http.Response.StartAsync();
        await http.Response.Body.FlushAsync();

        if (store.TryGet(WebAuthentication.GetSessionId(result.Principal), out var session))
        {
            try
            {
                // Not the request's token: the browser may have gone on to the sign-in page already.
                await session.SignOutAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is RoofApiException or TimeoutException or HttpRequestException or RoofProtocolException or OperationCanceledException)
            {
                // The web UI forgets the session either way; the controller ends it when it expires.
                logger.LogWarning("Web sign-out for {Name}: the controller did not end the session ({Failure})", session.Name, RoofText.DescribeFailure(ex));
            }

            logger.LogInformation("Web sign-out for {Name} from {RemoteIp}", session.Name, http.Connection.RemoteIpAddress);
        }

        return Results.Empty;
    }

    private static async Task EndQuietlyAsync(RoofControllerConnector connector, RoofSessionCredential credential, ILogger logger)
    {
        try
        {
            using var client = connector.Create(credential, WebSession.RequestTimeout);
            await client.Auth.SignOutAsync();
        }
        catch (Exception ex) when (ex is RoofApiException or TimeoutException or HttpRequestException or RoofProtocolException)
        {
            logger.LogWarning("The session for {Name} could not be ended at the controller ({Failure})", credential.Name, RoofText.DescribeFailure(ex));
        }
    }
}
