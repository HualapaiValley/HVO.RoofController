using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server's own protections (#220): it answers only its LAN (the allowlist), only for its own host names (DNS
/// rebinding), never a web page of another origin (CSRF), sends no CORS headers, and limits polling per address but
/// never AbortSlew.
/// </summary>
[TestClass]
public sealed class AlpacaSecurityTests
{
    [TestMethod]
    [DataRow("203.0.113.9", DisplayName = "a public address")]
    [DataRow("100.64.1.2", DisplayName = "carrier-grade NAT (overlay VPNs)")]
    public async Task AClientOutsideTheAllowedNetworks_IsRefused_BeforeAnythingElse(string address)
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof), remote: IPAddress.Parse(address), waitForStatus: false);

        using var read = await alpaca.Http.GetAsync("/api/v1/dome/0/name");
        using var stop = await alpaca.PutRawAsync("abortslew", "ClientID=1");
        using var management = await alpaca.Http.GetAsync("/management/apiversions");

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        stop.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        management.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task AllowedNetworks_NarrowWhoIsAnswered()
    {
        var settings = new Dictionary<string, string?> { ["Alpaca:AllowedNetworks:0"] = "192.168.7.0/24" };
        await using var inside = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), settings, IPAddress.Parse("192.168.7.30"), waitForStatus: false);
        await using var outside = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), settings, IPAddress.Parse("192.168.1.50"), waitForStatus: false);

        (await inside.Http.GetAsync("/api/v1/dome/0/name")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await outside.Http.GetAsync("/api/v1/dome/0/name")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AnIPv4ClientOnADualStackSocket_IsJudgedAsIPv4()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), remote: IPAddress.Parse("::ffff:192.168.1.50"), waitForStatus: false);

        (await alpaca.Http.GetAsync("/api/v1/dome/0/name")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("192.168.1.20:11111", true)]
    [DataRow("[fe80::1]:11111", true)]
    [DataRow("localhost:11111", true)]
    [DataRow("roof.example.org", true)]
    [DataRow("ROOF.EXAMPLE.ORG.", true)]
    [DataRow("attacker.example.com:11111", false)]
    [DataRow("192.168.1.20.attacker.example.com", false)]
    public async Task TheHostHeader_MustBeAnAddressOrANameTheServerAnswersFor(string host, bool allowed)
    {
        var settings = new Dictionary<string, string?> { ["Alpaca:AllowedHosts:0"] = "roof.example.org" };
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), settings, waitForStatus: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dome/0/name");
        request.Headers.Host = host;
        using var response = await alpaca.Http.SendAsync(request);

        response.StatusCode.Should().Be(allowed ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task AWebPageOfAnotherOrigin_IsRefused()
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof), waitForStatus: false);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/dome/0/abortslew")
        {
            Content = new StringContent("ClientID=1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("Origin", "http://attacker.example.com");
        using var response = await alpaca.Http.SendAsync(request);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dome/0/name");
        sameOrigin.Headers.Add("Origin", "http://localhost");
        using var allowed = await alpaca.Http.SendAsync(sameOrigin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the setup page's own origin is not another site");
    }

    [TestMethod]
    public async Task NoAnswer_CarriesCorsHeaders()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/dome/0/abortslew");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        using var preflightResponse = await alpaca.Http.SendAsync(preflight);
        using var read = await alpaca.Http.GetAsync("/api/v1/dome/0/name");

        foreach (var response in new[] { preflightResponse, read })
        {
            response.Headers.Select(header => header.Key).Should().NotContain(name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        }
    }

    [TestMethod]
    [DataRow("/api/v1/dome/0/abortslew")]
    [DataRow("/api/v1/dome/0/abortslew/")]
    public async Task EveryRouteTakenAsAbortSlew_PassesAnExhaustedPollingLimit(string path)
    {
        var roof = RoofServiceMock.Create();
        var settings = new Dictionary<string, string?> { ["Alpaca:RequestsPerSecondPerAddress"] = "1" };
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof), settings, waitForStatus: false);
        HttpStatusCode last;
        var attempts = 0;
        do
        {
            using var read = await alpaca.Http.GetAsync("/api/v1/dome/0/name");
            last = read.StatusCode;
        }
        while (last != HttpStatusCode.TooManyRequests && ++attempts < 20);
        last.Should().Be(HttpStatusCode.TooManyRequests, "the polling limit is exhausted");

        using var stop = await alpaca.Http.PutAsync(path, new StringContent("ClientID=1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

        stop.StatusCode.Should().Be(HttpStatusCode.OK);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public async Task Polling_IsLimitedPerAddress_ButAbortSlewNeverIs()
    {
        var roof = RoofServiceMock.Create();
        var settings = new Dictionary<string, string?> { ["Alpaca:RequestsPerSecondPerAddress"] = "1" };
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof), settings, waitForStatus: false);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await alpaca.Http.GetAsync("/api/v1/dome/0/name");
            statuses.Add(response.StatusCode);
        }

        using var stop = await alpaca.PutRawAsync("abortslew", "ClientID=1");

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        stop.StatusCode.Should().Be(HttpStatusCode.OK);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }
}
