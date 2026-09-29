using System;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Registers API key and session authentication, the Stop credential, the identity store (people, sessions, managed
/// keys) and the roof authorization policies.
/// </summary>
public static class RoofSecurityServiceCollectionExtensions
{
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
        services.AddRoofSignInRateLimiting();
        services.AddSingleton(provider => new RoofApiKeyStore(
            provider.GetRequiredService<IOptionsMonitor<RoofControllerSecurityOptions>>(),
            provider.GetRequiredService<ILogger<RoofApiKeyStore>>(),
            provider.GetRequiredService<RoofIdentityStore>()));
        services.AddSingleton(provider => new RoofCredentialValidator(
            provider.GetRequiredService<RoofApiKeyStore>(),
            provider.GetRequiredService<RoofIdentityStore>()));
        services.AddSingleton<IAuthorizationHandler, RoofStopAuthorizationHandler>();

        // Every endpoint names its scheme; the default is the API's (a session or an API key), so an endpoint that does
        // not name one accepts nothing else.
        services.AddAuthentication(RoofControllerSecurityDefaults.ApiScheme)
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
            .AddScheme<AuthenticationSchemeOptions, RoofStopAuthenticationHandler>(
                RoofControllerSecurityDefaults.StopScheme,
                displayName: "Session, or the API key sent with it (Stop)",
                _ => { });

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

        return services;
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
