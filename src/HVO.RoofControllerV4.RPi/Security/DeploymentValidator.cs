using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using HVO.Enterprise.Telemetry.Configuration;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Logging;
using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>The outcome of <see cref="DeploymentValidator.Validate"/>. Never contains key values or passwords.</summary>
/// <param name="Problems">Configuration that would leave the deployed controller unusable or unsafe; any one fails the check.</param>
/// <param name="Warnings">Weakened but deliberate settings (for example HTTPS turned off on an isolated LAN).</param>
/// <param name="Notes">What was checked (key names and roles, listener, certificate subject and expiry).</param>
public sealed record DeploymentValidationResult(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when no problem was found.</summary>
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks the configuration a container will start with, before the running controller is replaced
/// (<c>dotnet HVO.RoofControllerV4.RPi.dll --validate-deployment</c>, run by <c>deploy-roofcontroller-rpi.sh</c>
/// with the final container's environment, devices, secrets and certificate mounts). It reads configuration and only
/// checks that the HAT's I2C device exists: it does not build the host, open a listener or touch the HAT, so it is safe
/// while another controller owns the hardware.
/// </summary>
/// <remarks>
/// Fails when the roof options do not validate, or ignore the limit switches on the roof hardware without consent;
/// when another options section cannot be bound, the telemetry options do not validate or a log level is not one;
/// when no usable API key is configured, a configured key entry is rejected, no key can operate the roof (RoofOperator
/// or RoofAdmin), or the deploy script's key is missing or cannot stop the roof; when a Kestrel endpoint has no Url or
/// is http with HTTPS-only settings; when no listener serves http://localhost:8080 (the health check and the deploy
/// script's calls); when HTTPS is required but Kestrel would not listen on HTTPS; when an HTTPS listener has no
/// certificate, or a configured certificate cannot be loaded, has no private key, is not for server authentication,
/// or is outside its validity period; or when AllowedHosts would refuse localhost. Reachability from the network is
/// checked by the deploy script after the switch.
/// </remarks>
public static partial class DeploymentValidator
{
    /// <summary>Command-line switch that runs the check instead of the controller.</summary>
    public const string CommandLineSwitch = "--validate-deployment";

    /// <summary>
    /// Optional configuration key holding the SHA-256 (hex) of the key the deploy script will use; when set, that key
    /// must be one of the usable keys. The deploy script passes it to the pre-flight check only.
    /// </summary>
    public const string DeployKeySha256Key = "DeploymentCheck:DeployKeySha256";

    /// <summary>
    /// The HAT's I2C bus device. The HAT library opens it whenever it exists, whatever the hardware-detection settings
    /// say, so the controller drives the roof hardware exactly when this device is mapped into the container.
    /// </summary>
    internal const string HatBusDevicePath = "/dev/i2c-1";

    /// <summary>A certificate expiring sooner than this produces a warning.</summary>
    internal static readonly TimeSpan CertificateExpiryWarning = TimeSpan.FromDays(30);

    private const string DefaultCertificateSection = "Kestrel:Certificates:Default";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string EcdsaOid = "1.2.840.10045.2.1";
    private const string DsaOid = "1.2.840.10040.4.1";

    /// <summary>Validates <paramref name="configuration"/> as the controller would load it in <paramref name="environment"/>.</summary>
    public static DeploymentValidationResult Validate(IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider)
        => Validate(configuration, environment, timeProvider, File.Exists(HatBusDevicePath));

    /// <summary>Validates as <see cref="Validate(IConfiguration, IHostEnvironment, TimeProvider)"/> does, with the HAT's presence given.</summary>
    internal static DeploymentValidationResult Validate(IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider, bool hatBusPresent)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var problems = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();

        ValidateRoofOptions(configuration, hatBusPresent, problems);
        ValidateOtherOptions(configuration, problems, warnings);
        ValidateLogLevels(configuration, problems);
        var security = ValidateApiKeys(configuration, problems, notes);
        ValidateTransport(configuration, environment, security, timeProvider.GetUtcNow(), problems, warnings, notes);

        if (security?.AllowAnonymousStop == true)
        {
            warnings.Add("RoofControllerSecurity:AllowAnonymousStop is enabled: anyone who can reach the controller can stop the roof.");
        }

        ValidateAllowedHosts(configuration, environment, problems, warnings);
        return new DeploymentValidationResult(problems, warnings, notes);
    }

    /// <summary>Runs <see cref="Validate"/>, writes the report to <paramref name="output"/> and returns the process exit code.</summary>
    public static int Run(IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);

        return Report(environment.EnvironmentName, Validate(configuration, environment, timeProvider), output);
    }

    /// <summary>
    /// Reports a check that could not run (for example a malformed appsettings file) as a single problem naming the
    /// exception type and message, and returns the failing exit code. Configuration loaders name the file, not values.
    /// </summary>
    internal static int ReportFailure(Exception exception, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(output);

        var problem = $"The deployment check could not complete ({exception.GetType().Name}: {exception.Message}).";
        return Report(null, new DeploymentValidationResult([problem], [], []), output);
    }

    private static int Report(string? environmentName, DeploymentValidationResult result, TextWriter output)
    {
        output.WriteLine(environmentName is null ? "Deployment check:" : $"Deployment check ({environmentName}):");
        foreach (var note in result.Notes)
        {
            output.WriteLine($"  {note}");
        }

        foreach (var warning in result.Warnings)
        {
            output.WriteLine($"  WARNING: {warning}");
        }

        foreach (var problem in result.Problems)
        {
            output.WriteLine($"  PROBLEM: {problem}");
        }

        output.WriteLine(result.IsValid
            ? "Deployment check passed."
            : string.Create(CultureInfo.InvariantCulture, $"Deployment check FAILED: {result.Problems.Count} problem(s). See docs/deployment.md."));
        return result.IsValid ? 0 : 1;
    }

    private static void ValidateRoofOptions(IConfiguration configuration, bool hatBusPresent, List<string> problems)
    {
        var options = Bind<RoofControllerOptionsV4>(configuration, nameof(RoofControllerOptionsV4), problems);
        if (options is null)
        {
            return;
        }

        var result = new RoofControllerOptionsV4Validator().Validate(null, options);
        if (result.Failed)
        {
            problems.AddRange(result.Failures.Select(failure => $"{nameof(RoofControllerOptionsV4)}: {failure}"));
        }

        // The controller checks this at initialization, against the HAT it actually found: the real one whenever its
        // I2C device exists (see HatBusDevicePath), whatever HVO_FORCE_RASPBERRY_PI or USE_REAL_GPIO say.
        if (options.IgnorePhysicalLimitSwitches
            && !options.AllowIgnoringLimitSwitchesOnPhysicalHardware
            && hatBusPresent)
        {
            problems.Add(
                $"{nameof(RoofControllerOptionsV4)}: IgnorePhysicalLimitSwitches is true without AllowIgnoringLimitSwitchesOnPhysicalHardware, " +
                $"and the HAT's I2C device ({HatBusDevicePath}) is mapped, so the controller would drive the roof hardware, refuse to " +
                "initialize and accept no roof commands. Set IgnorePhysicalLimitSwitches to false (or, for supervised testing only, " +
                "AllowIgnoringLimitSwitchesOnPhysicalHardware to true).");
        }
    }

    /// <summary>
    /// The log levels the logging configuration reads (<c>Logging:LogLevel:*</c> and <c>Logging:&lt;provider&gt;:LogLevel:*</c>).
    /// A value that is not a <see cref="LogLevel"/> fails the controller at startup. The report names the setting only.
    /// </summary>
    private static void ValidateLogLevels(IConfiguration configuration, List<string> problems)
    {
        foreach (var section in configuration.GetSection("Logging").GetChildren())
        {
            var levels = string.Equals(section.Key, "LogLevel", StringComparison.OrdinalIgnoreCase) ? section : section.GetSection("LogLevel");
            foreach (var level in levels.GetChildren())
            {
                if (!string.IsNullOrEmpty(level.Value) && !Enum.TryParse<LogLevel>(level.Value, ignoreCase: true, out _))
                {
                    problems.Add(
                        $"{level.Path} is not a log level, so the controller would fail at startup. Use one of " +
                        $"{string.Join(", ", Enum.GetNames<LogLevel>())}.");
                }
            }
        }
    }

    /// <summary>
    /// Binds the other sections the controller reads at startup or on first use, and runs their checks. A value that
    /// cannot be converted would otherwise throw when the options are first resolved.
    /// </summary>
    private static void ValidateOtherOptions(IConfiguration configuration, List<string> problems, List<string> warnings)
    {
        Bind<RoofControllerHostOptionsV4>(configuration, nameof(RoofControllerHostOptionsV4), problems);
        Bind<ConsoleLogBufferOptions>(configuration, "ConsoleLogBuffer", problems);

        var blueIris = Bind<BlueIrisOptions>(configuration, BlueIrisOptions.SectionName, problems);
        if (!string.IsNullOrWhiteSpace(blueIris?.BaseUrl) && blueIris.GetConfigurationProblem() is { } cameraProblem)
        {
            // The camera is optional: the roof works without it, but the camera endpoint would answer 503.
            warnings.Add(cameraProblem);
        }

        var telemetry = Bind<TelemetryOptions>(configuration, "Telemetry", problems);
        try
        {
            telemetry?.Validate();
        }
        catch (InvalidOperationException ex)
        {
            // TelemetryOptionsValidator fails startup with the same message; it names settings and ranges, not values.
            problems.Add($"Telemetry: {ex.Message}");
        }
    }

    /// <summary>
    /// Binds <paramref name="sectionName"/> onto new options as <c>services.Configure</c> does, or reports why it cannot
    /// be bound and returns null. The binder's message quotes the offending value, which may be a secret, so the report
    /// names only the setting and the type it should have.
    /// </summary>
    private static T? Bind<T>(IConfiguration configuration, string sectionName, List<string> problems)
        where T : class, new()
    {
        var options = new T();
        try
        {
            configuration.GetSection(sectionName).Bind(options);
            return options;
        }
        catch (InvalidOperationException ex)
        {
            var conversion = FailedConversion().Match(ex.Message);
            problems.Add(conversion.Success
                ? $"{sectionName} could not be read: the value of {conversion.Groups["path"].Value} is not a valid {conversion.Groups["type"].Value}."
                : $"{sectionName} could not be read: a setting has a value that cannot be converted to its type.");
            return null;
        }
    }

    // "Failed to convert configuration value '<value>' at '<path>' to type '<type>'.": match from the end, past the value.
    [GeneratedRegex(@"at '(?<path>[^']*)' to type '(?<type>[^']*)'\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex FailedConversion();

    private static RoofControllerSecurityOptions? ValidateApiKeys(IConfiguration configuration, List<string> problems, List<string> notes)
    {
        var security = Bind<RoofControllerSecurityOptions>(configuration, RoofControllerSecurityOptions.SectionName, problems);
        if (security is null)
        {
            return null;
        }

        var (keys, keyProblems) = RoofApiKeyStore.Inspect(security);
        problems.AddRange(keyProblems.Select(problem => $"API key {problem}"));

        if (keys.Count == 0)
        {
            problems.Add(
                "No usable API keys are configured, so every protected endpoint would answer 401. Provision keys as Docker secrets " +
                "(RoofControllerSecurity__ApiKeys__N__Name, __Role and __Key under the secrets directory); see docs/security.md.");
            return security;
        }

        notes.Add("API keys: " + string.Join(", ", keys.Select(key => $"{key.Name} ({key.Role})")));
        if (!keys.Any(key => CanOperate(key.Role)))
        {
            problems.Add("No API key has the RoofOperator or RoofAdmin role, so nobody could open, close or stop the roof through the API or console.");
        }

        ValidateDeployKey(configuration[DeployKeySha256Key], keys, problems, notes);
        return security;
    }

    private static bool CanOperate(string role) => role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole;

    private static void ValidateDeployKey(string? deployKeySha256, IReadOnlyList<RoofApiKeyIdentity> keys, List<string> problems, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(deployKeySha256))
        {
            return;
        }

        var hex = deployKeySha256.Trim();
        if (hex.Length != SHA256.HashSizeInBytes * 2 || !hex.All(char.IsAsciiHexDigit))
        {
            problems.Add($"{DeployKeySha256Key} must be 64 hexadecimal characters.");
            return;
        }

        var keyId = RoofApiKeyStore.ComputeKeyId(Convert.FromHexString(hex));
        var match = keys.FirstOrDefault(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
        if (match is null)
        {
            problems.Add(
                "The deploy script's API key is not one of the configured keys, so the new controller would reject its status and " +
                "stop checks. Add it to the secrets directory or point the script at a configured key.");
            return;
        }

        notes.Add($"Deploy key: {match.Name} ({match.Role}).");
        if (!CanOperate(match.Role))
        {
            problems.Add(
                $"The deploy script's API key ({match.Name}) has the {match.Role} role, so the new controller would answer 403 to its " +
                "verified stop and the deployment would be rolled back. Point the script at a RoofOperator or RoofAdmin key.");
        }
    }

    private static void ValidateTransport(
        IConfiguration configuration,
        IHostEnvironment environment,
        RoofControllerSecurityOptions? security,
        DateTimeOffset now,
        List<string> problems,
        List<string> warnings,
        List<string> notes)
    {
        var listeners = RoofSecurityStartup.ResolveListeners(configuration);
        var httpsRequired = security is not null && RequireHttpsMiddleware.IsHttpsRequired(security, environment);
        notes.Add($"Listeners: {listeners}; HTTPS required: {(httpsRequired ? "yes" : "no")}.");

        ValidateEndpoints(configuration, problems);
        if (!listeners.Addresses.Any(ServesLocalHttp8080))
        {
            problems.Add(
                $"No listener serves http://localhost:8080 (Kestrel would listen on {listeners}), so the container health check and " +
                "the deploy script's status and stop calls would fail and the deployment would be rolled back. Keep an http " +
                "listener on port 8080 for localhost, a loopback address or all addresses, for example " +
                "ASPNETCORE_URLS=http://localhost:8080;https://+:8443 or a Kestrel:Endpoints entry with Url http://localhost:8080.");
        }

        if (httpsRequired && !listeners.HasHttps)
        {
            problems.Add(
                $"RoofControllerSecurity:RequireHttps is in effect but no HTTPS listener is configured: Kestrel would listen on {listeners} " +
                "only, so every remote request would get 403 https_required while /health/ready still passes. Kestrel takes its listeners " +
                "from Kestrel:Endpoints when any is set, otherwise from ASPNETCORE_URLS, otherwise from ASPNETCORE_HTTP_PORTS and " +
                "ASPNETCORE_HTTPS_PORTS. Configure an HTTPS listener with a certificate (docs/deployment.md) or, on an isolated LAN only, " +
                "set RoofControllerSecurity__RequireHttps=false.");
        }
        else if (!httpsRequired && !environment.IsDevelopment())
        {
            warnings.Add(
                "HTTPS is not required: API keys and the console access key cross the network in clear text. Use this only on an " +
                "isolated LAN (docs/deployment.md).");
        }

        var withoutCertificate = listeners.HasHttps ? ListenersWithoutCertificate(configuration, listeners) : [];
        if (withoutCertificate.Count > 0)
        {
            problems.Add(
                $"An HTTPS listener is configured without a certificate ({string.Join(", ", withoutCertificate)}). Set " +
                "Kestrel__Certificates__Default__Path (with Kestrel__Certificates__Default__KeyPath for a PEM key, or " +
                "Kestrel__Certificates__Default__Password for a protected PFX) to a certificate mounted into the container.");
        }

        // Kestrel loads every configured certificate at startup, even when no listener uses it.
        foreach (var source in CertificateSources(configuration))
        {
            ValidateCertificate(source, environment.ContentRootPath, now, problems, warnings, notes);
        }
    }

    /// <summary>
    /// The HTTPS listeners Kestrel would have no certificate for. An https endpoint uses its own Certificate, else the
    /// default certificate; https URLs and ports always use the default certificate (there is no development
    /// certificate in the container).
    /// </summary>
    private static List<string> ListenersWithoutCertificate(IConfiguration configuration, ResolvedListeners listeners)
    {
        if (IsConfigured(configuration.GetSection(DefaultCertificateSection)))
        {
            return [];
        }

        if (listeners.Source != ListenerSource.KestrelEndpoints)
        {
            return [RoofSecurityStartup.DescribeSource(listeners.Source)];
        }

        return configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Where(endpoint => RoofSecurityStartup.IsHttpsAddress(endpoint["Url"]) && !IsConfigured(endpoint.GetSection("Certificate")))
            .Select(endpoint => $"Kestrel:Endpoints:{endpoint.Key}")
            .ToList();
    }

    /// <summary>
    /// The Kestrel default certificate plus the certificates of https endpoints, as configured paths. An http endpoint's
    /// certificate is reported by <see cref="ValidateEndpoints"/> instead.
    /// </summary>
    private static IEnumerable<CertificateSource> CertificateSources(IConfiguration configuration)
    {
        var defaultCertificate = configuration.GetSection(DefaultCertificateSection);
        if (IsConfigured(defaultCertificate))
        {
            yield return new CertificateSource(DefaultCertificateSection, defaultCertificate);
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var certificate = endpoint.GetSection("Certificate");
            if (IsConfigured(certificate) && RoofSecurityStartup.IsHttpsAddress(endpoint["Url"]))
            {
                yield return new CertificateSource($"Kestrel:Endpoints:{endpoint.Key}:Certificate", certificate);
            }
        }
    }

    /// <summary>
    /// Kestrel reads every <c>Kestrel:Endpoints</c> entry at startup, whichever listener source wins, and refuses to start
    /// when one has no Url or is an http endpoint with HTTPS-only settings.
    /// </summary>
    private static void ValidateEndpoints(IConfiguration configuration, List<string> problems)
    {
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var name = $"Kestrel:Endpoints:{endpoint.Key}";
            var url = endpoint["Url"];
            if (string.IsNullOrWhiteSpace(url))
            {
                problems.Add($"{name} has no Url, so Kestrel would refuse to start. Set {name}:Url or remove the endpoint's settings.");
                continue;
            }

            if (RoofSecurityStartup.IsHttpsAddress(url))
            {
                continue;
            }

            var httpsOnly = new List<string>();
            if (IsConfigured(endpoint.GetSection("Certificate")))
            {
                httpsOnly.Add("Certificate");
            }

            if (Enum.TryParse<ClientCertificateMode>(endpoint["ClientCertificateMode"], ignoreCase: true, out _))
            {
                httpsOnly.Add("ClientCertificateMode");
            }

            if (endpoint.GetSection("SslProtocols").GetChildren().Any())
            {
                httpsOnly.Add("SslProtocols");
            }

            if (endpoint.GetSection("Sni").GetChildren().Any())
            {
                httpsOnly.Add("Sni");
            }

            if (httpsOnly.Count > 0)
            {
                problems.Add(
                    $"{name} is an http endpoint with HTTPS-only settings ({string.Join(", ", httpsOnly)}), so Kestrel would refuse to " +
                    "start. Use an https Url or remove those settings.");
            }
        }
    }

    /// <summary>
    /// True for an http listener on port 8080 that http://localhost:8080 reaches: localhost, a loopback address, or all
    /// addresses (*, +, 0.0.0.0, [::]).
    /// </summary>
    internal static bool ServesLocalHttp8080(string address)
    {
        const string scheme = "http://";
        var trimmed = address.Trim();
        if (!trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var authority = trimmed[scheme.Length..];
        var slash = authority.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        // host:port, or [IPv6]:port. Without a port the listener is on 80.
        var colon = authority.StartsWith('[') ? authority.IndexOf("]:", StringComparison.Ordinal) + 1 : authority.LastIndexOf(':');
        if (colon <= 0
            || !int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port != 8080)
        {
            return false;
        }

        return authority[..colon].ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "*" or "+" or "0.0.0.0" or "[::]";
    }

    private static bool IsConfigured(IConfigurationSection certificate)
        => !string.IsNullOrWhiteSpace(certificate["Path"]) || !string.IsNullOrWhiteSpace(certificate["Subject"]);

    private static void ValidateCertificate(
        CertificateSource source,
        string contentRoot,
        DateTimeOffset now,
        List<string> problems,
        List<string> warnings,
        List<string> notes)
    {
        var configuredPath = source.Section["Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            warnings.Add($"{source.Name} names a certificate store entry; only file certificates are checked here.");
            return;
        }

        // Kestrel resolves relative certificate paths against the content root, and pairs a key whenever KeyPath is set.
        var path = Path.Combine(contentRoot, configuredPath);
        var keyPath = source.Section["KeyPath"] is { } configuredKeyPath ? Path.Combine(contentRoot, configuredKeyPath) : null;

        if (!File.Exists(path))
        {
            problems.Add($"{source.Name}: certificate file '{path}' does not exist (is the certificate directory mounted?).");
            return;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = Load(path, keyPath, source.Section["Password"]);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Loader messages name the file and the failure (e.g. a wrong password); they never contain the password.
            problems.Add($"{source.Name}: certificate '{path}' could not be loaded ({ex.GetType().Name}: {ex.Message}).");
            return;
        }

        using (certificate)
        {
            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{source.Name}: {certificate.Subject}, valid {notBefore:yyyy-MM-dd} to {notAfter:yyyy-MM-dd}, thumbprint {certificate.Thumbprint}."));

            if (!certificate.HasPrivateKey)
            {
                problems.Add($"{source.Name}: certificate '{path}' has no private key, so Kestrel cannot serve HTTPS with it.");
            }

            if (!IsAllowedForServerAuthentication(certificate))
            {
                problems.Add(
                    $"{source.Name}: certificate '{path}' has an Extended Key Usage extension without Server Authentication " +
                    $"({ServerAuthenticationOid}), so Kestrel refuses to serve HTTPS with it.");
            }

            if (now < notBefore)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{source.Name}: certificate '{path}' is not valid until {notBefore:u}."));
            }
            else if (now >= notAfter)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{source.Name}: certificate '{path}' expired on {notAfter:u}."));
            }
            else if (notAfter - now < CertificateExpiryWarning)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{source.Name}: certificate '{path}' expires on {notAfter:u}; renew it soon."));
            }
        }
    }

    /// <summary>Kestrel's rule: a certificate without an Extended Key Usage extension may serve; one with it must list Server Authentication.</summary>
    private static bool IsAllowedForServerAuthentication(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList();
        return extensions.Count == 0
            || extensions.Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == ServerAuthenticationOid));
    }

    /// <summary>
    /// Loads the certificate the way Kestrel's certificate loader does. With a KeyPath, the file must hold a single PEM
    /// or DER certificate, and the PEM key is imported for the certificate's algorithm: as an encrypted key whenever a
    /// password is configured (even an empty one), as a plain key only when none is. Without a KeyPath, the file is
    /// PKCS#12 (PFX) or, carrying no key, a plain certificate.
    /// </summary>
    private static X509Certificate2 Load(string path, string? keyPath, string? password)
    {
        // Kestrel first reads the file as a PEM chain, so a malformed PEM certificate fails here as it would there.
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(path);
        foreach (var chainCertificate in chain)
        {
            chainCertificate.Dispose();
        }

        var contentType = X509Certificate2.GetCertContentType(path);
        if (keyPath is null)
        {
            return contentType == X509ContentType.Pkcs12
                ? X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet)
                : X509CertificateLoader.LoadCertificateFromFile(path);
        }

        if (contentType != X509ContentType.Cert)
        {
            throw new CryptographicException("The file is not a PEM or DER certificate, so the KeyPath key cannot be paired with it.");
        }

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        var keyText = File.ReadAllText(keyPath);
        switch (certificate.PublicKey.Oid.Value)
        {
            case RsaOid:
            {
                using var rsa = RSA.Create();
                ImportKey(rsa, keyText, password);
                return certificate.CopyWithPrivateKey(rsa);
            }

            case EcdsaOid:
            {
                using var ecdsa = ECDsa.Create();
                ImportKey(ecdsa, keyText, password);
                return certificate.CopyWithPrivateKey(ecdsa);
            }

            case DsaOid:
            {
                using var dsa = DSA.Create();
                ImportKey(dsa, keyText, password);
                return certificate.CopyWithPrivateKey(dsa);
            }

            default:
                throw new CryptographicException(
                    $"The certificate's key algorithm ({certificate.PublicKey.Oid.Value}) is not checked here; use an RSA or ECDSA certificate.");
        }

        static void ImportKey(AsymmetricAlgorithm key, string keyText, string? password)
        {
            if (password is null)
            {
                key.ImportFromPem(keyText);
            }
            else
            {
                key.ImportFromEncryptedPem(keyText, password);
            }
        }
    }

    /// <summary>
    /// AllowedHosts as host filtering applies it: split on ';' without trimming, any entry "*", "[::]" or "0.0.0.0"
    /// (or none at all) allows every host, and other entries must match the Host header's name exactly, ignoring case.
    /// The container health check requests http://localhost:8080/health/ready.
    /// </summary>
    private static void ValidateAllowedHosts(IConfiguration configuration, IHostEnvironment environment, List<string> problems, List<string> warnings)
    {
        var entries = configuration["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (entries.Length == 0 || entries.Any(entry => entry is "*" or "[::]" or "0.0.0.0"))
        {
            if (environment.IsProduction())
            {
                warnings.Add("AllowedHosts is '*' in Production; set it to the controller's host names to refuse DNS-rebinding requests.");
            }

            return;
        }

        if (!entries.Any(entry => string.Equals(entry, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(
                "AllowedHosts does not include localhost, so host filtering would answer 400 to the container health check " +
                "(http://localhost:8080/health/ready), the container would never become healthy and the deployment would be rolled " +
                "back. Add localhost to the ';'-separated list; entries must match exactly (no spaces or ports), and a *.domain " +
                "wildcard does not cover localhost.");
        }
    }

    private sealed record CertificateSource(string Name, IConfigurationSection Section);
}
