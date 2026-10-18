using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using TemporaryDirectory = HVO.RoofControllerV4.RPi.Tests.Security.TemporaryDirectory;

namespace HVO.RoofControllerV4.RPi.Tests.HealthChecks;

/// <summary>Deployment availability must include the real security checks even when a plant fault is acceptable.</summary>
[TestClass]
[DoNotParallelize]
[UnsupportedOSPlatform("windows")]
public sealed class RoofControllerAvailabilityEndpointTests
{
    private static readonly DateTimeOffset Made = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("none", HttpStatusCode.OK)]
    [DataRow("missing-certificate", HttpStatusCode.ServiceUnavailable)]
    [DataRow("wrong-certificate-password", HttpStatusCode.ServiceUnavailable)]
    [DataRow("expired-certificate", HttpStatusCode.ServiceUnavailable)]
    [DataRow("corrupt-identity", HttpStatusCode.ServiceUnavailable)]
    public async Task AStoppedDriveFault_IsAvailable_OnlyWhileCertificateAndIdentityAreHealthy(string failure, HttpStatusCode expected)
    {
        using var directory = new TemporaryDirectory();
        var certificatePath = directory.File("roof-controller.pfx");
        var identityPath = directory.File("identity.json");
        if (failure != "missing-certificate")
        {
            InstallerCertificates.WritePfx(certificatePath, Made).Dispose();
        }
        if (failure == "corrupt-identity")
        {
            await File.WriteAllTextAsync(identityPath, "{ not json");
        }

        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(
            FakeRoofControllerService.HealthySnapshot() with
            {
                Status = RoofControllerStatus.Error,
                IsFaultLatched = true,
                LatchedFaultReason = RoofControllerStopReason.DriveFault,
                IsDriveFaultActive = true
            });
        using var host = new RoofApiTestHost(roof, settings: new Dictionary<string, string?>
        {
            [$"{RoofServerCertificate.Section}:Path"] = certificatePath,
            [$"{RoofServerCertificate.Section}:Password"] = failure == "wrong-certificate-password"
                ? "wrong-password-not-a-secret" : InstallerCertificates.Password,
            ["RoofControllerSecurity:Identity:StorePath"] = identityPath
        }, configureServices: services => services.AddSingleton<TimeProvider>(
            new ManualTimeProvider(Made.AddDays(failure == "expired-certificate" ? 400 : 1))));
        using var anonymous = host.CreateApiClient();
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);

        // Exercise the mapped HTTP endpoint, not its registration tags. Dropping security from its predicate makes
        // each security-failure row return 200 and fail here; the fault-only row also protects the plant exception.
        using var availability = await anonymous.GetAsync("/health/available");
        availability.StatusCode.Should().Be(expected);
        (await availability.Content.ReadAsStringAsync()).Should().Be(expected == HttpStatusCode.OK ? "Degraded" : "Unhealthy");

        using var detailed = await viewer.GetAsync("/health");
        detailed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the DriveFault remains operationally unhealthy");
        var report = await ApiJson.ReadElementAsync(detailed);
        var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(check => check.GetProperty("name").GetString()!);
        checks["roof_controller"].GetProperty("status").GetString().Should().Be("Unhealthy");
        checks["https_certificate"].GetProperty("status").GetString().Should().Be(
            failure is "missing-certificate" or "wrong-certificate-password" or "expired-certificate" ? "Unhealthy" : "Healthy");
        checks["identity_store"].GetProperty("status").GetString().Should().Be(failure == "corrupt-identity" ? "Unhealthy" : "Healthy");
    }
}
