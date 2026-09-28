using System;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Registers API key + console cookie authentication and the roof authorization policies.</summary>
public static class RoofSecurityServiceCollectionExtensions
{
    /// <summary>Both schemes, for endpoints usable from the console and from API clients (<c>/health</c>, camera).</summary>
    public const string ApiKeyOrCookieSchemes = RoofControllerSecurityDefaults.ApiKeyScheme + "," + RoofControllerSecurityDefaults.CookieScheme;

    /// <summary>Name of the console cookie.</summary>
    public const string ConsoleCookieName = "hvo.roof.console";

    private static readonly PathString[] NonBrowserPrefixes =
    [
        new("/api"),
        new("/health"),
        new("/openapi"),
        new("/_blazor"),
        new("/_framework"),
        new("/account"),
        new("/hubs")
    ];

    public static IServiceCollection AddRoofControllerSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bound lazily so configuration added by the host after this call (tests, key-per-file secrets) is honoured.
        services.AddOptions<RoofControllerSecurityOptions>()
            .Bind(configuration.GetSection(RoofControllerSecurityOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RoofApiKeyStore>();
        services.AddSingleton<IAuthorizationHandler, RoofStopAuthorizationHandler>();

        services.AddAuthentication(options =>
            {
                // The console (Razor components, /health from a browser) uses the cookie. api/* endpoints name the
                // ApiKey scheme explicitly, so a console cookie is never accepted there.
                options.DefaultScheme = RoofControllerSecurityDefaults.CookieScheme;
                options.DefaultChallengeScheme = RoofControllerSecurityDefaults.CookieScheme;
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                RoofControllerSecurityDefaults.ApiKeyScheme,
                displayName: "API key (X-Api-Key header)",
                _ => { })
            .AddCookie(RoofControllerSecurityDefaults.CookieScheme, options =>
            {
                options.Cookie.Name = ConsoleCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = RoofControllerSecurityDefaults.LoginPath;
                options.LogoutPath = RoofControllerSecurityDefaults.LogoutPostPath;
                options.AccessDeniedPath = RoofControllerSecurityDefaults.AccessDeniedPath;
                options.ReturnUrlParameter = RoofControllerSecurityDefaults.ReturnUrlFormField;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden),
                    OnValidatePrincipal = ValidateConsolePrincipalAsync
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(RoofControllerSecurityDefaults.ViewerPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoofControllerApiContract.ViewerRole))
            .AddPolicy(RoofControllerSecurityDefaults.OperatorPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoofControllerApiContract.OperatorRole))
            .AddPolicy(RoofControllerSecurityDefaults.AdminPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoofControllerApiContract.AdminRole))
            .AddPolicy(RoofControllerSecurityDefaults.StopPolicy, policy => policy
                .AddRequirements(new RoofStopRequirement()));

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RoofConsoleAuthenticationStateProvider>();

        return services;
    }

    /// <summary>
    /// Browser page navigations (GET/HEAD outside the API, health, hub and framework paths) are redirected to the
    /// login/access-denied pages; everything else gets a plain status code.
    /// </summary>
    public static bool IsBrowserNavigation(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        foreach (var prefix in NonBrowserPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (IsBrowserNavigation(context.Request))
        {
            context.Response.Redirect(context.RedirectUri);
        }
        else
        {
            context.Response.StatusCode = statusCode;
        }

        return Task.CompletedTask;
    }

    private static async Task ValidateConsolePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var keyStore = context.HttpContext.RequestServices.GetRequiredService<RoofApiKeyStore>();
        if (RoofConsoleAuthenticationStateProvider.IsStillValid(keyStore, context.Principal))
        {
            return;
        }

        // The key that signed this session in was removed, rotated or re-roled: end the session.
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(RoofControllerSecurityDefaults.CookieScheme).ConfigureAwait(false);
    }
}
