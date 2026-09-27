using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
/// with the final container's environment, secrets and certificate mounts). It reads configuration only: it does not
/// build the host, open a listener or touch the HAT, so it is safe while another controller owns the hardware.
/// </summary>
/// <remarks>
/// Fails when the roof options do not validate; when no usable API key is configured, a configured key entry is
/// rejected, or no key can operate the roof (RoofOperator or RoofAdmin); when HTTPS is required but no HTTPS listener
/// is configured; or when an HTTPS listener's certificate cannot be loaded, has no private key, or is outside its
/// validity period. Reachability from the network is checked by the deploy script after the switch.
/// </remarks>
public static class DeploymentValidator
{
    /// <summary>Command-line switch that runs the check instead of the controller.</summary>
    public const string CommandLineSwitch = "--validate-deployment";

    /// <summary>
    /// Optional configuration key holding the SHA-256 (hex) of the key the deploy script will use; when set, that key
    /// must be one of the usable keys. The deploy script passes it to the pre-flight check only.
    /// </summary>
    public const string DeployKeySha256Key = "DeploymentCheck:DeployKeySha256";

    /// <summary>A certificate expiring sooner than this produces a warning.</summary>
    internal static readonly TimeSpan CertificateExpiryWarning = TimeSpan.FromDays(30);

