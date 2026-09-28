using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// The <c>--validate-deployment</c> check that deploy-roofcontroller-rpi.sh runs with the final container's configuration
/// before it replaces the running controller.
/// </summary>
[TestClass]
public sealed class DeploymentValidatorTests
{
    private const string OperatorKey = "deploy-check-operator-key-0000000001";
    private const string ViewerKey = "deploy-check-viewer-key-000000000002";
    private const string CertificatePassword = "deploy-check-pfx-password";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-deploy-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void HttpsDeployment_WithOperatorKeyAndValidCertificate_Passes()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
        result.Notes.Should().Contain(note => note.Contains("operator (RoofOperator)"));
        result.Notes.Should().Contain(note => note.Contains("https://+:8443") && note.Contains("HTTPS required: yes"));
        result.Notes.Should().Contain(note => note.Contains("CN=roof-controller.test"));
    }

    [TestMethod]
    public void ProductionDefault_WithoutHttpsListener_FailsWithTheRemote403Explanation()
    {
        var configuration = OperatorKeys();
        configuration["urls"] = "http://+:8080";

        var result = Validate(configuration);

        result.IsValid.Should().BeFalse();
        result.Problems.Should().ContainSingle(problem => problem.Contains("no HTTPS listener") && problem.Contains("403 https_required"));
    }

    [TestMethod]
    public void ExplicitHttpOptOut_PassesWithAClearTextWarning()
    {
        var configuration = OperatorKeys();
        configuration["urls"] = "http://+:8080";
        configuration["RoofControllerSecurity:RequireHttps"] = "false";

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().Contain(warning => warning.Contains("clear text"));
    }

    [TestMethod]
    public void Development_DoesNotRequireHttps()
    {
        var configuration = OperatorKeys();
        configuration["urls"] = "http://+:8080";

        var result = Validate(configuration, Environments.Development);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().NotContain(warning => warning.Contains("clear text"));
    }

    [TestMethod]
    public void NoKeys_Fails()
    {
        var result = Validate(new Dictionary<string, string?> { ["RoofControllerSecurity:RequireHttps"] = "false" });

        result.Problems.Should().ContainSingle(problem => problem.Contains("No usable API keys"));
    }

    [TestMethod]
    public void ViewerKeyOnly_FailsBecauseNobodyCouldOperateTheRoof()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:RequireHttps"] = "false",
            ["RoofControllerSecurity:ApiKeys:0:Name"] = "dashboard",
            ["RoofControllerSecurity:ApiKeys:0:Role"] = "RoofViewer",
            ["RoofControllerSecurity:ApiKeys:0:Key"] = ViewerKey
        };

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("RoofOperator or RoofAdmin"));
    }

    [TestMethod]
    public void RejectedKeyEntry_Fails_WithoutRevealingTheKey()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:ApiKeys:1:Name"] = "short";
        configuration["RoofControllerSecurity:ApiKeys:1:Role"] = "RoofAdmin";
        configuration["RoofControllerSecurity:ApiKeys:1:Key"] = "too-short-key";

        var (result, report) = Run(configuration);

        result.Should().Be(1);
        report.Should().Contain("ApiKeys[1] ('short')").And.Contain("shorter than");
        report.Should().NotContain("too-short-key").And.NotContain(OperatorKey);
    }

    [TestMethod]
    public void NoIdentityStore_WarnsOutsideDevelopment()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";

        var production = Validate(configuration);
        var development = Validate(configuration, Environments.Development);

        production.Problems.Should().BeEmpty();
        production.Warnings.Should().ContainSingle(warning => warning.StartsWith("No identity store is configured"));
        development.Warnings.Should().NotContain(warning => warning.Contains("identity store"));
    }

    [TestMethod]
    public void AWritableIdentityStore_Passes_AndLeavesNothingBehind()
    {
        var path = Path.Combine(_directory, "identity.json");
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:Identity:StorePath"] = path;

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Notes.Should().Contain($"Identity store: {path}.");
        result.Warnings.Should().NotContain(warning => warning.Contains("identity store", StringComparison.OrdinalIgnoreCase));
        Directory.EnumerateFileSystemEntries(_directory).Should().BeEmpty("the check neither creates the store nor leaves its probe file");
    }

    [TestMethod]
    public void TheIdentityCheck_KeepsTheRunningControllersSaveInProgress()
    {
        var path = Path.Combine(_directory, "identity.json");
        var inProgress = path + ".0123456789abcdef0123456789abcdef.tmp";
        File.WriteAllText(inProgress, "{");
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:Identity:StorePath"] = path;

        Validate(configuration).Problems.Should().BeEmpty();

        File.Exists(inProgress).Should().BeTrue("the old controller is still running while the check runs");
    }

    [TestMethod]
    [DataRow("missing-directory", "does not exist")]
    [DataRow("corrupt-file", "is not valid JSON")]
    [DataRow("bad-threshold", "LockoutThreshold must be at least 1")]
    [DataRow("bad-duration", "the value of RoofControllerSecurity:Identity:SessionLifetime is not a valid System.TimeSpan")]
    [DataRow("root-path", "must name a file in a directory")]
    [DataRow("directory-path", "is a directory")]
    [DataRow("bad-rate-limit", "SignInAttemptsPerMinute must be between 0 (off) and 10000")]
    public void AnUnusableIdentityStore_OrBadIdentitySettings_Fail(string setup, string expected)
    {
        var path = Path.Combine(_directory, "identity.json");
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:Identity:StorePath"] = path;
        switch (setup)
        {
            case "missing-directory":
                configuration["RoofControllerSecurity:Identity:StorePath"] = Path.Combine(_directory, "missing", "identity.json");
                break;
            case "corrupt-file":
                File.WriteAllText(path, "{ not json");
                break;
            case "bad-threshold":
                configuration["RoofControllerSecurity:Identity:LockoutThreshold"] = "0";
                break;
            case "bad-duration":
                configuration["RoofControllerSecurity:Identity:SessionLifetime"] = "soon";
                break;
            case "root-path":
                configuration["RoofControllerSecurity:Identity:StorePath"] = "/";
                break;
            case "directory-path":
                Directory.CreateDirectory(path);
                break;
            case "bad-rate-limit":
                configuration["RoofControllerSecurity:Identity:SignInAttemptsPerMinute"] = "-1";
                break;
        }

        var result = Validate(configuration);

        result.IsValid.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().Contain(expected);
    }

    [TestMethod]
    public void InvalidRoofOptions_Fail()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerOptionsV4:OpenRelayId"] = "9";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("RoofControllerOptionsV4: OpenRelayId"));
    }

    [TestMethod]
    public void UnreadableRoofOptions_Fail()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerOptionsV4:SafetyWatchdogTimeout"] = "not-a-timespan";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("RoofControllerOptionsV4 could not be read"));
    }

    [TestMethod]
    public void HttpsListener_WithoutCertificate_Fails()
    {
        var configuration = OperatorKeys();
        configuration["urls"] = "http://localhost:8080;https://+:8443";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("without a certificate"));
    }

    [TestMethod]
    public void MissingCertificateFile_Fails()
    {
        var result = Validate(HttpsDeployment(Path.Combine(_directory, "missing.pfx")));

        result.Problems.Should().ContainSingle(problem => problem.Contains("does not exist"));
    }

    [TestMethod]
    public void WrongCertificatePassword_Fails_WithoutRevealingThePassword()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = HttpsDeployment(certificate);
        configuration["Kestrel:Certificates:Default:Password"] = "wrong-pfx-password";

        var (exitCode, report) = Run(configuration);

        exitCode.Should().Be(1);
        report.Should().Contain("could not be loaded");
        report.Should().NotContain("wrong-pfx-password").And.NotContain(CertificatePassword);
    }

    [TestMethod]
    public void ExpiredCertificate_Fails()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-400), Now.AddDays(-1));

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().ContainSingle(problem => problem.Contains("expired"));
    }

    [TestMethod]
    public void NotYetValidCertificate_Fails()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(1), Now.AddDays(365));

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().ContainSingle(problem => problem.Contains("not valid until"));
    }

    [TestMethod]
    public void CertificateExpiringSoon_PassesWithAWarning()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-300), Now.AddDays(10));

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().Contain(warning => warning.Contains("renew it soon"));
    }

    [TestMethod]
    public void CertificateWithoutPrivateKey_Fails()
    {
        using var withKey = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var path = Path.Combine(_directory, "public-only.pfx");
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pkcs12, CertificatePassword));

        var result = Validate(HttpsDeployment(path));

        result.Problems.Should().ContainSingle(problem => problem.Contains("no private key"));
    }

    [TestMethod]
    public void PemCertificateWithKeyFile_Passes()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var certificatePath = Path.Combine(_directory, "roof.crt");
        var keyPath = Path.Combine(_directory, "roof.key");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var configuration = OperatorKeys();
        configuration["urls"] = "http://localhost:8080;https://+:8443";
        configuration["Kestrel:Certificates:Default:Path"] = certificatePath;
        configuration["Kestrel:Certificates:Default:KeyPath"] = keyPath;

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    public void RelativeCertificatePath_IsResolvedAgainstTheContentRoot()
    {
        WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));

        var result = Validate(HttpsDeployment("roof.pfx"));

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    public void EndpointCertificate_IsCheckedToo()
    {
        var configuration = OperatorKeys();
        configuration["Kestrel:Endpoints:Https:Url"] = "https://+:8443";
        configuration["Kestrel:Endpoints:Https:Certificate:Path"] = Path.Combine(_directory, "endpoint.pfx");

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("Kestrel:Endpoints:Https:Certificate") && problem.Contains("does not exist"));
    }

    [TestMethod]
    public void Run_PrintsTheReport_AndReturnsZeroWhenValid()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));

        var (exitCode, report) = Run(HttpsDeployment(certificate));

        exitCode.Should().Be(0);
        report.Should().Contain("Deployment check (Production)").And.Contain("Deployment check passed.");
        report.Should().NotContain(OperatorKey).And.NotContain(CertificatePassword);
    }

    [TestMethod]
    public void DeployKey_ThatIsConfigured_Passes_AndIsNamed()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration[DeploymentValidator.DeployKeySha256Key] = Sha256Hex(OperatorKey);

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Notes.Should().Contain("Deploy key: operator (RoofOperator).");
    }

    [TestMethod]
    public void DeployKey_ThatIsNotConfigured_Fails()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration[DeploymentValidator.DeployKeySha256Key] = Sha256Hex("some-other-deploy-key-000000000000003");

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("deploy script's API key is not one of the configured keys"));
    }

    [TestMethod]
    [DataRow("not-hex")]
    [DataRow("abcd")]
    public void DeployKey_Malformed_Fails(string value)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration[DeploymentValidator.DeployKeySha256Key] = value;

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("64 hexadecimal characters"));
    }

    [TestMethod]
    public void AnonymousStopAndWildcardHosts_AreWarnings()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:AllowAnonymousStop"] = "true";
        configuration["AllowedHosts"] = "*";

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().Contain(warning => warning.Contains("AllowAnonymousStop"));
        result.Warnings.Should().Contain(warning => warning.Contains("AllowedHosts"));
    }

    [TestMethod]
    public void HttpsPortsIgnoredBecauseOfHttpUrls_Fail()
    {
        // The image sets ASPNETCORE_URLS=http://+:8080; Kestrel then ignores ASPNETCORE_HTTPS_PORTS.
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = HttpsDeployment(certificate);
        configuration["urls"] = "http://+:8080";
        configuration["https_ports"] = "8443";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("no HTTPS listener") && problem.Contains("listen on http://+:8080"));
        result.Notes.Should().Contain(note => note.Contains("http://+:8080 (from ASPNETCORE_URLS; ignored: ASPNETCORE_HTTPS_PORTS)"));
    }

    [TestMethod]
    public void HttpKestrelEndpointOverridingHttpsUrls_Fails()
    {
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = HttpsDeployment(certificate);
        configuration["Kestrel:Endpoints:Http:Url"] = "http://+:8080";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("no HTTPS listener"));
        result.Notes.Should().Contain(note => note.Contains("http://+:8080 (from Kestrel:Endpoints; ignored: ASPNETCORE_URLS)"));
    }

    [TestMethod]
    public void HttpUrlsOverridingAnHttpsAspNetCoreUrlsVariable_Fail()
    {
        // The unprefixed environment provider also exposes the raw ASPNETCORE_URLS key, but Kestrel reads only urls,
        // which a /run/secrets/urls file (the last configuration source) overrides.
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = HttpsDeployment(certificate);
        configuration["ASPNETCORE_URLS"] = configuration["urls"];
        configuration["urls"] = "http://+:8080";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("no HTTPS listener"));
    }

    [TestMethod]
    public void HttpsKestrelEndpointWithItsOwnCertificate_Passes()
    {
        var certificate = WritePfx("endpoint.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = OperatorKeys();
        configuration["Kestrel:Endpoints:Http:Url"] = "http://localhost:8080";
        configuration["Kestrel:Endpoints:Https:Url"] = "https://+:8443";
        configuration["Kestrel:Endpoints:Https:Certificate:Path"] = certificate;
        configuration["Kestrel:Endpoints:Https:Certificate:Password"] = CertificatePassword;

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Notes.Should().Contain(note => note.Contains("https://+:8443 (from Kestrel:Endpoints; ignored: ASPNETCORE_URLS)"));
    }

    [TestMethod]
    public void HttpsKestrelEndpointWithoutACertificate_Fails_EvenWhenAnotherEndpointHasOne()
    {
        var certificate = WritePfx("endpoint.pfx", Now.AddDays(-1), Now.AddDays(365));
        var configuration = OperatorKeys();
        configuration["Kestrel:Endpoints:Https:Url"] = "https://+:8443";
        configuration["Kestrel:Endpoints:Https:Certificate:Path"] = certificate;
        configuration["Kestrel:Endpoints:Https:Certificate:Password"] = CertificatePassword;
        configuration["Kestrel:Endpoints:Admin:Url"] = "https://+:9443";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("without a certificate (Kestrel:Endpoints:Admin)"));
    }

    [TestMethod]
    public void DeployKey_WithTheViewerRole_Fails()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:ApiKeys:1:Name"] = "dashboard";
        configuration["RoofControllerSecurity:ApiKeys:1:Role"] = "RoofViewer";
        configuration["RoofControllerSecurity:ApiKeys:1:Key"] = ViewerKey;
        configuration[DeploymentValidator.DeployKeySha256Key] = Sha256Hex(ViewerKey);

        var result = Validate(configuration);

        result.Notes.Should().Contain("Deploy key: dashboard (RoofViewer).");
        result.Problems.Should().ContainSingle(problem =>
            problem.Contains("deploy script's API key (dashboard) has the RoofViewer role")
            && problem.Contains("Point the script at a RoofOperator or RoofAdmin key")
            && !problem.Contains("403"));
    }

    [TestMethod]
    public void DeployKey_WithTheAdminRole_Passes()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerSecurity:ApiKeys:0:Role"] = "RoofAdmin";
        configuration[DeploymentValidator.DeployKeySha256Key] = Sha256Hex(OperatorKey);

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Notes.Should().Contain("Deploy key: operator (RoofAdmin).");
    }

    [TestMethod]
    [DataRow("roof-pi")]
    [DataRow("roof-pi; localhost")]
    [DataRow("roof-pi;localhost:8080")]
    [DataRow("*.local")]
    [DataRow(" ")]
    public void AllowedHosts_WithoutLocalhost_Fails(string allowedHosts)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["AllowedHosts"] = allowedHosts;

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("AllowedHosts does not include localhost"));
        result.Warnings.Should().NotContain(warning => warning.Contains("AllowedHosts"));
    }

    [TestMethod]
    [DataRow("localhost")]
    [DataRow("roof-pi;LOCALHOST;")]
    public void AllowedHosts_WithLocalhost_Passes(string allowedHosts)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["AllowedHosts"] = allowedHosts;

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().NotContain(warning => warning.Contains("AllowedHosts"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("*")]
    [DataRow("roof-pi;*")]
    [DataRow("[::]")]
    [DataRow("0.0.0.0")]
    public void AllowedHosts_AllowingEveryHost_IsOnlyAProductionWarning(string allowedHosts)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["AllowedHosts"] = allowedHosts;

        var production = Validate(configuration);
        var development = Validate(configuration, Environments.Development);

        production.Problems.Should().BeEmpty();
        production.Warnings.Should().ContainSingle(warning => warning.StartsWith("AllowedHosts is '*' in Production"));
        development.Problems.Should().BeEmpty();
        development.Warnings.Should().NotContain(warning => warning.Contains("AllowedHosts"));
    }

    [TestMethod]
    public void ClientAuthenticationOnlyCertificate_Fails()
    {
        var certificate = WritePfx("client.pfx", Now.AddDays(-1), Now.AddDays(365), ClientAuthenticationOid);

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().ContainSingle(problem =>
            problem.StartsWith("Kestrel:Certificates:Default") && problem.Contains("without Server Authentication (1.3.6.1.5.5.7.3.1)"));
    }

    [TestMethod]
    public void ServerAuthenticationCertificate_Passes()
    {
        var certificate = WritePfx("server.pfx", Now.AddDays(-1), Now.AddDays(365), ClientAuthenticationOid, ServerAuthenticationOid);

        var result = Validate(HttpsDeployment(certificate));

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("RoofControllerOptionsV4", "SafetyWatchdogTimeout")]
    [DataRow("RoofControllerHostOptionsV4", "RestartOnFailureWaitTime")]
    [DataRow("ConsoleLogBuffer", "Capacity")]
    [DataRow("BlueIris", "ConnectTimeout")]
    [DataRow("Telemetry", "DefaultSamplingRate")]
    [DataRow("RoofControllerSecurity", "AllowAnonymousStop")]
    public void UnconvertibleValue_Fails_NamingTheSettingButNotTheValue(string section, string setting)
    {
        const string value = "unconvertible-value-that-might-be-a-secret";
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration[$"{section}:{setting}"] = value;

        var (exitCode, report) = Run(configuration);

        exitCode.Should().Be(1);
        report.Should().Contain($"PROBLEM: {section} could not be read: the value of {section}:{setting} is not a valid System.");
        report.Should().NotContain(value);
    }

    [TestMethod]
    public void InvalidTelemetryOptions_Fail()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Telemetry:Queue:Capacity"] = "10";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("Telemetry: Queue capacity must be at least 100"));
    }

    [TestMethod]
    public void MisconfiguredCameraProxy_IsAWarning()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["BlueIris:BaseUrl"] = "http://192.0.2.4:81";
        configuration["BlueIris:UserName"] = "camera-user";

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().Contain(warning => warning.Contains("BlueIris:UserName and BlueIris:Password") && !warning.Contains("camera-user"));
    }

    [TestMethod]
    public void EmptyPasswordWithAnUnencryptedPemKey_Fails()
    {
        // Kestrel treats any configured password, even an empty one, as the password of an encrypted key.
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate);
        var configuration = PemDeployment(certificatePath, keyPath);
        configuration["Kestrel:Certificates:Default:Password"] = "";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("could not be loaded"));
    }

    [TestMethod]
    public void DerCertificateWithPemKey_Passes()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate, derCertificate: true);

        var result = Validate(PemDeployment(certificatePath, keyPath));

        result.Problems.Should().BeEmpty();
        result.Notes.Should().Contain(note => note.Contains("CN=roof-controller.test"));
    }

    [TestMethod]
    public void EcdsaPemCertificateWithKeyFile_Passes()
    {
        using var certificate = CreateEcdsaCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate);

        var result = Validate(PemDeployment(certificatePath, keyPath));

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    public void EncryptedPemKey_WithItsPassword_Passes()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate, keyPassword: CertificatePassword);
        var configuration = PemDeployment(certificatePath, keyPath);
        configuration["Kestrel:Certificates:Default:Password"] = CertificatePassword;

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    public void EncryptedPemKey_WithAWrongPassword_Fails_WithoutRevealingThePassword()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate, keyPassword: CertificatePassword);
        var configuration = PemDeployment(certificatePath, keyPath);
        configuration["Kestrel:Certificates:Default:Password"] = "wrong-key-password";

        var (exitCode, report) = Run(configuration);

        exitCode.Should().Be(1);
        report.Should().Contain("could not be loaded");
        report.Should().NotContain("wrong-key-password").And.NotContain(CertificatePassword);
    }

    [TestMethod]
    public void EncryptedPemKey_WithoutAPassword_Fails()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, keyPath) = WritePem(certificate, keyPassword: CertificatePassword);

        var result = Validate(PemDeployment(certificatePath, keyPath));

        result.Problems.Should().ContainSingle(problem => problem.Contains("could not be loaded"));
    }

    [TestMethod]
    public void PemKeyOfAnotherCertificate_Fails()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        using var other = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, _) = WritePem(certificate);
        var otherKeyPath = Path.Combine(_directory, "other.key");
        File.WriteAllText(otherKeyPath, other.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        var result = Validate(PemDeployment(certificatePath, otherKeyPath));

        result.Problems.Should().ContainSingle(problem => problem.Contains("could not be loaded"));
    }

    [TestMethod]
    public void PfxWithAKeyPath_Fails()
    {
        // With KeyPath, Kestrel accepts only a PEM or DER certificate file.
        var certificate = WritePfx("roof.pfx", Now.AddDays(-1), Now.AddDays(365));
        using var withKey = X509CertificateLoader.LoadPkcs12FromFile(certificate, CertificatePassword);
        var (_, keyPath) = WritePem(withKey);

        var result = Validate(PemDeployment(certificate, keyPath));

        result.Problems.Should().ContainSingle(problem => problem.Contains("could not be loaded") && problem.Contains("not a PEM or DER certificate"));
    }

    [TestMethod]
    public void PemCertificateWithoutAKeyPath_FailsForTheMissingPrivateKey()
    {
        using var certificate = CreateCertificate(Now.AddDays(-1), Now.AddDays(365));
        var (certificatePath, _) = WritePem(certificate);
        var configuration = PemDeployment(certificatePath, keyPath: null);

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.Contains("no private key"));
    }

    [TestMethod]
    [DataRow("Production", null, null)]
    [DataRow("Production", "HVO_FORCE_RASPBERRY_PI", "false")]
    [DataRow("Development", "USE_REAL_GPIO", "false")]
    public void IgnoredLimitSwitches_WithTheHatDeviceMapped_Fail(string environment, string? key, string? value)
    {
        // The HAT library opens the I2C device whenever it exists, whatever the hardware-detection settings say.
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerOptionsV4:IgnorePhysicalLimitSwitches"] = "true";
        if (key is not null)
        {
            configuration[key] = value;
        }

        var result = Validate(configuration, environment, hatBusPresent: true);

        result.Problems.Should().ContainSingle(problem =>
            problem.StartsWith("RoofControllerOptionsV4: IgnorePhysicalLimitSwitches is true without AllowIgnoringLimitSwitchesOnPhysicalHardware")
            && problem.Contains(DeploymentValidator.HatBusDevicePath));
    }

    [TestMethod]
    [DataRow(true, "RoofControllerOptionsV4:AllowIgnoringLimitSwitchesOnPhysicalHardware", "true")]
    [DataRow(false, null, null)]
    [DataRow(false, "HVO_FORCE_RASPBERRY_PI", "true")]
    [DataRow(false, "USE_REAL_GPIO", "true")]
    public void IgnoredLimitSwitches_WithConsentOrWithoutTheHatDevice_Pass(bool hatBusPresent, string? key, string? value)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["RoofControllerOptionsV4:IgnorePhysicalLimitSwitches"] = "true";
        if (key is not null)
        {
            configuration[key] = value;
        }

        var result = Validate(configuration, hatBusPresent: hatBusPresent);

        result.Problems.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public void HatEmulatorMode_OutsideDevelopment_WithoutConsent_Fails(string environment)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["HatEmulator:Host"] = "hat-emulator";

        var result = Validate(configuration, environment);

        result.Problems.Should().ContainSingle().Which.Should()
            .StartWith($"HatEmulator:Enabled is true in the {environment} environment without HatEmulator:AllowOutsideDevelopment")
            .And.Contain("ALLOW_EMULATED_HAT=true");
        result.Warnings.Should().NotContain(warning => warning.StartsWith("HAT emulator mode"));
    }

    [TestMethod]
    [DataRow("Production", "true")]
    [DataRow("Development", null)]
    public void HatEmulatorMode_Allowed_PassesWithAWarningNamingTheEndpoint(string environment, string? allow)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["HatEmulator:Host"] = "hat-emulator";
        configuration["HatEmulator:Port"] = "5391";
        configuration["HatEmulator:AllowOutsideDevelopment"] = allow;

        var result = Validate(configuration, environment);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(warning => warning.StartsWith("HAT emulator mode"))
            .Which.Should().Contain("hat-emulator:5391").And.Contain("not the physical HAT");
    }

    [TestMethod]
    [DataRow("Production", "true")]
    [DataRow("Development", null)]
    public void HatEmulatorMode_WithTheHatDeviceMapped_Fails(string environment, string? allow)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["HatEmulator:Host"] = "hat-emulator";
        configuration["HatEmulator:AllowOutsideDevelopment"] = allow;

        var result = Validate(configuration, environment, hatBusPresent: true);

        result.Problems.Should().ContainSingle().Which.Should()
            .StartWith($"HatEmulator:Enabled is true and the HAT's I2C device ({DeploymentValidator.HatBusDevicePath}) is mapped")
            .And.Contain("secrets directory");
    }

    [TestMethod]
    public void HatEmulatorMode_OutsideDevelopment_WithoutConsent_AndTheHatDeviceMapped_ReportsBoth()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["HatEmulator:Host"] = "hat-emulator";

        var result = Validate(configuration, "Production", hatBusPresent: true);

        result.Problems.Should().HaveCount(2)
            .And.Contain(problem => problem.Contains(DeploymentValidator.HatBusDevicePath))
            .And.Contain(problem => problem.Contains("without HatEmulator:AllowOutsideDevelopment"));
    }

    [TestMethod]
    [DataRow("HatEmulator:Port", "0", "HatEmulator:Port must be between 1 and 65535.")]
    [DataRow("HatEmulator:Host", " ", "HatEmulator:Host")]
    [DataRow("HatEmulator:Host", "hat-emulator:5291", "HatEmulator:Host must be a host name or an IP address, without a scheme or a port")]
    [DataRow("HatEmulator:Host", "http://hat-emulator", "HatEmulator:Host must be a host name or an IP address, without a scheme or a port")]
    [DataRow("HatEmulator:RequestTimeout", "00:00:00.010", "HatEmulator:RequestTimeout")]
    [DataRow("HatEmulator:Port", "not-a-port", "HatEmulator could not be read: the value of HatEmulator:Port is not a valid System.Int32.")]
    public void HatEmulatorMode_WithInvalidSettings_Fails(string key, string value, string expected)
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["HatEmulator:AllowOutsideDevelopment"] = "true";
        configuration[key] = value;

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle().Which.Should().StartWith(expected);
        result.Problems.Should().NotContain(problem => problem.Contains("not-a-port"));
    }

    [TestMethod]
    public void HatEmulatorMode_Disabled_IgnoresTheOtherEmulatorSettings()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "false";
        configuration["HatEmulator:Port"] = "0";

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().NotContain(warning => warning.Contains("emulator"));
    }

    [TestMethod]
    public void HatEmulatorMode_WithIgnoredLimitSwitches_Fails_BecauseTheControllerTreatsTheEmulatorAsHardware()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["HatEmulator:Enabled"] = "true";
        configuration["RoofControllerOptionsV4:IgnorePhysicalLimitSwitches"] = "true";

        var result = Validate(configuration, "Development", hatBusPresent: false);

        result.Problems.Should().ContainSingle(problem =>
            problem.StartsWith("RoofControllerOptionsV4: IgnorePhysicalLimitSwitches is true without AllowIgnoringLimitSwitchesOnPhysicalHardware")
            && problem.Contains("HAT emulator mode is on"));
    }

    [TestMethod]
    public void DefaultCertificate_IsCheckedWithOnlyHttpListeners()
    {
        // Kestrel loads Kestrel:Certificates:Default at startup even when no listener is https.
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Kestrel:Certificates:Default:Path"] = Path.Combine(_directory, "left-over.pfx");

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("Kestrel:Certificates:Default") && problem.Contains("does not exist"));
    }

    [TestMethod]
    public void KestrelEndpoint_WithoutAUrl_Fails()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Kestrel:Endpoints:Https:Certificate:Password"] = CertificatePassword;

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem => problem.StartsWith("Kestrel:Endpoints:Https has no Url"));
        result.Problems.Should().NotContain(problem => problem.Contains(CertificatePassword));
    }

    [TestMethod]
    public void HttpKestrelEndpoint_WithHttpsOnlySettings_Fails()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Kestrel:Endpoints:Http:Url"] = "http://localhost:8080";
        configuration["Kestrel:Endpoints:Http:Certificate:Path"] = Path.Combine(_directory, "unused.pfx");
        configuration["Kestrel:Endpoints:Http:SslProtocols:0"] = "Tls12";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle().Which.Should()
            .StartWith("Kestrel:Endpoints:Http is an http endpoint with HTTPS-only settings (Certificate, SslProtocols)");
    }

    [TestMethod]
    public void KestrelEndpoints_WithoutALocalhost8080Listener_Fail()
    {
        // Kestrel:Endpoints win over the image's ASPNETCORE_URLS=http://+:8080, so nothing serves the health check.
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Kestrel:Endpoints:Lan:Url"] = "http://+:9090";

        var result = Validate(configuration);

        result.Problems.Should().ContainSingle(problem =>
            problem.StartsWith("No listener serves http://localhost:8080") && problem.Contains("http://+:9090 (from Kestrel:Endpoints; ignored: ASPNETCORE_URLS)"));
    }

    [TestMethod]
    [DataRow("http://localhost:8080", true)]
    [DataRow("HTTP://LOCALHOST:8080/", true)]
    [DataRow("http://+:8080", true)]
    [DataRow("http://*:8080", true)]
    [DataRow("http://0.0.0.0:8080", true)]
    [DataRow("http://[::]:8080", true)]
    [DataRow("http://127.0.0.1:8080", true)]
    [DataRow("http://[::1]:8080", true)]
    [DataRow("http://[0:0:0:0:0:0:0:1]:8080", true)]
    [DataRow("http://roof-pi:8080", true)]
    [DataRow("http://roof-pi.local:8080/", true)]
    [DataRow("http://127.0.0.2:8080", false)]
    [DataRow("http://192.0.2.1:8080", false)]
    [DataRow("http://[fe80::1]:8080", false)]
    [DataRow("https://localhost:8080", false)]
    [DataRow("http://localhost:5000", false)]
    [DataRow("http://localhost", false)]
    [DataRow("http://10.0.0.5:8080", false)]
    [DataRow("http://[::1]8080", false)]
    public void ServesLocalHttp8080_MatchesTheHealthCheckAddress(string address, bool expected)
        => DeploymentValidator.ServesLocalHttp8080(address).Should().Be(expected);

    [TestMethod]
    public void InvalidLogLevels_Fail_AndNameTheSettingOnly()
    {
        var configuration = OperatorKeys();
        configuration["RoofControllerSecurity:RequireHttps"] = "false";
        configuration["Logging:LogLevel:Default"] = "Info";
        configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "warning";
        configuration["Logging:Console:LogLevel:Default"] = "Loud";
        configuration["Logging:LogLevel:Microsoft:AspNetCore"] = "Quiet";
        configuration["Logging:LogLevel:Microsoft:Hosting"] = "Information";

        var result = Validate(configuration);

        result.Problems.Should().HaveCount(3);
        result.Problems.Should().Contain(problem => problem.StartsWith("Logging:LogLevel:Default is not a log level"));
        result.Problems.Should().Contain(problem => problem.StartsWith("Logging:Console:LogLevel:Default is not a log level"));
        result.Problems.Should().Contain(problem => problem.StartsWith("Logging:LogLevel:Microsoft:AspNetCore is not a log level"));
        result.Problems.Should().NotContain(problem => problem.Contains("Info,") || problem.Contains("Loud") || problem.Contains("Quiet"));
    }

    private DeploymentValidationResult Validate(Dictionary<string, string?> configuration, string environment = "Production", bool hatBusPresent = false)
        => DeploymentValidator.Validate(Build(configuration), new StubHostEnvironment(environment, _directory), new FixedTimeProvider(Now), hatBusPresent);

    private (int ExitCode, string Report) Run(Dictionary<string, string?> configuration)
    {
        using var output = new StringWriter();
        var exitCode = DeploymentValidator.Run(Build(configuration), new StubHostEnvironment("Production", _directory), new FixedTimeProvider(Now), output);
        return (exitCode, output.ToString());
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> OperatorKeys() => new()
    {
        // The image's ASPNETCORE_URLS, as Kestrel reads it.
        ["urls"] = "http://+:8080",
        ["AllowedHosts"] = "roof-pi;localhost",
        ["RoofControllerSecurity:ApiKeys:0:Name"] = "operator",
        ["RoofControllerSecurity:ApiKeys:0:Role"] = "RoofOperator",
        ["RoofControllerSecurity:ApiKeys:0:Key"] = OperatorKey
    };

    private static Dictionary<string, string?> HttpsDeployment(string certificatePath)
    {
        var configuration = OperatorKeys();

        // "urls" is ASPNETCORE_URLS as Kestrel reads it (the ASPNETCORE_ environment provider strips the prefix).
        configuration["urls"] = "http://localhost:8080;https://+:8443";
        configuration["Kestrel:Certificates:Default:Path"] = certificatePath;
        configuration["Kestrel:Certificates:Default:Password"] = CertificatePassword;
        return configuration;
    }

    private static Dictionary<string, string?> PemDeployment(string certificatePath, string? keyPath)
    {
        var configuration = OperatorKeys();
        configuration["urls"] = "http://localhost:8080;https://+:8443";
        configuration["Kestrel:Certificates:Default:Path"] = certificatePath;
        if (keyPath is not null)
        {
            configuration["Kestrel:Certificates:Default:KeyPath"] = keyPath;
        }

        return configuration;
    }

    private string WritePfx(string name, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] enhancedKeyUsages)
    {
        using var certificate = CreateCertificate(notBefore, notAfter, enhancedKeyUsages);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, CertificatePassword));
        return path;
    }

    /// <summary>Writes the certificate (PEM, or DER) and its PKCS#8 PEM key (encrypted when a password is given).</summary>
    private (string CertificatePath, string KeyPath) WritePem(X509Certificate2 certificate, bool derCertificate = false, string? keyPassword = null)
    {
        var certificatePath = Path.Combine(_directory, derCertificate ? "roof.der" : "roof.crt");
        var keyPath = Path.Combine(_directory, "roof.key");
        if (derCertificate)
        {
            File.WriteAllBytes(certificatePath, certificate.RawData);
        }
        else
        {
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        }

        using AsymmetricAlgorithm key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey()!;
        File.WriteAllText(keyPath, keyPassword is null
            ? key.ExportPkcs8PrivateKeyPem()
            : key.ExportEncryptedPkcs8PrivateKeyPem(keyPassword, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        return (certificatePath, keyPath);
    }

    private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] enhancedKeyUsages)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=roof-controller.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (enhancedKeyUsages.Length > 0)
        {
            var usages = new OidCollection();
            foreach (var usage in enhancedKeyUsages)
            {
                usages.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 CreateEcdsaCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=roof-controller.test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHostEnvironment(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "HVO.RoofControllerV4.RPi.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
