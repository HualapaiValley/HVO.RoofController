using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Production security posture: HTTPS requirement, OpenAPI protection, and fail-closed behaviour without keys.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SecurityHostingTests
{
    private const string StatusPath = "/api/v4.0/RoofControl/Status";
    private static readonly IPAddress LanClient = IPAddress.Parse("192.168.1.50");

    [TestMethod]
    public async Task OpenApi_InProduction_RequiresAdminKey()
    {
        using var host = new RoofApiTestHost(environment: "Production");
        using var anonymous = host.CreateApiClient(https: true);
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer, https: true);
        using var admin = host.CreateApiClient(TestApiKeys.Admin, https: true);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v4.json")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/openapi/v4.json")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync("/openapi/v4.json")).StatusCode);
    }

    [TestMethod]
    public async Task OpenApi_InDevelopment_IsOpen()
    {
        using var host = new RoofApiTestHost();
        using var client = host.CreateApiClient();

        var response = await client.GetAsync("/openapi/v4.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await ApiJson.ReadElementAsync(response);
        document.GetProperty("openapi").GetString().Should().StartWith("3.");
        document.GetProperty("paths").EnumerateObject().Should().Contain(p => p.Name.Contains("RoofControl/Status", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RequireHttps_ProductionDefault_RefusesPlainHttpFromTheLan()
    {
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: LanClient);
        using var plain = host.CreateApiClient(TestApiKeys.Viewer);
        using var secure = host.CreateApiClient(TestApiKeys.Viewer, https: true);

        var refused = await plain.GetAsync(StatusPath);
        var live = await plain.GetAsync("/health/live");
        var accepted = await secure.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await ApiJson.ReadElementAsync(refused);
        Assert.AreEqual("https_required", problem.GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode, "liveness stays reachable over plain HTTP");
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        Assert.IsNull(refused.Headers.Location, "never redirect a request that carries a key");
    }

    [TestMethod]
    public async Task RequireHttps_ProductionDefault_AllowsLoopbackPlainHttp()
    {
        // Docker healthcheck and the deploy script's docker-exec calls use http://localhost inside the container.
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: IPAddress.Loopback);
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task RequireHttps_ExplicitlyDisabled_AllowsPlainHttpFromTheLan()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:RequireHttps"] = "false" },
            environment: "Production",
            remoteIp: LanClient);
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task NoKeysConfigured_ProtectedEndpointsFailClosed()
    {
        using var host = new RoofApiTestHost(includeDefaultKeys: false);
        using var client = host.CreateApiClient(TestApiKeys.Admin);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(StatusPath)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v4.0/RoofControl/Stop", content: null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v4.0/RoofControl/Open", content: null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        host.RoofService.Verify(s => s.Open(), Moq.Times.Never);
        host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<Common.Models.RoofControllerStopReason>()), Moq.Times.Never);
    }

    [TestMethod]
    public async Task ShortConfiguredKey_IsIgnored()
    {
        using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "too-short",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = "RoofAdmin",
            ["RoofControllerSecurity:ApiKeys:4:Key"] = "short-test-key"
        });
        using var client = host.CreateApiClient("short-test-key");

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
