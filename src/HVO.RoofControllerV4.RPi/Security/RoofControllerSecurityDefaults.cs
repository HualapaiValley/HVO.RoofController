using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Names shared by the HTTP API, the Blazor console and the authentication setup in <c>Program</c>.
/// </summary>
public static class RoofControllerSecurityDefaults
{
    /// <summary>Header-based API key scheme (<c>X-Api-Key</c>).</summary>
    public const string ApiKeyScheme = RoofControllerApiContract.ApiKeyScheme;

    /// <summary>Session scheme: <c>Authorization: Bearer &lt;token&gt;</c> from a sign-in.</summary>
    public const string SessionScheme = "RoofSession";

    /// <summary>
    /// The scheme <c>api/*</c> routes, the status hub and OpenAPI accept: a session when the request carries
    /// <c>Authorization: Bearer</c>, otherwise an API key. Never a cookie.
    /// </summary>
    public const string ApiScheme = "RoofApi";

    /// <summary>
    /// The scheme Stop accepts: like <see cref="ApiScheme"/>, except that when the session token is refused (ended, idle
    /// or expired) an API key sent with it is tried, so a kiosk or UI can always stop with its own key.
    /// </summary>
    public const string StopScheme = "RoofStop";

    /// <summary>Cookie scheme used only by the Blazor console after an access-key login.</summary>
    public const string CookieScheme = "RoofConsoleCookie";

    public const string LoginPath = "/login";
    public const string LoginPostPath = "/account/login";
    public const string LogoutPostPath = "/account/logout";
    public const string AccessDeniedPath = "/access-denied";

    /// <summary>Console Stop that does not need the Blazor circuit (used by the reconnect dialog).</summary>
    public const string ConsoleStopPath = "/console/stop";

    /// <summary>Form field names posted to <see cref="LoginPostPath"/>.</summary>
    public const string AccessKeyFormField = "accessKey";
    public const string ReturnUrlFormField = "returnUrl";

    /// <summary>Status, camera and health details. Granted to every role.</summary>
    public const string ViewerPolicy = "RoofViewerPolicy";

    /// <summary>Open, Close, ClearFault and lease renewal. Granted to operators and admins.</summary>
    public const string OperatorPolicy = "RoofOperatorPolicy";

    /// <summary>Configuration, diagnostics, logs and OpenAPI. Admins only.</summary>
    public const string AdminPolicy = "RoofAdminPolicy";

    /// <summary>
    /// Stop is always the safe direction, so any authenticated role may send it, and anonymous callers may too
    /// when <c>RoofControllerSecurity:AllowAnonymousStop</c> is set.
    /// </summary>
    public const string StopPolicy = "RoofStopPolicy";
}
