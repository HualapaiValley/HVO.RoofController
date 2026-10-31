using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.Simulation.Safety;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The HAT emulator's ASCOM Alpaca devices (a telescope mount and a weather safety monitor, #220), checked against the
/// ASCOM Alpaca Device and Management APIs (https://ascom-standards.org/api/) and the ASCOM interfaces, not against the
/// controller's adapter: the answer's shape and its transaction numbers, parameter names (any case in a GET, exact in a PUT), form-encoded PUTs,
/// HTTP 400 for a request a device cannot interpret, NotConnected (0x407) while not connected, a mount that cannot park,
/// the fault injection, and the control API.
/// </summary>
[TestClass]
public sealed class EmulatedAlpacaTests
{
    private const string Control = "/api/emulator/alpaca";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [TestMethod]
    public async Task AValueRead_IsTheAlpacaAnswer_WithTheClientsTransactionEchoed_AndTheServersCounting()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var first = await ReadAsync(client, "/api/v1/telescope/0/atpark?ClientID=42&ClientTransactionID=7");
        var second = await ReadAsync(client, "/api/v1/safetymonitor/0/issafe?clientid=42&clienttransactionid=8");

        first.Status.Should().Be(HttpStatusCode.OK);
        first.Body.EnumerateObject().Select(property => property.Name).Should().Equal(
            "Value", "ClientTransactionID", "ServerTransactionID", "ErrorNumber", "ErrorMessage");
        first.Body.GetProperty("Value").GetBoolean().Should().BeTrue("the mount starts parked");
        first.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(7);
        first.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        first.Body.GetProperty("ErrorMessage").GetString().Should().BeEmpty();
        second.Body.GetProperty("Value").GetBoolean().Should().BeTrue("the weather starts safe");
        second.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(8, "parameter names are not case sensitive");
        second.Body.GetProperty("ServerTransactionID").GetUInt32().Should().Be(first.Body.GetProperty("ServerTransactionID").GetUInt32() + 1);
    }

    [TestMethod]
    public async Task AReadWithoutTheClientsNumbers_EchoesZero_TheApisNotSupplied()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var answer = await ReadAsync(client, "/api/v1/telescope/0/connected");

        answer.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(0);
        answer.Body.GetProperty("ServerTransactionID").GetUInt32().Should().BeGreaterThan(0, "the server counts from 1");
        answer.Body.GetProperty("Value").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task TheCommonMembers_AnswerAsTheInterfacesSay()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var mountVersion = await ReadAsync(client, "/api/v1/telescope/0/interfaceversion");
        var monitorVersion = await ReadAsync(client, "/api/v1/safetymonitor/0/interfaceversion");
        var name = await ReadAsync(client, "/api/v1/safetymonitor/0/name");
        var driverVersion = await ReadAsync(client, "/api/v1/safetymonitor/0/driverversion");
        var actions = await ReadAsync(client, "/api/v1/safetymonitor/0/supportedactions");
        var connecting = await ReadAsync(client, "/api/v1/safetymonitor/0/connecting");
        var state = await ReadAsync(client, "/api/v1/safetymonitor/0/devicestate");
        var canPark = await ReadAsync(client, "/api/v1/telescope/0/canpark");

        mountVersion.Body.GetProperty("Value").GetInt32().Should().Be(4, "ITelescopeV4");
        monitorVersion.Body.GetProperty("Value").GetInt32().Should().Be(3, "ISafetyMonitorV3");
        name.Body.GetProperty("Value").GetString().Should().Be("Emulated safety monitor 0");
        driverVersion.Body.GetProperty("Value").GetString().Should().MatchRegex(@"^\d+\.\d+$", "DriverVersion is major.minor");
        actions.Body.GetProperty("Value").GetArrayLength().Should().Be(0);
        connecting.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        state.Body.GetProperty("Value").EnumerateArray().Select(item => item.GetProperty("Name").GetString()).Should().Equal("IsSafe", "TimeStamp");
        state.Body.GetProperty("Value")[0].GetProperty("Value").GetBoolean().Should().BeTrue();
        canPark.Body.GetProperty("Value").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    [DataRow("/api/v1/Telescope/0/atpark", "a device type in capitals")]
    [DataRow("/api/v1/telescope/0/AtPark", "a member in capitals")]
    [DataRow("/api/v1/dome/0/atpark", "a device type the emulator does not serve")]
    [DataRow("/api/v1/telescope/1/atpark", "a device number it does not serve")]
    [DataRow("/api/v1/telescope/x/atpark", "a device number that is not a number")]
    [DataRow("/api/v1/telescope/0/shutterstatus", "a member no telescope has")]
    [DataRow("/api/v1/telescope/0/park", "a member only PUT")]
    [DataRow("/api/v1/safetymonitor/0/atpark", "a telescope's member asked of a safety monitor")]
    [DataRow("/api/v1/telescope/0/atpark?ClientTransactionID=-1", "a transaction number below 0")]
    [DataRow("/api/v1/telescope/0/atpark?ClientID=abc", "a client number that is not a number")]
    public async Task ARequestTheDeviceCannotInterpret_IsHttp400_WithATextMessage(string path, string because)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task AValueRead_WhileNotConnected_IsNotConnected_0x407_WithNoValue_ButConnectedAndTheNameStillAnswer()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        (await PostAsync(client, "telescope", new { device = 0, connected = false })).Should().Be(HttpStatusCode.OK);

        var parked = await ReadAsync(client, "/api/v1/telescope/0/atpark");
        var canPark = await ReadAsync(client, "/api/v1/telescope/0/canpark");
        var connected = await ReadAsync(client, "/api/v1/telescope/0/connected");
        var name = await ReadAsync(client, "/api/v1/telescope/0/name");

        parked.Status.Should().Be(HttpStatusCode.OK, "an ASCOM error is a transaction the server handled");
        parked.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x407);
        parked.Body.GetProperty("ErrorMessage").GetString().Should().NotBeEmpty();
        parked.Body.TryGetProperty("Value", out _).Should().BeFalse("an error carries no value a client could take for a reading");
        canPark.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x407);
        connected.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        connected.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        name.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0, "the name is readable before connecting");
    }

    [TestMethod]
    public async Task AFormEncodedPut_ConnectsAndDisconnects_AndParksAndUnparks()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var disconnect = await PutAsync(client, "/api/v1/telescope/0/connected", ("Connected", "false"), ("ClientTransactionID", "11"));
        var refused = await PutAsync(client, "/api/v1/telescope/0/unpark");
        var connect = await PutAsync(client, "/api/v1/telescope/0/connect");
        var unpark = await PutAsync(client, "/api/v1/telescope/0/unpark", ("ClientTransactionID", "12"));
        var unparked = await ReadAsync(client, "/api/v1/telescope/0/atpark");
        await PutAsync(client, "/api/v1/telescope/0/park");
        var parked = await ReadAsync(client, "/api/v1/telescope/0/atpark");

        disconnect.Status.Should().Be(HttpStatusCode.OK);
        disconnect.Body.EnumerateObject().Select(property => property.Name).Should().Equal(
            "ClientTransactionID", "ServerTransactionID", "ErrorNumber", "ErrorMessage");
        disconnect.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(11);
        refused.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x407, "a mount that is not connected cannot unpark");
        connect.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        unpark.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        unparked.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        parked.Body.GetProperty("Value").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task APutsParameterNames_AreCaseSensitive_AsTheAlpacaApiSays()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var misspelt = await PutAsync(client, "/api/v1/telescope/0/connect", ("clienttransactionid", "12"));
        var lowerCase = await PutAsync(client, "/api/v1/telescope/0/connected", ("connected", "False"));

        misspelt.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(0, "a PUT's clienttransactionid is not its ClientTransactionID");
        lowerCase.Status.Should().Be(HttpStatusCode.BadRequest, "a PUT's connected is not its Connected");
        host.Alpaca.GetStatus().Telescopes[0].Connected.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("/api/v1/telescope/0/connected", "Connected", "maybe", "a Connected that is not True or False")]
    [DataRow("/api/v1/telescope/0/connected", "Other", "True", "no Connected at all")]
    [DataRow("/api/v1/telescope/0/atpark", "Other", "1", "a member only read")]
    [DataRow("/api/v1/safetymonitor/0/park", "Other", "1", "a telescope's method called on a safety monitor")]
    public async Task APutTheDeviceCannotInterpret_IsHttp400(string path, string name, string value, string because)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var response = await PutAsync(client, path, (name, value));

        response.Status.Should().Be(HttpStatusCode.BadRequest, because);
    }

    [TestMethod]
    public async Task APutWithoutAForm_IsHttp400()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        using var response = await client.PutAsync("/api/v1/telescope/0/connected", new StringContent("{\"Connected\":true}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Alpaca PUT parameters are form-encoded");
    }

    [TestMethod]
    public async Task ActionsAndCommands_AreNotImplemented()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var action = await PutAsync(client, "/api/v1/safetymonitor/0/action", ("Action", "anything"), ("Parameters", string.Empty));
        var command = await PutAsync(client, "/api/v1/safetymonitor/0/commandblind", ("Command", "x"), ("Raw", "false"));

        action.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x40C, "ActionNotImplementedException: SupportedActions is empty");
        command.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x400);
    }

    [TestMethod]
    public async Task AMountThatCannotPark_SaysSo_IsNeverParked_AndCannotBeParked()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        (await PostAsync(client, "telescope", new { device = 0, canPark = false })).Should().Be(HttpStatusCode.OK);

        var canPark = await ReadAsync(client, "/api/v1/telescope/0/canpark");
        var canUnpark = await ReadAsync(client, "/api/v1/telescope/0/canunpark");
        var atPark = await ReadAsync(client, "/api/v1/telescope/0/atpark");
        var park = await PutAsync(client, "/api/v1/telescope/0/park");

        canPark.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        canUnpark.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        atPark.Body.GetProperty("Value").GetBoolean().Should().BeFalse("ITelescopeV4: AtPark is false for a mount that cannot park");
        park.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x400);
    }

    [TestMethod]
    public async Task TheMount_IsAWholeITelescopeV4_ThatCanOnlyPark()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var sidereal = await ReadAsync(client, "/api/v1/telescope/0/siderealtime");
        var rightAscension = await ReadAsync(client, "/api/v1/telescope/0/rightascension");
        var utc = await ReadAsync(client, "/api/v1/telescope/0/utcdate");
        var canSlew = await ReadAsync(client, "/api/v1/telescope/0/canslew");
        var rates = await ReadAsync(client, "/api/v1/telescope/0/trackingrates");
        var axisRates = await ReadAsync(client, "/api/v1/telescope/0/axisrates?Axis=1");
        var badAxis = await ReadAsync(client, "/api/v1/telescope/0/canmoveaxis?axis=7");
        var altitude = await ReadAsync(client, "/api/v1/telescope/0/altitude");
        var tracking = await PutAsync(client, "/api/v1/telescope/0/tracking", ("Tracking", "true"));
        var slew = await PutAsync(client, "/api/v1/telescope/0/slewtocoordinatesasync", ("RightAscension", "1.5"), ("Declination", "10"));
        var abortParked = await PutAsync(client, "/api/v1/telescope/0/abortslew");
        var state = await ReadAsync(client, "/api/v1/telescope/0/devicestate");

        sidereal.Body.GetProperty("Value").GetDouble().Should().BeInRange(0, 24);
        rightAscension.Body.GetProperty("Value").GetDouble().Should().BeApproximately(sidereal.Body.GetProperty("Value").GetDouble(), 0.01, "it stands on the meridian");
        DateTime.Parse(utc.Body.GetProperty("Value").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        canSlew.Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        rates.Body.GetProperty("Value").EnumerateArray().Select(rate => rate.GetInt32()).Should().Equal(0);
        axisRates.Body.GetProperty("Value").GetArrayLength().Should().Be(0);
        badAxis.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x401, "InvalidValue for an axis that is not 0, 1 or 2");
        altitude.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x400, "an optional member it does not implement");
        tracking.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x400, "CanSetTracking is false");
        slew.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x400, "CanSlewAsync is false");
        abortParked.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x408, "ParkedException while parked");
        state.Body.GetProperty("Value").EnumerateArray().Select(item => item.GetProperty("Name").GetString())
            .Should().Equal("AtHome", "AtPark", "Declination", "RightAscension", "SiderealTime", "Slewing", "Tracking", "UTCDate", "TimeStamp");
    }

    [TestMethod]
    [DataRow("/api/v1/telescope/0/tracking", "Tracking", "yes", "a Tracking that is not True or False")]
    [DataRow("/api/v1/telescope/0/sitelatitude", "SiteLatitude", "north", "a SiteLatitude that is not a number")]
    [DataRow("/api/v1/telescope/0/moveaxis", "Axis", "0", "MoveAxis without its Rate")]
    [DataRow("/api/v1/telescope/0/utcdate", "UTCDate", "yesterday", "a UTCDate that is not a date")]
    public async Task AMountPutWithAMissingOrBadParameter_IsHttp400(string path, string name, string value, string because)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var response = await PutAsync(client, path, (name, value));

        response.Status.Should().Be(HttpStatusCode.BadRequest, because);
    }

    [TestMethod]
    public async Task TheControlApi_UnparksTheMount_AndMakesTheWeatherUnsafe()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostAsync(client, "telescope", new { device = 0, parked = false })).Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, "safetymonitor", new { device = 0, safe = false })).Should().Be(HttpStatusCode.OK);

        (await ReadAsync(client, "/api/v1/telescope/0/atpark")).Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        (await ReadAsync(client, "/api/v1/safetymonitor/0/issafe")).Body.GetProperty("Value").GetBoolean().Should().BeFalse();
        var status = (await client.GetFromJsonAsync<EmulatedAlpacaStatus>(Control, Json))!;
        status.Telescopes.Single().Should().BeEquivalentTo(new
        {
            DeviceType = "telescope",
            DeviceNumber = 0,
            UniqueId = "hvo-hat-emulator-telescope-0",
            Parked = (bool?)false,
            CanPark = (bool?)true,
            Safe = (bool?)null,
            Connected = true,
            Requests = 1L
        });
        status.SafetyMonitors.Single().Should().BeEquivalentTo(new { DeviceType = "safetymonitor", Safe = (bool?)false, Parked = (bool?)null, CanPark = (bool?)null });
    }

    [TestMethod]
    public async Task ADriverError_Is0x500_AndAServerError_IsHttp500()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        await PostAsync(client, "telescope", new { device = 0, behaviour = "DriverError" });
        var driver = await ReadAsync(client, "/api/v1/telescope/0/atpark");
        await PostAsync(client, "telescope", new { device = 0, behaviour = "ServerError" });
        using var server = await client.GetAsync("/api/v1/telescope/0/atpark");

        driver.Status.Should().Be(HttpStatusCode.OK);
        driver.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0x500);
        driver.Body.TryGetProperty("Value", out _).Should().BeFalse();
        server.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        server.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [TestMethod]
    public async Task ADeviceThatHangs_AnswersNothing_UntilTheClientGivesUp()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        await PostAsync(client, "safetymonitor", new { device = 0, behaviour = "Hangs" });

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var read = () => client.GetAsync("/api/v1/safetymonitor/0/issafe", giveUp.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        await PostAsync(client, "safetymonitor", new { device = 0, behaviour = "Answers" });
        (await ReadAsync(client, "/api/v1/safetymonitor/0/issafe")).Status.Should().Be(HttpStatusCode.OK, "the device answers again");
    }

    [TestMethod]
    public async Task TheEmulatorsReset_RestoresTheDevices()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        await PostAsync(client, "telescope", new { device = 0, parked = false, connected = false, canPark = false, behaviour = "ServerError" });

        using var reset = await client.PostAsync("/api/emulator/reset", null);

        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Alpaca.GetStatus().Telescopes.Single().Should().BeEquivalentTo(new
        {
            Parked = (bool?)true,
            CanPark = (bool?)true,
            Connected = true,
            Behaviour = EmulatedAlpacaBehaviour.Answers,
            Requests = 0L
        });
        (await ReadAsync(client, "/api/v1/telescope/0/atpark")).Body.GetProperty("Value").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    [DataRow("telescope", "{\"device\":1,\"parked\":false}", "a device number the emulator does not serve")]
    [DataRow("telescope", "{\"device\":0,\"behaviour\":3}", "a behaviour given as a number")]
    [DataRow("safetymonitor", "{\"device\":0,\"behaviour\":\"Sometimes\"}", "a behaviour that is not one")]
    public async Task TheControlApi_RefusesWhatItCannotDo(string device, string body, string because)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsync($"{Control}/{device}", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
    }

    [TestMethod]
    public async Task TheSettings_ChooseHowManyDevicesAreServed()
    {
        await using var host = new EmulatorHost(new() { ["Emulator:AlpacaTelescopes"] = "2", ["Emulator:AlpacaSafetyMonitors"] = "0" });
        using var client = host.CreateClient();

        (await ReadAsync(client, "/api/v1/telescope/1/atpark")).Status.Should().Be(HttpStatusCode.OK);
        using var none = await client.GetAsync("/api/v1/safetymonitor/0/issafe");
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var devices = await ReadAsync(client, "/management/v1/configureddevices");
        devices.Body.GetProperty("Value").EnumerateArray()
            .Select(device => (device.GetProperty("DeviceType").GetString(), device.GetProperty("DeviceNumber").GetInt32(), device.GetProperty("UniqueID").GetString()))
            .Should().Equal(("Telescope", 0, "hvo-hat-emulator-telescope-0"), ("Telescope", 1, "hvo-hat-emulator-telescope-1"));
    }

    [TestMethod]
    public void TooManyDevices_StopTheEmulator()
    {
        var start = () => new EmulatorHostOptions { AlpacaTelescopes = 9 }.AlpacaServer();
        var negative = () => new EmulatorHostOptions { AlpacaSafetyMonitors = -1 }.AlpacaServer();

        start.Should().Throw<InvalidOperationException>().WithMessage("*AlpacaTelescopes must be between 0 and 8*");
        negative.Should().Throw<InvalidOperationException>().WithMessage("*AlpacaSafetyMonitors must be between 0 and 8*");
    }

    [TestMethod]
    public async Task TheManagementApi_ListsTheVersionsAndTheDescription()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var versions = await ReadAsync(client, "/management/apiversions?ClientTransactionID=3");
        var description = await ReadAsync(client, "/management/v1/description");
        using var unknown = await client.GetAsync("/management/v2/description");

        versions.Body.GetProperty("Value").EnumerateArray().Select(version => version.GetInt32()).Should().Equal(1);
        versions.Body.GetProperty("ClientTransactionID").GetUInt32().Should().Be(3);
        versions.Body.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        description.Body.GetProperty("Value").GetProperty("ServerName").GetString().Should().Be("HVO HAT emulator");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, string path, params (string Name, string Value)[] form)
    {
        using var response = await client.PutAsync(path, new FormUrlEncodedContent(form.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value))));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string device, object request)
    {
        using var response = await client.PostAsJsonAsync($"{Control}/{device}", request);
        return response.StatusCode;
    }
}
