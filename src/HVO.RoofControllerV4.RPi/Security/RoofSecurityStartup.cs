using System;
using System.Collections.Generic;
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

    // Listener settings as Kestrel reads them. The ASPNETCORE_ environment provider maps ASPNETCORE_URLS to urls and
    // ASPNETCORE_HTTP_PORTS/ASPNETCORE_HTTPS_PORTS to http_ports/https_ports (configuration keys ignore case).
    private const string UrlsKey = "urls";
    private const string HttpPortsKey = "http_ports";
    private const string HttpsPortsKey = "https_ports";
    private const string PreferHostingUrlsKey = "preferHostingUrls";
    private const string EndpointsSection = "Kestrel:Endpoints";
    private const string DefaultAddress = "http://localhost:5000";
    private const string HttpPortsName = "ASPNETCORE_HTTP_PORTS";
    private const string HttpsPortsName = "ASPNETCORE_HTTPS_PORTS";

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
                "No usable API keys are configured: every protected endpoint (roof commands, status, configuration, /health and " +
                "camera) will answer 401 to an API key. Provision at least one key per role, for example with environment variables " +
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
                "RoofControllerSecurity:RequireHttps is explicitly disabled in {Environment}: API keys, passwords and PINs " +
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

    /// <summary>
    /// True when Kestrel will listen on HTTPS: at least one of the listeners <see cref="ResolveListeners"/> resolves is
    /// an https URL. Settings Kestrel ignores (for example HTTPS_PORTS while ASPNETCORE_URLS is set) do not count.
    /// </summary>
    public static bool IsHttpsConfigured(IConfiguration configuration) => ResolveListeners(configuration).HasHttps;

    /// <summary>
    /// Resolves the listeners Kestrel will bind from configuration, with its precedence: <c>Kestrel:Endpoints</c> when
    /// any endpoint is configured (unless <c>preferHostingUrls</c> is true and server URLs are set), otherwise the
    /// server URLs, which are <c>urls</c> (ASPNETCORE_URLS) when set and otherwise <c>http_ports</c>/<c>https_ports</c>
    /// (ASPNETCORE_HTTP_PORTS/ASPNETCORE_HTTPS_PORTS), otherwise Kestrel's default http://localhost:5000.
    /// </summary>
    /// <remarks>
    /// Kestrel reads only the <c>urls</c>, <c>http_ports</c> and <c>https_ports</c> keys (the ASPNETCORE_ environment
    /// provider strips the prefix). A raw <c>ASPNETCORE_URLS</c> key, which the unprefixed environment provider or a
    /// secret file named that way produces, is not read by Kestrel and is ignored here too.
    /// </remarks>
    internal static ResolvedListeners ResolveListeners(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Server addresses, as GenericWebHostService builds them: urls wins over the port settings.
        var urls = configuration[UrlsKey];
        var httpPorts = configuration[HttpPortsKey];
        var httpsPorts = configuration[HttpsPortsKey];
        var serverAddressSource = string.IsNullOrEmpty(urls) ? ListenerSource.Ports : ListenerSource.Urls;
        var serverAddresses = serverAddressSource == ListenerSource.Urls
            ? SplitList(urls)
            : ExpandPorts(httpPorts, "http").Concat(ExpandPorts(httpsPorts, "https")).ToList();

        // Endpoints from configuration; Kestrel refuses to start when one has no Url, so only those with one count.
        var endpoints = configuration.GetSection(EndpointsSection).GetChildren()
            .Select(endpoint => endpoint["Url"])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .ToList();

        var preferHostingUrls = configuration[PreferHostingUrlsKey] is { } prefer
            && (string.Equals(prefer, "true", StringComparison.OrdinalIgnoreCase) || prefer == "1");

        ListenerSource source;
        IReadOnlyList<string> addresses;
        if (serverAddresses.Count > 0 && (endpoints.Count == 0 || preferHostingUrls))
        {
            (source, addresses) = (serverAddressSource, serverAddresses);
        }
        else if (endpoints.Count > 0)
        {
            (source, addresses) = (ListenerSource.KestrelEndpoints, endpoints);
        }
        else
        {
            (source, addresses) = (ListenerSource.Default, [DefaultAddress]);
        }

        var ignored = new List<string>();
        if (!string.IsNullOrEmpty(urls) && source != ListenerSource.Urls)
        {
            ignored.Add(DescribeSource(ListenerSource.Urls));
        }

        if (!string.IsNullOrEmpty(httpPorts) && source != ListenerSource.Ports)
        {
            ignored.Add(HttpPortsName);
        }

        if (!string.IsNullOrEmpty(httpsPorts) && source != ListenerSource.Ports)
        {
            ignored.Add(HttpsPortsName);
        }

        if (endpoints.Count > 0 && source != ListenerSource.KestrelEndpoints)
        {
            ignored.Add(DescribeSource(ListenerSource.KestrelEndpoints));
        }

        return new ResolvedListeners(source, addresses, ignored);

        static List<string> SplitList(string? value)
            => (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        static IEnumerable<string> ExpandPorts(string? ports, string scheme)
            => SplitList(ports).Select(port => $"{scheme}://*:{port}");
    }

    /// <summary>Operator-facing name of a listener source: the environment variable a container sets for it.</summary>
    internal static string DescribeSource(ListenerSource source) => source switch
    {
        ListenerSource.KestrelEndpoints => "Kestrel:Endpoints",
        ListenerSource.Urls => "ASPNETCORE_URLS",
        ListenerSource.Ports => $"{HttpPortsName}/{HttpsPortsName}",
        _ => "Kestrel's default"
    };

    /// <summary>True for an https listener URL (Kestrel decides by the scheme alone).</summary>
    internal static bool IsHttpsAddress(string? address)
        => address is not null && address.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where Kestrel takes its listeners from; it uses exactly one source.</summary>
internal enum ListenerSource
{
    /// <summary>The <c>Kestrel:Endpoints</c> configuration section.</summary>
    KestrelEndpoints,

    /// <summary>The <c>urls</c> setting (ASPNETCORE_URLS).</summary>
    Urls,

    /// <summary>The <c>http_ports</c>/<c>https_ports</c> settings (ASPNETCORE_HTTP_PORTS/ASPNETCORE_HTTPS_PORTS).</summary>
    Ports,

    /// <summary>Nothing configured: Kestrel listens on http://localhost:5000.</summary>
    Default
}

/// <summary>The listeners Kestrel will bind, as <see cref="RoofSecurityStartup.ResolveListeners"/> resolved them.</summary>
/// <param name="Source">The configuration the listeners come from.</param>
/// <param name="Addresses">The listener URLs (ports expanded to http://*:port and https://*:port, as Kestrel does).</param>
/// <param name="Ignored">Listener settings that are present but overridden by <paramref name="Source"/>.</param>
internal sealed record ResolvedListeners(ListenerSource Source, IReadOnlyList<string> Addresses, IReadOnlyList<string> Ignored)
{
    /// <summary>True when at least one listener is an https URL.</summary>
    public bool HasHttps => Addresses.Any(RoofSecurityStartup.IsHttpsAddress);

    /// <summary>For reports, e.g. "http://+:8080 (from ASPNETCORE_URLS; ignored: ASPNETCORE_HTTPS_PORTS)".</summary>
    public override string ToString()
        => $"{string.Join(", ", Addresses)} (from {RoofSecurityStartup.DescribeSource(Source)}"
            + (Ignored.Count > 0 ? $"; ignored: {string.Join(", ", Ignored)})" : ")");
}
