using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.RPi.Tests.Client;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server's protocol (#220): the response envelope, parameters (a GET's names in any case, a PUT's exactly
/// cased and form-encoded), transaction numbers, malformed requests, the management API and the setup pages.
/// </summary>
[TestClass]
public sealed class AlpacaProtocolTests
{
    [TestMethod]
    public async Task APropertyAnswer_IsTheStandardEnvelope_InItsCasing()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var response = await alpaca.Http.GetAsync("/api/v1/dome/0/name?ClientID=7&ClientTransactionID=1234");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.EnumerateObject().Select(property => property.Name).Should().Equal(
            "Value", "ClientTransactionID", "ServerTransactionID", "ErrorNumber", "ErrorMessage");
        body.GetProperty("Value").GetString().Should().Be(AlpacaDome.DeviceName);
        body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(1234);
        body.GetProperty("ServerTransactionID").GetUInt32().Should().BeGreaterThan(0);
        body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        body.GetProperty("ErrorMessage").GetString().Should().BeEmpty();
    }

    [TestMethod]
    public async Task AMethodAnswer_HasNoValue()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        var body = await alpaca.PutAsync("connect", "ClientID=3&ClientTransactionID=55");

        body.TryGetProperty("Value", out _).Should().BeFalse();
        body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(55);
        body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
    }

    [TestMethod]
    public async Task ServerTransactionIds_CountUp()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        var first = (await alpaca.GetAsync("name")).GetProperty("ServerTransactionID").GetUInt32();
        var second = (await alpaca.GetAsync("description")).GetProperty("ServerTransactionID").GetUInt32();
        var third = (await alpaca.PutAsync("connect")).GetProperty("ServerTransactionID").GetUInt32();

        second.Should().BeGreaterThan(first);
        third.Should().BeGreaterThan(second);
    }

    [TestMethod]
    [DataRow("name?ClientID=1", 0u, DisplayName = "missing")]
    [DataRow("name?clienttransactionid=42", 42u, DisplayName = "lower case")]
    [DataRow("name?CLIENTTRANSACTIONID=43", 43u, DisplayName = "upper case")]
    [DataRow("name?ClientTransactionID=4294967295", 4294967295u, DisplayName = "the largest")]
    public async Task AGetsClientTransactionId_IsEchoed_WhateverTheCaseOfItsName(string path, uint expected)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var response = await alpaca.Http.GetAsync($"/api/v1/dome/0/{path}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(expected);
    }

    [TestMethod]
    [DataRow("ClientTransactionID=")]
    [DataRow("ClientTransactionID=%20")]
    [DataRow("ClientTransactionID=-1")]
    [DataRow("ClientTransactionID=abc")]
    [DataRow("ClientTransactionID=4294967296")]
    [DataRow("ClientID=")]
    [DataRow("ClientID=-5")]
    [DataRow("ClientID=x")]
    public async Task AClientOrTransactionIdThatIsNotANumber_IsAMalformedRequest(string query)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var get = await alpaca.Http.GetAsync($"/api/v1/dome/0/name?{query}");
        using var put = await alpaca.PutRawAsync("connect", query);

        get.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        get.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task APutsParameterNames_AreCaseSensitive()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var wronglyCased = await alpaca.PutRawAsync("connected", "connected=true&ClientID=1");
        var badlyCasedId = await alpaca.PutAsync("connected", "Connected=true&clientid=9&clienttransactionid=77");

        wronglyCased.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a PUT's Connected must be cased exactly");
        (await wronglyCased.Content.ReadAsStringAsync()).Should().Contain("Connected parameter is missing");
        badlyCasedId.GetProperty("ClientTransactionID").GetUInt32().Should().Be(0, "a wrongly cased ClientTransactionID is ignored");
        (await alpaca.GetAsync("connected", "ClientID=0")).GetProperty("Value").GetBoolean().Should().BeTrue("the wrongly cased ClientID was ignored, so client 0 connected");
    }

    [TestMethod]
    public async Task APutsParameters_MustBeFormEncoded()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var json = await alpaca.Http.PutAsync("/api/v1/dome/0/connected",
            new StringContent("{\"Connected\":true}", System.Text.Encoding.UTF8, "application/json"));
        using var inTheQuery = await alpaca.Http.PutAsync("/api/v1/dome/0/connected?Connected=true", null);

        json.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        inTheQuery.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a PUT's parameters are in its body, not its query string");
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("True", true)]
    [DataRow("FALSE", false)]
    public async Task ABooleanValue_IsReadWhateverItsCase(string value, bool connected)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        await alpaca.PutAsync("connected", $"Connected={value}&ClientID=1");

        (await alpaca.GetAsync("connected")).GetProperty("Value").GetBoolean().Should().Be(connected);
    }

    [TestMethod]
    [DataRow("GET", "/api/v1/dome/0/nosuchmember", DisplayName = "an unknown member")]
    [DataRow("GET", "/api/v1/dome/0/ShutterStatus", DisplayName = "a member not in lower case")]
    [DataRow("GET", "/api/v1/dome/1/name", DisplayName = "another device number")]
    [DataRow("GET", "/api/v1/telescope/0/name", DisplayName = "another device type")]
    [DataRow("GET", "/api/v1/Dome/0/name", DisplayName = "a device type not in lower case")]
    [DataRow("GET", "/api/V1/dome/0/name", DisplayName = "a capital V")]
    [DataRow("GET", "/api/v2/dome/0/name", DisplayName = "another API version")]
    [DataRow("GET", "/api/v1/dome/0/abortslew", DisplayName = "a method read with GET")]
    [DataRow("PUT", "/api/v1/dome/0/name", DisplayName = "a property written with PUT")]
    [DataRow("POST", "/api/v1/dome/0/connected", DisplayName = "POST")]
    [DataRow("DELETE", "/api/v1/dome/0/connected", DisplayName = "DELETE")]
    [DataRow("GET", "/management/v2/description", DisplayName = "an unknown management path")]
    public async Task AnUnknownEndpointOrMethod_IsAMalformedRequest_InPlainText(string method, string path)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = new StringContent("ClientID=1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var response = await alpaca.Http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [TestMethod]
    public async Task ThePathsOutsideTheApi_AreNotFound()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var response = await alpaca.Http.GetAsync("/favicon.ico");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task TheManagementApi_DescribesTheServerAndTheRoof()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(),
            new Dictionary<string, string?> { ["Alpaca:Location"] = "Test observatory" }, waitForStatus: false);

        var versions = await Management(alpaca, "/management/apiversions?ClientTransactionID=5");
        var description = await Management(alpaca, "/management/v1/description");
        var devices = await Management(alpaca, "/management/v1/configureddevices");

        versions.GetProperty("Value").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(1);
        versions.GetProperty("ClientTransactionID").GetUInt32().Should().Be(5);
        versions.GetProperty("ServerTransactionID").GetUInt32().Should().BeGreaterThan(0);
        description.GetProperty("Value").GetProperty("ServerName").GetString().Should().NotBeNullOrWhiteSpace();
        description.GetProperty("Value").GetProperty("Manufacturer").GetString().Should().Be("Hualapai Valley Observatory");
        description.GetProperty("Value").GetProperty("ManufacturerVersion").GetString().Should().NotContain("+", "the version without its commit");
        description.GetProperty("Value").GetProperty("Location").GetString().Should().Be("Test observatory");
        var device = devices.GetProperty("Value").EnumerateArray().Should().ContainSingle().Subject;
        device.GetProperty("DeviceName").GetString().Should().Be(AlpacaDome.DeviceName);
        device.GetProperty("DeviceType").GetString().Should().Be("Dome");
        device.GetProperty("DeviceNumber").GetInt32().Should().Be(0);
        device.GetProperty("UniqueID").GetString().Should().Be(alpaca.Services.GetRequiredService<AlpacaDeviceIds>().Dome);
        Guid.TryParse(device.GetProperty("UniqueID").GetString(), out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task TheDomesUniqueId_SurvivesARestart()
    {
        var data = Directory.CreateTempSubdirectory("hvo-alpaca-ids-").FullName;
        try
        {
            var settings = new Dictionary<string, string?> { ["Alpaca:DataPath"] = data };
            string first;
            await using (var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), settings, waitForStatus: false))
            {
                first = (await Management(alpaca, "/management/v1/configureddevices")).GetProperty("Value")[0].GetProperty("UniqueID").GetString()!;
            }

            await using (var restarted = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), settings, waitForStatus: false))
            {
                (await Management(restarted, "/management/v1/configureddevices")).GetProperty("Value")[0].GetProperty("UniqueID").GetString()
                    .Should().Be(first);
            }
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("/setup")]
    [DataRow("/setup/v1/dome/0/setup")]
    public async Task TheSetupPages_OnlyRead(string path)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());

        using var response = await alpaca.Http.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("form-action 'none'");
        html.Should().Contain(alpaca.Services.GetRequiredService<AlpacaDeviceIds>().Dome);
        html.Should().Contain("Disabled").And.Contain("Closed");
        html.Should().NotContain("<form").And.NotContain("<script");
    }

    [TestMethod]
    public async Task ASetupPageForAnotherDevice_IsAMalformedRequest()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), waitForStatus: false);

        using var response = await alpaca.Http.GetAsync("/setup/v1/telescope/0/setup");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<JsonElement> Management(AlpacaTestHost alpaca, string path)
    {
        using var response = await alpaca.Http.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        body.EnumerateObject().Select(property => property.Name).Should().Equal("Value", "ClientTransactionID", "ServerTransactionID");
        return body;
    }
}
