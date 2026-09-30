using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.RoofControllerV4.RPi.Tests.HealthChecks;

/// <summary>The served certificate's health: its expiry, and a file that cannot be read.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class RoofCertificateHealthCheckTests
{
    private static readonly DateTimeOffset Made = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-certificate-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public async Task NoCertificateFile_IsHealthy()
    {
        var result = await CheckAsync(new Dictionary<string, string?>(), Made);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("configured").WhoseValue.Should().Be(false);
    }

    [TestMethod]
    public async Task AValidCertificate_IsHealthy_AndNamesItsCa()
    {
        using var authority = InstallerCertificates.WritePfx(Path.Combine(_directory, "roof-controller.pfx"), Made);

        var result = await CheckAsync(Configured("roof-controller.pfx"), Made.AddDays(1));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("The HTTPS certificate is valid until 2027-11-02.");
        result.Data["subject"].Should().Be("roofpi");
        result.Data["daysLeft"].Should().Be(396);
        result.Data["sha256"].Should().BeOfType<string>().Which.Should().MatchRegex("^([0-9A-F]{2}:){31}[0-9A-F]{2}$");
        result.Data["issuerSha256"].Should().Be(ControllerCertificates.Fingerprint(authority));
        result.Data["issuer"].Should().Be(authority.GetNameInfo(X509NameType.SimpleName, false));
    }

    [TestMethod]
    public async Task WithinThirtyDaysOfExpiry_IsDegraded_AndSaysHowToRenew()
    {
        InstallerCertificates.WritePfx(Path.Combine(_directory, "roof-controller.pfx"), Made).Dispose();

        var result = await CheckAsync(Configured("roof-controller.pfx"), Made.AddDays(380));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("The HTTPS certificate expires on 2027-11-02, in 17 days. Renew it on the controller with: sudo hvo-roof-install cert");
    }

    [TestMethod]
    public async Task AnExpiredCertificate_IsUnhealthy()
    {
        InstallerCertificates.WritePfx(Path.Combine(_directory, "roof-controller.pfx"), Made).Dispose();

        var result = await CheckAsync(Configured("roof-controller.pfx"), Made.AddDays(400));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().StartWith("The HTTPS certificate expired on 2027-11-02: clients refuse it.");
        result.Data["daysLeft"].Should().Be(-3);
    }

    [TestMethod]
    public async Task AWrongPassword_IsUnhealthy_WithoutThePasswordOrThePath()
    {
        InstallerCertificates.WritePfx(Path.Combine(_directory, "roof-controller.pfx"), Made).Dispose();
        var settings = Configured("roof-controller.pfx");
        settings[$"{RoofServerCertificate.Section}:Password"] = "wrong-password-not-a-secret";

        var result = await CheckAsync(settings, Made);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The HTTPS certificate cannot be checked: the configured certificate file cannot be opened (CryptographicException).");
        result.Description.Should().NotContain("wrong-password").And.NotContain(_directory);
    }

    [TestMethod]
    public async Task AMissingFile_IsUnhealthy()
    {
        var result = await CheckAsync(Configured("missing.pfx"), Made);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The HTTPS certificate cannot be checked: the configured certificate file does not exist.");
    }

    [TestMethod]
    public async Task ARenewedCertificate_IsReadAgain()
    {
        var path = Path.Combine(_directory, "roof-controller.pfx");
        InstallerCertificates.WritePfx(path, Made).Dispose();
        var certificate = new RoofServerCertificate(Build(Configured("roof-controller.pfx")), new TestHostEnvironment { ContentRootPath = _directory });
        var before = certificate.Read();

        using var renewed = InstallerCertificates.WritePfx(path, Made.AddDays(370));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var after = certificate.Read();

        after.NotAfter.Should().BeAfter(before.NotAfter!.Value);
        after.AuthorityFingerprint.Should().Be(ControllerCertificates.Fingerprint(renewed));
    }

    [TestMethod]
    public void ASelfSignedCertificate_HasNoCa()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Made);
        File.WriteAllBytes(Path.Combine(_directory, "roof-controller.pfx"), ControllerCertificates.ExportPfx(selfSigned, null, InstallerCertificates.Password));

        var state = new RoofServerCertificate(Build(Configured("roof-controller.pfx")), new TestHostEnvironment { ContentRootPath = _directory }).Read();

        state.Problem.Should().BeNull();
        state.Fingerprint.Should().Be(ControllerCertificates.Fingerprint(selfSigned));
        state.AuthorityPem.Should().BeNull();
    }

    [TestMethod]
    public void APemChain_NamesItsCa()
    {
        var (authority, issued) = InstallerCertificates.Issue(Made);
        using (authority)
        using (issued)
        {
            File.WriteAllText(Path.Combine(_directory, "roof.crt"), issued.ExportCertificatePem() + "\n" + authority.ExportCertificatePem() + "\n");

            var state = new RoofServerCertificate(Build(Configured("roof.crt")), new TestHostEnvironment { ContentRootPath = _directory }).Read();

            state.Fingerprint.Should().Be(ControllerCertificates.Fingerprint(issued));
            state.AuthorityPem.Should().Be(authority.ExportCertificatePem());
        }
    }

    private Dictionary<string, string?> Configured(string path) => new()
    {
        [$"{RoofServerCertificate.Section}:Path"] = path,
        [$"{RoofServerCertificate.Section}:Password"] = InstallerCertificates.Password
    };

    private Task<HealthCheckResult> CheckAsync(Dictionary<string, string?> settings, DateTimeOffset now)
        => new RoofCertificateHealthCheck(
                new RoofServerCertificate(Build(settings), new TestHostEnvironment { ContentRootPath = _directory }),
                new FixedTime(now))
            .CheckHealthAsync(new HealthCheckContext());

    private static IConfiguration Build(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
