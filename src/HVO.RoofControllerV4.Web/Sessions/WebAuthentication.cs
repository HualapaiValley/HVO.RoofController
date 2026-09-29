using System.Security.Claims;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// How people sign in to the web UI: with their name and password at the controller (<c>POST Auth/Session</c>). The web
/// UI keeps the controller's session in an HttpOnly, SameSite=Strict cookie that lasts no longer than the session, and
/// sends it to the controller for everything the person does. The controller decides what they may do.
/// </summary>
public static class WebAuthentication
{
    /// <summary>The authentication scheme.</summary>
    public const string Scheme = "RoofWeb";

    /// <summary>The sign-in cookie.</summary>
    public const string CookieName = "hvo.roof.web";

    /// <summary>The claim naming the controller session.</summary>
    public const string SessionIdClaimType = "hvo:roof:web:session";

    /// <summary>The sign-in page (static, needs no live connection).</summary>
    public const string SignInPath = "/signin";

    /// <summary>The page a signed-in person sees when their role does not permit the page they asked for.</summary>
    public const string AccessDeniedPath = "/denied";

    /// <summary>The sign-in form posts here.</summary>
    public const string SignInPostPath = "/account/signin";

    /// <summary>The sign-out form posts here.</summary>
    public const string SignOutPostPath = "/account/signout";

    /// <summary>The Stop form posts here when a page has no live connection.</summary>
    public const string StopPostPath = "/stop";

    public const string ReturnUrlField = "returnUrl";

    public const string NameField = "name";

    public const string PasswordField = "password";

    /// <summary>The query field on the sign-in page naming why the person is there (see <see cref="SignInMessages"/>).</summary>
    public const string MessageQuery = "message";

    // The token is kept in the cookie's (encrypted) properties, not in a claim, so it never reaches a page.
    private const string TokenItem = ".hvo.roof.token";

    // Set when a request's cookie named a session that ended, so the sign-in page can say so.
    private const string SessionEndedItem = "hvo.roof.session-ended";

    /// <summary>Adds cookie sign-in, data protection and the pages' view of the signed-in person.</summary>
    public static IServiceCollection AddRoofWebAuthentication(this IServiceCollection services, RoofWebOptions options)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("HVO.RoofControllerV4.Web");
        if (!string.IsNullOrWhiteSpace(options.DataProtectionPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionPath));
        }

        services.AddSingleton<WebSessionStore>();
        services.AddSingleton<WebSignInLimiter>();
        services.AddAuthentication(Scheme)
            .AddCookie(Scheme, cookie =>
            {
                cookie.Cookie.Name = CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = false;
                cookie.LoginPath = SignInPath;
                cookie.AccessDeniedPath = AccessDeniedPath;
                cookie.ReturnUrlParameter = ReturnUrlField;
                cookie.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ValidatePrincipalAsync,
                    OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden),
                };
            });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, WebAuthenticationStateProvider>();
        services.AddScoped<WebSessionAccessor>();
        return services;
    }

    /// <summary>The person, as the pages see them: their name, their roles (each role grants those below it) and their session.</summary>
    public static ClaimsPrincipal CreatePrincipal(WebSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, session.Name),
            new(SessionIdClaimType, session.Id),
        };
        claims.AddRange(WebRoles.Implied(session.Role).Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>The cookie's lifetime and contents: it ends with the controller session, and it is never refreshed.</summary>
    public static AuthenticationProperties CreateProperties(WebSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            ExpiresUtc = session.ExpiresUtc,
        };
        properties.Items[TokenItem] = session.Credential.Session.Token;
        return properties;
    }

    /// <summary>The controller session a cookie holds, or null when it holds none (or is not the web UI's).</summary>
    public static WebTicket? ReadTicket(ClaimsPrincipal? principal, AuthenticationProperties? properties)
    {
        var sessionId = principal?.FindFirstValue(SessionIdClaimType);
        var name = principal?.Identity?.Name;
        var role = principal is null ? null : WebRoles.Normalize(principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).FirstOrDefault());
        string? token = null;
        properties?.Items.TryGetValue(TokenItem, out token);
        return sessionId is null || name is null || role is null || string.IsNullOrEmpty(token) || properties?.ExpiresUtc is not { } expires
            ? null
            : new WebTicket(sessionId, token, name, role, expires);
    }

    /// <summary>The controller session's identifier, from the signed-in person's claims.</summary>
    public static string? GetSessionId(ClaimsPrincipal? principal) => principal?.FindFirstValue(SessionIdClaimType);

    /// <summary>True when this request's cookie named a session that has ended.</summary>
    public static bool SessionEnded(HttpContext? context) => context?.Items.ContainsKey(SessionEndedItem) == true;

    /// <summary>
    /// Every request with a cookie: the session it names must still be open (a restarted web UI picks it up again from
    /// the cookie). Otherwise the cookie is removed and the request goes on signed out. Stop is the exception: it goes
    /// on with the cookie, so it can still be sent for the person with the web UI's Stop key.
    /// </summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var ticket = ReadTicket(context.Principal, context.Properties);
        var store = context.HttpContext.RequestServices.GetRequiredService<WebSessionStore>();
        if (ticket is not null && store.TryResume(ticket, out _))
        {
            return;
        }

        if (ticket is not null && context.Request.Path.Equals(StopPostPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        context.RejectPrincipal();
        context.HttpContext.Items[SessionEndedItem] = true;
        await context.HttpContext.SignOutAsync(Scheme);
    }

    // Pages (GET or HEAD) are sent to the sign-in page, which says so when the person's session had ended; a form post,
    // the live connection or a camera stream gets the status code.
    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        var request = context.Request;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && !request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            && !request.Path.StartsWithSegments(WebCameraEndpoint.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var target = context.RedirectUri;
            if (statusCode == StatusCodes.Status401Unauthorized && SessionEnded(context.HttpContext))
            {
                target = QueryHelpers.AddQueryString(target, MessageQuery, SignInMessages.Ended);
            }

            context.Response.Redirect(target);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
