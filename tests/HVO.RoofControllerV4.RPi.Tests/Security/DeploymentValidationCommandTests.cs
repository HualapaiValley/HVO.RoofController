using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// <c>--validate-deployment</c> as <c>Program.Main</c> runs it: the real configuration sources (command line, content
/// root appsettings, key-per-file secrets), with the secrets directory redirected to a temporary directory.
/// </summary>
[TestClass]
public sealed class DeploymentValidationCommandTests
{
    private const string OperatorKey = "deploy-command-operator-key-00000001";
    private const string CertificatePassword = "deploy-command-pfx-password";

    private string _contentRoot = null!;
    private string _secrets = null!;

    [TestInitialize]
    public void CreateDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-deploy-command-" + Guid.NewGuid().ToString("N"));
        _contentRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        _secrets = Directory.CreateDirectory(Path.Combine(root, "secrets")).FullName;
    }

    [TestCleanup]
    public void DeleteDirectories() => Directory.Delete(Path.GetDirectoryName(_contentRoot)!, recursive: true);

    [TestMethod]
    public void KeysFromTheSecretsDirectory_Pass()
    {
        WriteAppSettings("""{ "AllowedHosts": "roof-pi;localhost", "RoofControllerSecurity": { "RequireHttps": false } }""");
        WriteOperatorKeySecrets();

        var (exitCode, report) = RunValidation();

        exitCode.Should().Be(0, report);
        report.Should().Contain("Deployment check (Production):").And.Contain("API keys: operator (RoofOperator)");
        report.Should().Contain("Deployment check passed.").And.NotContain("PROBLEM").And.NotContain(OperatorKey);
    }

    [TestMethod]
    public void TheSwitch_IsRemovedBeforeTheCommandLineIsParsed()
    {
        // Left in, the command-line provider would read "--validate-deployment" as a key with "--contentRoot" as its value,
        // so the content root (and its appsettings.json turning RequireHttps off) would be lost and the check would fail.
        WriteAppSettings("""{ "AllowedHosts": "localhost", "RoofControllerSecurity": { "RequireHttps": false } }""");
        WriteOperatorKeySecrets();

        var (exitCode, report) = RunValidation(DeploymentValidator.CommandLineSwitch, "--contentRoot", _contentRoot, "--environment", "Production");

        exitCode.Should().Be(0, report);
        report.Should().Contain("HTTPS required: no");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AUrlsSecretFile_OverridesTheCommandLineUrls(bool httpOnlyUrlsSecret)
    {
        // Secrets are the last configuration source, so /run/secrets/urls wins over ASPNETCORE_URLS and the command line.
        var certificatePath = Path.Combine(_contentRoot, "roof.pfx");
        using (var certificate = CreateCertificate())
        {
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pkcs12, CertificatePassword));
        }

        WriteAppSettings("""{ "AllowedHosts": "localhost", "Kestrel": { "Certificates": { "Default": { "Path": "roof.pfx" } } } }""");
        WriteOperatorKeySecrets();
        WriteSecret("Kestrel__Certificates__Default__Password", CertificatePassword);
        if (httpOnlyUrlsSecret)
        {
            WriteSecret("urls", "http://+:8080");
        }

        var (exitCode, report) = RunValidation(
            DeploymentValidator.CommandLineSwitch,
            "--contentRoot", _contentRoot,
            "--environment", "Production",
            "--urls", "http://localhost:8080;https://+:8443");

        if (httpOnlyUrlsSecret)
        {
            exitCode.Should().Be(1, report);
            report.Should().Contain("PROBLEM: RoofControllerSecurity:RequireHttps is in effect but no HTTPS listener is configured");
        }
        else
        {
            exitCode.Should().Be(0, report);
            report.Should().Contain("https://+:8443 (from ASPNETCORE_URLS)");
        }

        report.Should().NotContain(CertificatePassword).And.NotContain(OperatorKey);
    }

    [TestMethod]
    public void ConfigurationThatCannotBeLoaded_IsReportedAsAProblem_WithoutValues()
    {
        const string value = "value-inside-malformed-json";
        WriteAppSettings("{ \"BlueIris\": { \"Password\": \"" + value + "\" ");

        var (exitCode, report) = RunValidation();

        exitCode.Should().Be(1);
        report.Should().Contain("PROBLEM: The deployment check could not complete (InvalidDataException: ");
        report.Should().Contain("Deployment check FAILED: 1 problem(s).");
        report.Should().NotContain(value);
    }

    private (int ExitCode, string Report) RunValidation(params string[] args)
    {
        if (args.Length == 0)
        {
            args = [DeploymentValidator.CommandLineSwitch, "--contentRoot", _contentRoot, "--environment", "Production"];
        }

        // No file watchers for a configuration that is read once.
        using var output = new StringWriter();
        var exitCode = Program.RunValidation([.. args, "--hostBuilder:reloadConfigOnChange", "false"], _secrets, output);
        return (exitCode, output.ToString());
    }

    private void WriteAppSettings(string json) => File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), json);

    private void WriteOperatorKeySecrets()
    {
        WriteSecret("RoofControllerSecurity__ApiKeys__0__Name", "operator");
        WriteSecret("RoofControllerSecurity__ApiKeys__0__Role", "RoofOperator");
        WriteSecret("RoofControllerSecurity__ApiKeys__0__Key", OperatorKey);
    }

    private void WriteSecret(string name, string value) => File.WriteAllText(Path.Combine(_secrets, name), value);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=roof-controller.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }
}
