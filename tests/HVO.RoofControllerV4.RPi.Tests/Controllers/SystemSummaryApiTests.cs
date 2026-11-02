using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// <c>GET api/v4.0/System/Summary</c> (#209): the controller's version and basic resource use for every role, where the
/// detailed host information stays admin-only.
/// </summary>
[TestClass]
public sealed class SystemSummaryApiTests
{
    private const string Summary = "/api/v4.0/System/Summary";
    private const string KioskKey = "test-kiosk-key-not-a-real-secret-05";

    [TestMethod]
    [DataRow(TestApiKeys.Viewer)]
    [DataRow(TestApiKeys.HashedViewer)]
    [DataRow(TestApiKeys.Operator)]
    [DataRow(TestApiKeys.Admin)]
    [DataRow(KioskKey)]
    public async Task EveryRole_ReadsTheSummary_WithTheControllersVersion(string key)
    {
        using var host = CreateHost();
        using var client = host.CreateApiClient(key);

        var response = await client.GetAsync(Summary);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await ApiJson.ReadAsync<SystemSummaryResponse>(response);
        summary.ApplicationVersion.Should().Be(RoofProductVersion.Of(typeof(Program).Assembly),
            "the controller reports its own version, not the test runner's");
        summary.ProcessorCount.Should().BePositive();
        summary.ProcessWorkingSetBytes.Should().BePositive();
        summary.GeneratedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        summary.IpAddresses.Should().NotBeNull();
    }

    [TestMethod]
    public async Task APinSession_ReadsTheSummary_ButNotTheDetailedInformation()
    {
        using var host = CreateHost();
        using (var admin = host.CreateApiClient(TestApiKeys.Admin))
        {
            (await admin.PostAsJsonAsync("/api/v4.0/Identity/Users", new RoofUserCreateRequest
            {
                Name = "olive",
                Role = RoofControllerApiContract.AdminRole,
                Password = TestSecrets.Password,
                Pin = TestSecrets.Pin
            })).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var kiosk = host.CreateApiClient(KioskKey);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync("/api/v4.0/Auth/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }));
        using var olive = host.CreateApiClient();
        olive.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, session.Token);

        (await olive.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await olive.GetAsync("/api/v1.0/System/info")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a PIN session is an operator's, even an admin's");
    }

    [TestMethod]
    public async Task AnAnonymousCaller_IsRefused()
    {
        using var host = CreateHost();
        using var anonymous = host.CreateApiClient();

        (await anonymous.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task TheSummary_NamesNoHostUserOrFile()
    {
        using var host = CreateHost();
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);

        var body = await ApiJson.ReadElementAsync(await viewer.GetAsync(Summary));

        body.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
        [
            "applicationVersion", "uptimeSeconds", "generatedAtUtc", "hostUptimeSeconds", "processorCount", "cpuUsagePercent",
            "loadAverage1Minute", "loadAverage5Minutes", "loadAverage15Minutes", "cpuTemperatureCelsius", "memoryTotalBytes",
            "memoryAvailableBytes", "processWorkingSetBytes", "diskTotalBytes", "diskFreeBytes", "webUiUrls", "webUiAccess", "hostAddresses",
            "ipAddresses", "runningInContainer", "alpacaServer"
        ], "the summary is for every role: the machine's name, the environment and paths stay in the admin-only System/info");
        body.GetRawText().Should().NotContain(Environment.MachineName);
    }

    [TestMethod]
    public async Task WhereTheWebUiIsReached_IsAsTheDeploymentWroteIt()
    {
        using var host = CreateHost(new Dictionary<string, string?>
        {
            // As the deploy script passes them (RoofHost__*), with documentation addresses.
            ["RoofHost:Addresses"] = "192.0.2.10;2001:db8::10",
            ["RoofHost:WebUiUrls"] = "https://192.0.2.10:8088/;https://[2001:db8::10]:8088/",
            ["RoofHost:WebUiAccess"] = "Network"
        });
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);

        var summary = await ApiJson.ReadAsync<SystemSummaryResponse>(await viewer.GetAsync(Summary));

        summary.WebUiUrls.Should().Equal("https://192.0.2.10:8088/", "https://[2001:db8::10]:8088/");
        summary.WebUiAccess.Should().Be(RoofWebUiAccess.Network);
        summary.HostAddresses.Should().Equal("192.0.2.10", "2001:db8::10");
    }

    [TestMethod]
    public async Task AlpacaSummary_IsOffByDefault_AndNeverClaimsAStaleClientCount()
    {
        using (var off = CreateHost())
        using (var viewer = off.CreateApiClient(TestApiKeys.Viewer))
        {
            var body = await ApiJson.ReadElementAsync(await viewer.GetAsync(Summary));
            body.GetProperty("alpacaServer").GetProperty("enabled").GetBoolean().Should().BeFalse();
        }

        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-summary-").FullName;
        var path = Path.Combine(directory, "status.json");
        try
        {
            using var host = CreateHost(new Dictionary<string, string?>
            {
                ["Alpaca:Enabled"] = "true",
                ["Alpaca:Discovery:AdvertisedPort"] = "47677",
                ["Alpaca:StatusFilePath"] = path
            });
            using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
            var missing = (await ApiJson.ReadElementAsync(await viewer.GetAsync(Summary))).GetProperty("alpacaServer");
            missing.GetProperty("enabled").GetBoolean().Should().BeTrue();
            missing.GetProperty("running").GetBoolean().Should().BeFalse();
            missing.GetProperty("port").GetInt32().Should().Be(47677);
            missing.GetProperty("domeConnectedClients").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

            var dome = Guid.NewGuid().ToString("D");
            var weather = Guid.NewGuid().ToString("D");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Port = 47677,
                DomeUniqueId = dome,
                SafetyMonitorUniqueId = weather,
                DomeConnectedClients = 2,
                SafetyMonitorConnectedClients = 1
            }));
            var fresh = (await ApiJson.ReadElementAsync(await viewer.GetAsync(Summary))).GetProperty("alpacaServer");
            fresh.GetProperty("running").GetBoolean().Should().BeTrue();
            fresh.GetProperty("domeUniqueId").GetString().Should().Be(dome);
            fresh.GetProperty("safetyMonitorUniqueId").GetString().Should().Be(weather);
            fresh.GetProperty("domeConnectedClients").GetInt32().Should().Be(2);
            fresh.GetProperty("safetyMonitorConnectedClients").GetInt32().Should().Be(1);

            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), Port = 47677,
                DomeUniqueId = dome, SafetyMonitorUniqueId = weather,
                DomeConnectedClients = 2, SafetyMonitorConnectedClients = 1
            }));
            var stale = (await ApiJson.ReadElementAsync(await viewer.GetAsync(Summary))).GetProperty("alpacaServer");
            stale.GetProperty("running").GetBoolean().Should().BeFalse();
            stale.GetProperty("domeConnectedClients").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            stale.GetProperty("safetyMonitorConnectedClients").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RoofApiTestHost CreateHost(IDictionary<string, string?>? extra = null)
        => new(
            settings: new Dictionary<string, string?>(extra ?? new Dictionary<string, string?>())
            {
                ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
                ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
                ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
                ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
            },
            configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000));
}
