using System;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Registers API key, session and console cookie authentication, the identity store (people, sessions, managed keys)
/// and the roof authorization policies.
/// </summary>
public static class RoofSecurityServiceCollectionExtensions
{
    /// <summary>API key, session or console cookie, for endpoints usable from the console and from API clients (<c>/health</c>, camera).</summary>
    public const string ApiKeyOrCookieSchemes = RoofControllerSecurityDefaults.ApiScheme + "," + RoofControllerSecurityDefaults.CookieScheme;

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

        services.AddOptions<RoofIdentityOptions>()
            .Bind(configuration.GetSection(RoofIdentityOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoofIdentityOptions>, RoofIdentityOptionsValidator>();
        services.AddOptions<PasswordHasherOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RoofIdentityStore>();
        services.AddSingleton<RoofSecretHasher>();
        services.AddSingleton<RoofSignInLockout>();
        services.AddSingleton<RoofSignInService>();
        services.AddSingleton(provider => new RoofApiKeyStore(
            provider.GetRequiredService<IOptionsMonitor<RoofControllerSecurityOptions>>(),
            provider.GetRequiredService<ILogger<RoofApiKeyStore>>(),
            provider.GetRequiredService<RoofIdentityStore>()));
        services.AddSingleton(provider => new RoofCredentialValidator(
            provider.GetRequiredService<RoofApiKeyStore>(),
            provider.GetRequiredService<RoofIdentityStore>()));
        services.AddSingleton<IAuthorizationHandler, RoofStopAuthorizationHandler>();

        services.AddAuthentication(options =>
            {
                // The console (Razor components, /health from a browser) uses the cookie. api/* endpoints name the
                // RoofApi scheme (a session or an API key) explicitly, so a console cookie is never accepted there.
                options.DefaultScheme = RoofControllerSecurityDefaults.CookieScheme;
                options.DefaultChallengeScheme = RoofControllerSecurityDefaults.CookieScheme;
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                RoofControllerSecurityDefaults.ApiKeyScheme,
                displayName: "API key (X-Api-Key header)",
                _ => { })
            .AddScheme<RoofSessionAuthenticationOptions, RoofSessionAuthenticationHandler>(
                RoofControllerSecurityDefaults.SessionScheme,
                displayName: "Session (Authorization: Bearer)",
                _ => { })
            .AddPolicyScheme(RoofControllerSecurityDefaults.ApiScheme, displayName: "Session or API key", options =>
            {
                options.ForwardDefaultSelector = context => RoofSessionAuthenticationHandler.HasBearerToken(context.Request)
                    ? RoofControllerSecurityDefaults.SessionScheme
                    : RoofControllerSecurityDefaults.ApiKeyScheme;
            })
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
        var validator = context.HttpContext.RequestServices.GetRequiredService<RoofCredentialValidator>();
        if (validator.IsStillValid(context.Principal))
        {
            return;
        }

        // The key that signed this session in was removed, rotated or re-roled: end the session.
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(RoofControllerSecurityDefaults.CookieScheme).ConfigureAwait(false);
    }
}

/// <summary>Reports every problem with <see cref="RoofIdentityOptions"/> at startup.</summary>
internal sealed class RoofIdentityOptionsValidator : IValidateOptions<RoofIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, RoofIdentityOptions options)
    {
        var problems = options.Validate();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