    /// <summary>Validates <paramref name="configuration"/> as the controller would load it in <paramref name="environment"/>.</summary>
    public static DeploymentValidationResult Validate(IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var problems = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();

        ValidateRoofOptions(configuration, problems);
        var security = ValidateApiKeys(configuration, problems, notes);
        ValidateTransport(configuration, environment, security, timeProvider.GetUtcNow(), problems, warnings, notes);

        if (security?.AllowAnonymousStop == true)
        {
            warnings.Add("RoofControllerSecurity:AllowAnonymousStop is enabled: anyone who can reach the controller can stop the roof.");
        }

        var allowedHosts = configuration["AllowedHosts"];
        if (environment.IsProduction() && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*"))
        {
            warnings.Add("AllowedHosts is '*' in Production; set it to the controller's host names to refuse DNS-rebinding requests.");
        }

        return new DeploymentValidationResult(problems, warnings, notes);
    }

    /// <summary>Runs <see cref="Validate"/>, writes the report to <paramref name="output"/> and returns the process exit code.</summary>
    public static int Run(IConfiguration configuration, IHostEnvironment environment, TimeProvider timeProvider, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = Validate(configuration, environment, timeProvider);
        output.WriteLine($"Deployment check ({environment.EnvironmentName}):");
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

    private static void ValidateRoofOptions(IConfiguration configuration, List<string> problems)
    {
        RoofControllerOptionsV4 options;
        try
        {
            options = configuration.GetSection(nameof(RoofControllerOptionsV4)).Get<RoofControllerOptionsV4>() ?? new RoofControllerOptionsV4();
        }
        catch (InvalidOperationException ex)
        {
            problems.Add($"{nameof(RoofControllerOptionsV4)} could not be read: {ex.Message}");
            return;
        }

        var result = new RoofControllerOptionsV4Validator().Validate(null, options);
        if (result.Failed)
        {
            problems.AddRange(result.Failures.Select(failure => $"{nameof(RoofControllerOptionsV4)}: {failure}"));
        }
    }

    private static RoofControllerSecurityOptions? ValidateApiKeys(IConfiguration configuration, List<string> problems, List<string> notes)
    {
        RoofControllerSecurityOptions? security;
        try
        {
            security = configuration.GetSection(RoofControllerSecurityOptions.SectionName).Get<RoofControllerSecurityOptions>()
                ?? new RoofControllerSecurityOptions();
        }
        catch (InvalidOperationException ex)
        {
            problems.Add($"{RoofControllerSecurityOptions.SectionName} could not be read: {ex.Message}");
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
        if (!keys.Any(key => key.Role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole))
        {
            problems.Add("No API key has the RoofOperator or RoofAdmin role, so nobody could open, close or stop the roof through the API or console.");
        }

        ValidateDeployKey(configuration[DeployKeySha256Key], keys, problems, notes);
        return security;
    }

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
        var httpsConfigured = RoofSecurityStartup.IsHttpsConfigured(configuration);
        var httpsRequired = security is not null && RequireHttpsMiddleware.IsHttpsRequired(security, environment);
        notes.Add($"Listeners: {DescribeListeners(configuration)}; HTTPS required: {(httpsRequired ? "yes" : "no")}.");

        if (httpsRequired && !httpsConfigured)
        {
            problems.Add(
                "RoofControllerSecurity:RequireHttps is in effect but no HTTPS listener is configured (ASPNETCORE_URLS, HTTPS_PORTS or " +
                "Kestrel:Endpoints), so every remote request would get 403 https_required while /health/ready still passes. Configure " +
                "a certificate (docs/deployment.md) or, on an isolated LAN only, set RoofControllerSecurity__RequireHttps=false.");
        }
        else if (!httpsRequired && !environment.IsDevelopment())
        {
            warnings.Add(
                "HTTPS is not required: API keys and the console access key cross the network in clear text. Use this only on an " +
                "isolated LAN (docs/deployment.md).");
        }

        if (!httpsConfigured)
        {
            return;
        }

        var certificates = CertificateSources(configuration).ToList();
        if (certificates.Count == 0)
        {
            problems.Add(
                "An HTTPS listener is configured without a certificate. Set Kestrel__Certificates__Default__Path (and " +
                "Kestrel__Certificates__Default__Password for a protected PFX) to a certificate mounted into the container.");
            return;
        }

        foreach (var source in certificates)
        {
            ValidateCertificate(source, environment.ContentRootPath, now, problems, warnings, notes);
        }
    }

    private static string DescribeListeners(IConfiguration configuration)
    {
        var values = new[] { configuration["urls"], configuration["ASPNETCORE_URLS"] }
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"]))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (key, scheme) in new[] { ("http_ports", "http"), ("HTTP_PORTS", "http"), ("https_ports", "https"), ("HTTPS_PORTS", "https") })
        {
            var ports = configuration[key];
            if (!string.IsNullOrWhiteSpace(ports))
            {
                values.Add($"{scheme} port(s) {ports}");
            }
        }

        return values.Count == 0 ? "default" : string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The Kestrel default certificate plus the certificates of https endpoints, as configured paths.</summary>
    private static IEnumerable<CertificateSource> CertificateSources(IConfiguration configuration)
    {
        var defaultCertificate = configuration.GetSection("Kestrel:Certificates:Default");
        if (!string.IsNullOrWhiteSpace(defaultCertificate["Path"]) || !string.IsNullOrWhiteSpace(defaultCertificate["Subject"]))
        {
            yield return new CertificateSource("Kestrel:Certificates:Default", defaultCertificate);
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var certificate = endpoint.GetSection("Certificate");
            if (!string.IsNullOrWhiteSpace(certificate["Path"]) || !string.IsNullOrWhiteSpace(certificate["Subject"]))
            {
                yield return new CertificateSource($"Kestrel:Endpoints:{endpoint.Key}:Certificate", certificate);
            }
        }
    }

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

        // Kestrel resolves relative certificate paths against the content root.
        var path = Path.Combine(contentRoot, configuredPath);
        var keyPath = source.Section["KeyPath"] is { Length: > 0 } configuredKeyPath ? Path.Combine(contentRoot, configuredKeyPath) : null;

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
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
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

    /// <summary>Loads the certificate the way Kestrel does: PEM with a key file when KeyPath is set, otherwise PKCS#12.</summary>
    private static X509Certificate2 Load(string path, string? keyPath, string? password)
    {
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            return string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
    }

    private sealed record CertificateSource(string Name, IConfigurationSection Section);
}
