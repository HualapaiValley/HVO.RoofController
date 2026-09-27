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
        configuration["ASPNETCORE_URLS"] = "http://+:8080";

        var result = Validate(configuration);

        result.IsValid.Should().BeFalse();
        result.Problems.Should().ContainSingle(problem => problem.Contains("no HTTPS listener") && problem.Contains("403 https_required"));
    }

    [TestMethod]
    public void ExplicitHttpOptOut_PassesWithAClearTextWarning()
    {
        var configuration = OperatorKeys();
        configuration["ASPNETCORE_URLS"] = "http://+:8080";
        configuration["RoofControllerSecurity:RequireHttps"] = "false";

        var result = Validate(configuration);

        result.Problems.Should().BeEmpty();
        result.Warnings.Should().Contain(warning => warning.Contains("clear text"));
    }

    [TestMethod]
    public void Development_DoesNotRequireHttps()
    {
        var configuration = OperatorKeys();
        configuration["ASPNETCORE_URLS"] = "http://+:8080";

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
        configuration["ASPNETCORE_URLS"] = "http://localhost:8080;https://+:8443";

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
        configuration["ASPNETCORE_URLS"] = "http://localhost:8080;https://+:8443";
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

    private DeploymentValidationResult Validate(Dictionary<string, string?> configuration, string environment = "Production")
        => DeploymentValidator.Validate(Build(configuration), new StubHostEnvironment(environment, _directory), new FixedTimeProvider(Now));

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
        ["AllowedHosts"] = "roof-pi;localhost",
        ["RoofControllerSecurity:ApiKeys:0:Name"] = "operator",
        ["RoofControllerSecurity:ApiKeys:0:Role"] = "RoofOperator",
        ["RoofControllerSecurity:ApiKeys:0:Key"] = OperatorKey
    };

    private static Dictionary<string, string?> HttpsDeployment(string certificatePath)
    {
        var configuration = OperatorKeys();
        configuration["ASPNETCORE_URLS"] = "http://localhost:8080;https://+:8443";
        configuration["Kestrel:Certificates:Default:Path"] = certificatePath;
        configuration["Kestrel:Certificates:Default:Password"] = CertificatePassword;
        return configuration;
    }

    private string WritePfx(string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var certificate = CreateCertificate(notBefore, notAfter);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, CertificatePassword));
        return path;
    }

    private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=roof-controller.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
