using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Names shared by the HTTP API, the Blazor console and the authentication setup in <c>Program</c>.
/// </summary>
public static class RoofControllerSecurityDefaults
{
    /// <summary>Header-based API key scheme. The only scheme accepted by <c>api/*</c> routes, so they never use cookies.</summary>
    public const string ApiKeyScheme = RoofControllerApiContract.ApiKeyScheme;

    /// <summary>Cookie scheme used only by the Blazor console after an access-key login.</summary>
    public const string CookieScheme = "RoofConsoleCookie";

    public const string LoginPath = "/login";
    public const string LoginPostPath = "/account/login";
    public const string LogoutPostPath = "/account/logout";
    public const string AccessDeniedPath = "/access-denied";

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
