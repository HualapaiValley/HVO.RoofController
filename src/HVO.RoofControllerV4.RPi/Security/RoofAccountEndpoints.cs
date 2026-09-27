using System;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Console sign-in/sign-out endpoints. The <c>/login</c> page (static server-rendered, owned by the UI) posts a plain
/// form to <c>POST /account/login</c> with fields <c>accessKey</c>, <c>returnUrl</c> and the antiforgery token.
/// </summary>
public static class RoofAccountEndpoints
{
    /// <summary>Query value of <c>error</c> on <c>/login</c> after a wrong access key.</summary>
    public const string InvalidKeyError = "1";

    /// <summary>Query value of <c>error</c> on <c>/login</c> when the form's antiforgery token was missing or stale.</summary>
    public const string ExpiredFormError = "2";

    /// <summary>Fixed delay before answering a failed login, to slow down guessing.</summary>
    internal static TimeSpan FailedLoginDelay { get; set; } = TimeSpan.FromSeconds(1);

    private const int MaximumReturnUrlLength = 2048;

    public static IEndpointRouteBuilder MapRoofAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Antiforgery is validated explicitly in LoginAsync so a stale form redirects back to /login instead of a bare 400.
        endpoints.MapPost(RoofControllerSecurityDefaults.LoginPostPath, LoginAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

        // Logout only removes the caller's own cookie. The cookie is SameSite=Strict and the Origin check covers
        // POST /account/*, so no antiforgery token is required here.
        endpoints.MapPost(RoofControllerSecurityDefaults.LogoutPostPath, LogoutAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Returns <paramref name="returnUrl"/> when it is a safe local path ("/..."), otherwise "/". Rejects absolute and
    /// protocol-relative URLs ("//host", "/\host"), control characters and backslashes, and the account endpoints.
    /// </summary>
    public static string GetSafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var candidate = returnUrl.Trim();
        if (candidate.Length > MaximumReturnUrlLength || candidate[0] != '/')
        {
            return "/";
        }

        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return "/";
        }

        foreach (var c in candidate)
        {
            if (char.IsControl(c) || c == '\\')
            {
                return "/";
            }
        }

        if (candidate.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("/account", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return candidate;
    }

    internal static string BuildLoginRedirect(string error, string safeReturnUrl)
    {
        var url = $"{RoofControllerSecurityDefaults.LoginPath}?error={error}";
        return safeReturnUrl == "/"
            ? url
            : $"{url}&{RoofControllerSecurityDefaults.ReturnUrlFormField}={Uri.EscapeDataString(safeReturnUrl)}";
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        RoofApiKeyStore keyStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(RoofAccountEndpoints).FullName!);
        if (!http.Request.HasFormContentType)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Form post required",
                detail: "Sign in by posting the login form.");
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        var returnUrl = GetSafeReturnUrl(form[RoofControllerSecurityDefaults.ReturnUrlFormField].ToString());

        if (!await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
        {
            logger.LogWarning("Console login refused from {RemoteIp}: missing or invalid antiforgery token", http.Connection.RemoteIpAddress);
            return Results.Redirect(BuildLoginRedirect(ExpiredFormError, returnUrl));
        }

        var accessKey = form[RoofControllerSecurityDefaults.AccessKeyFormField].ToString();
        if (!keyStore.TryValidate(accessKey, out var identity))
        {
            logger.LogWarning(
                "Console login failed from {RemoteIp} ({Reason})",
                http.Connection.RemoteIpAddress,
                keyStore.HasKeys ? "unknown access key" : "no API keys are configured");
            try
            {
                await Task.Delay(FailedLoginDelay, http.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client went away; the redirect below is simply not delivered.
            }

            return Results.Redirect(BuildLoginRedirect(InvalidKeyError, returnUrl));
        }

        var principal = RoofPrincipalFactory.Create(identity, RoofControllerSecurityDefaults.CookieScheme);
        await http.SignInAsync(
            RoofControllerSecurityDefaults.CookieScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true }).ConfigureAwait(false);

        logger.LogInformation(
            "Console login succeeded for key {KeyName} ({Role}) from {RemoteIp}",
            identity.Name,
            identity.Role,
            http.Connection.RemoteIpAddress);

        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, ILoggerFactory loggerFactory)
    {
        var caller = RoofPrincipalFactory.DescribeCaller(
            (await http.AuthenticateAsync(RoofControllerSecurityDefaults.CookieScheme).ConfigureAwait(false)).Principal);
        await http.SignOutAsync(RoofControllerSecurityDefaults.CookieScheme).ConfigureAwait(false);
        loggerFactory.CreateLogger(typeof(RoofAccountEndpoints).FullName!)
            .LogInformation("Console logout for {KeyName} from {RemoteIp}", caller, http.Connection.RemoteIpAddress);
        return Results.LocalRedirect(RoofControllerSecurityDefaults.LoginPath);
    }
}
