using System;
using System.Linq;
using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Startup diagnostics for the security configuration. Never logs key values.</summary>
public static class RoofSecurityStartup
{
    /// <summary>Logger category used for the startup security report.</summary>
    public const string LoggerCategory = "HVO.RoofControllerV4.RPi.Security";

    /// <summary>
    /// Logs the key inventory (names and roles only), a Critical entry with provisioning instructions when no key is
    /// usable, and warnings for weakened settings (RequireHttps off in Production, anonymous Stop, AllowedHosts "*").
    /// </summary>
    public static void ReportSecurityPosture(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        var options = services.GetRequiredService<IOptionsMonitor<RoofControllerSecurityOptions>>().CurrentValue;
        var keyStore = services.GetRequiredService<RoofApiKeyStore>();

        foreach (var problem in keyStore.ConfigurationProblems)
        {
            logger.LogError("API key configuration problem: {Problem}", problem);
        }

        if (!keyStore.HasKeys)
        {
            logger.LogCritical(
                "No usable API keys are configured: every protected endpoint (roof commands, status, configuration, /health, camera, " +
                "and the web console login) will answer 401. Provision at least one key per role, for example with environment variables " +
                "RoofControllerSecurity__ApiKeys__0__Name=operator, RoofControllerSecurity__ApiKeys__0__Role=RoofOperator and " +
                "RoofControllerSecurity__ApiKeys__0__Key=<random value of at least {MinimumKeyLength} characters>, or as Docker secrets " +
                "with the same names under /run/secrets. See docs/security.md.",
                RoofControllerSecurityOptions.MinimumKeyLength);
        }
        else
        {
            logger.LogInformation("{KeyCount} API key(s) configured.", keyStore.KeyCount);
        }

        var httpsRequired = RequireHttpsMiddleware.IsHttpsRequired(options, environment);
        if (!environment.IsDevelopment() && options.RequireHttps == false)
        {
            logger.LogWarning(
                "RoofControllerSecurity:RequireHttps is explicitly disabled in {Environment}: API keys and the console access key " +
                "can be read by anyone on the network path. Enable HTTPS (docs/deployment.md).",
                environment.EnvironmentName);
        }

        if (options.AllowAnonymousStop)
        {
            logger.LogWarning("RoofControllerSecurity:AllowAnonymousStop is enabled: anyone who can reach the controller can stop the roof.");
        }

        var allowedHosts = configuration["AllowedHosts"];
        if (environment.IsProduction() && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*"))
        {
            logger.LogWarning(
                "AllowedHosts is '*' in Production. Authentication protects the API, but set AllowedHosts to the controller's " +
                "host names (e.g. \"roof-pi;roof-pi.local;localhost\") to refuse DNS-rebinding requests outright.");
        }

        if (httpsRequired)
        {
            var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 }
                    && !addresses.Any(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogWarning(
                        "HTTPS is required but the server is not listening on HTTPS ({Addresses}). Remote clients will receive 403 " +
                        "https_required; only loopback and /health/live, /health/ready work. Configure a Kestrel certificate " +
                        "(docs/deployment.md) or, on a trusted network only, set RoofControllerSecurity__RequireHttps=false.",
                        string.Join(", ", addresses));
                }
            });
        }
    }

    /// <summary>True when configuration declares an HTTPS listener (urls, https_ports or a Kestrel https endpoint).</summary>
    public static bool IsHttpsConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (ContainsHttps(configuration["urls"]) || ContainsHttps(configuration["ASPNETCORE_URLS"]))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuration["https_ports"]) || !string.IsNullOrWhiteSpace(configuration["HTTPS_PORTS"]))
        {
            return true;
        }

        return configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Any(endpoint => ContainsHttps(endpoint["Url"]));

        static bool ContainsHttps(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }
}
