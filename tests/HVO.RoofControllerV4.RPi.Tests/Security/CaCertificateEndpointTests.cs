using System.Runtime.Versioning;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// GET /ca.crt: the CA that issued the controller's certificate, for clients to trust, since the TLS handshake leaves a
/// self-signed root out.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
[DoNotParallelize]
public sealed class CaCertificateEndpointTests
{
    private static readonly IPAddress LanClient = IPAddress.Parse("192.168.1.50");

    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-ca-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public async Task TheCa_IsServedToAnyone_OverHttps_WithoutItsKey()
    {
        var path = Path.Combine(_directory, "roof-controller.pfx");
        using var authority = InstallerCertificates.WritePfx(path, DateTimeOffset.UtcNow);
        using var host = Host(path);
        using var anonymous = host.CreateApiClient(https: true);

        var response = await anonymous.GetAsync("/ca.crt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-x509-ca-cert");
        var pem = await response.Content.ReadAsStringAsync();
        pem.Should().Be(authority.ExportCertificatePem() + "\n").And.NotContain("PRIVATE KEY");
        using var served = X509Certificate2.CreateFromPem(pem);
        served.RawData.Should().Equal(authority.RawData);
    }

    [TestMethod]
    public async Task WithoutACa_ItIsNotFound()
    {
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: LanClient);
        using var anonymous = host.CreateApiClient(https: true);

        (await anonymous.GetAsync("/ca.crt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task OverPlainHttpFromTheLan_ItIsRefused()
    {
        var path = Path.Combine(_directory, "roof-controller.pfx");
        InstallerCertificates.WritePfx(path, DateTimeOffset.UtcNow).Dispose();
        using var host = Host(path);
        using var plain = host.CreateApiClient();

        (await plain.GetAsync("/ca.crt")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static RoofApiTestHost Host(string path) => new(
        settings: new Dictionary<string, string?>
        {
            [$"{RoofServerCertificate.Section}:Path"] = path,
            [$"{RoofServerCertificate.Section}:Password"] = InstallerCertificates.Password
        },
        environment: "Production",
        remoteIp: LanClient);
}
