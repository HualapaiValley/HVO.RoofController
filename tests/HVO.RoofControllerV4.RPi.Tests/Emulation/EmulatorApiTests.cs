using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The emulator host (<c>HVO.RoofControllerV4.Emulator</c>): its control API changes the plant and the link, and its
/// register port serves the session's HAT to the controller's socket client.
/// </summary>
[TestClass]
public sealed class EmulatorApiTests
{
    private const string Api = "/api/emulator";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [TestMethod]
    public async Task Status_ReportsThePlantAtTheDocumentedStart_AndTheRegisterPort()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var status = await GetStatusAsync(client);

        status.Plant.Generation.Should().Be(0);
        status.Plant.PositionMeters.Should().Be(new RoofPlantOptions().InitialPosition);
        status.Plant.ClosedLimitActuated.Should().BeTrue();
        status.Plant.HatPowered.Should().BeTrue();
        status.Plant.DriveTrip.Should().Be(SmVectorTrip.None);
        status.Plant.TimeScale.Should().Be(1);
        status.Link.RegisterEndpoint.Should().Be(host.Register.LocalEndPoint.ToString()).And.StartWith("127.0.0.1:");
        host.Register.LocalEndPoint.Port.Should().NotBe(0, "port 0 picks a free port");
        (status.Link.Outage, status.Link.ResponseDelayMilliseconds, status.Link.OpenConnections, status.Link.AcceptedConnections, status.Link.Requests)
            .Should().Be((false, 0d, 0, 0L, 0L));
    }

    [TestMethod]
    public async Task Enums_AreWrittenAndReadAsNames()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>($"{Api}/status");
        using var named = await client.PostAsJsonAsync($"{Api}/drive/trip", new { trip = "MotorOverload" });

        var plant = json.GetProperty("plant");
        plant.GetProperty("driveTrip").GetString().Should().Be("None");
        plant.GetProperty("driveMode").GetString().Should().Be(nameof(SmVectorMode.Stopped));
        plant.GetProperty("wiring").GetString().Should().Be("None");
        named.StatusCode.Should().Be(HttpStatusCode.OK);
        (await named.Content.ReadFromJsonAsync<EmulatorStatusResponse>(Json))!.Plant.DriveTrip.Should().Be(SmVectorTrip.MotorOverload);
    }

    [TestMethod]
    public async Task TheRoot_RedirectsToTheStatus()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be($"{Api}/status");
    }

    [TestMethod]
    public async Task TheRegisterPort_ServesTheSessionsHat_ToTheControllersClient()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var hat = host.ConnectRegisterClient();

        hat.WriteByte(SmI010Board.RelayValueRegister, 0x08);
        var inputs = hat.ReadByte(SmI010Board.DigitalInputRegister);
        var status = await GetStatusAsync(client);

        status.Plant.RelayRegister.Should().Be(0x08);
        inputs.Should().Be(status.Plant.InputBits);
        (status.Plant.BusReads, status.Plant.BusWrites).Should().Be((1L, 1L));
        (status.Link.OpenConnections, status.Link.AcceptedConnections, status.Link.Requests).Should().Be((1, 1L, 2L));
        hat.ConnectionSettings.DeviceAddress.Should().Be(0x0E);
    }

    [TestMethod]
    public async Task Reset_StartsAFreshPlant_AtThePositionAndWiringGiven_WithoutDroppingTheLink()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var hat = host.ConnectRegisterClient();
        hat.WriteByte(SmI010Board.RelayValueRegister, 0x08);

        var status = await PostAsync(client, "/reset", new ResetRequest(1.0, WiringFault.SwappedLimitInputs));

        status.Plant.Generation.Should().Be(1);
        status.Plant.PositionMeters.Should().Be(1.0);
        status.Plant.Wiring.Should().Be(WiringFault.SwappedLimitInputs);
        status.Plant.RelayRegister.Should().Be(0, "the new HAT is at its power-on state");
        hat.ReadByte(SmI010Board.RelayValueRegister).Should().Be(0, "the next access reaches the new plant");
        hat.ConnectCount.Should().Be(1);

        var defaults = await PostAsync(client, "/reset", new { });

        defaults.Plant.Generation.Should().Be(2);
        defaults.Plant.PositionMeters.Should().Be(new RoofPlantOptions().InitialPosition);
        defaults.Plant.Wiring.Should().Be(WiringFault.None);

        // The body is optional too.
        using var empty = await client.PostAsync($"{Api}/reset", content: null);
        empty.StatusCode.Should().Be(HttpStatusCode.OK, await empty.Content.ReadAsStringAsync());
        (await empty.Content.ReadFromJsonAsync<EmulatorStatusResponse>(Json))!.Plant.Generation.Should().Be(3);
    }

    [TestMethod]
    public async Task Reset_OutsideTheHardStops_IsRefused_AndKeepsThePlant()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var detail = await PostRefusedAsync(client, "/reset", new ResetRequest(PositionMeters: 99));

        detail.Should().Be("The roof must start between the hard stops.");
        (await GetStatusAsync(client)).Plant.Generation.Should().Be(0);
    }

    [TestMethod]
    public async Task TimeScale_ChangesThePlantClock_AndOutOfRangeIsRefused()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostAsync(client, "/time-scale", new TimeScaleRequest(4))).Plant.TimeScale.Should().Be(4);
        host.Session.Clock.Scale.Should().Be(4);

        (await PostRefusedAsync(client, "/time-scale", new TimeScaleRequest(1000))).Should().Be("The time scale must be between 0.1 and 100.");
        (await PostRefusedAsync(client, "/time-scale", new TimeScaleRequest(0))).Should().Be("The time scale must be between 0.1 and 100.");
        host.Session.Clock.Scale.Should().Be(4);
    }

    [TestMethod]
    public async Task DriveTrip_InjectsAnExternalTripByDefault_AndRefusesNone()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostRefusedAsync(client, "/drive/trip", new DriveTripRequest(SmVectorTrip.None)))
            .Should().Be("Give the trip to inject; the drive's own Clear Fault input (RLY3) resets it.");
        (await GetStatusAsync(client)).Plant.DriveTrip.Should().Be(SmVectorTrip.None);

        var status = await PostAsync(client, "/drive/trip", new { });

        status.Plant.DriveTrip.Should().Be(SmVectorTrip.External);
    }

    [TestMethod]
    public async Task Power_OfTheDriveAndTheHat_IsSwitched_AndAnUnpoweredHatFailsTheBus()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var hat = host.ConnectRegisterClient();

        (await PostAsync(client, "/drive/power", new PowerRequest(false))).Plant.DrivePowered.Should().BeFalse();
        var unpowered = await PostAsync(client, "/hat/power", new PowerRequest(false));

        unpowered.Plant.HatPowered.Should().BeFalse();
        hat.Invoking(h => h.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>()
            .WithMessage("SM-I-010 at 0x0E does not respond (HAT unpowered).");

        (await PostAsync(client, "/hat/power", new PowerRequest(true))).Plant.HatPowered.Should().BeTrue();
        (await PostAsync(client, "/drive/power", new PowerRequest(true))).Plant.DrivePowered.Should().BeTrue();
        hat.ReadByte(SmI010Board.RelayValueRegister).Should().Be(0);
        hat.ConnectCount.Should().Be(1, "a bus failure keeps the connection");
    }

    [TestMethod]
    public async Task PlantFaults_AreInjected_AndRecorded()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostAsync(client, "/external-stop", new ExternalStopRequest(true))).Plant.ExternalStopOpen.Should().BeTrue();
        (await PostAsync(client, "/jam", new JamRequest(true))).Plant.Jammed.Should().BeTrue();
        (await PostAsync(client, "/limit-fault", new LimitFaultRequest(LimitSide.Open, LimitSwitchFault.StuckActuated))).Plant.OpenLimitFault
            .Should().Be(LimitSwitchFault.StuckActuated);
        (await PostAsync(client, "/limit-fault", new LimitFaultRequest(LimitSide.Closed, LimitSwitchFault.BrokenNcWire))).Plant.ClosedLimitFault
            .Should().Be(LimitSwitchFault.BrokenNcWire);
        await PostAsync(client, "/relay-fault", new RelayFaultRequest(2, RelayContactFault.Welded));
        (await PostAsync(client, "/wiring", new WiringRequest(WiringFault.SwappedDirectionRelays))).Plant.Wiring.Should().Be(WiringFault.SwappedDirectionRelays);

        var history = await client.GetFromJsonAsync<PlantEventResponse[]>($"{Api}/history", Json);

        history!.Where(e => e.Kind == PlantEventKind.Injected).Select(e => e.Detail).Should().ContainInOrder(
            "external STOP on",
            "jam on",
            "RLY2 fault Welded",
            "wiring None -> SwappedDirectionRelays");
        (await PostAsync(client, "/external-stop", new ExternalStopRequest(false))).Plant.ExternalStopOpen.Should().BeFalse();
        (await PostAsync(client, "/jam", new JamRequest(false))).Plant.Jammed.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public async Task ARelayFault_OnARelayTheHatDoesNotHave_IsRefused(int relay)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostRefusedAsync(client, "/relay-fault", new RelayFaultRequest(relay, RelayContactFault.Dead))).Should().Be("Relays are numbered 1-4.");
        host.Session.Plant.History.Should().NotContain(e => e.Detail.Contains("fault Dead"));
    }

    [TestMethod]
    public async Task BusFaults_FailTheNextAccesses_OverTheLink_AndKeepTheConnection()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var hat = host.ConnectRegisterClient();

        await PostAsync(client, "/bus", new BusFaultRequest(FailNextReads: 1));

        hat.Invoking(h => h.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>()
            .WithMessage($"Injected I2C read failure at register {SmI010Board.DigitalInputRegister}.");
        hat.ReadByte(SmI010Board.DigitalInputRegister);
        hat.ConnectCount.Should().Be(1);

        var failing = await PostAsync(client, "/bus", new BusFaultRequest(FailReads: true, FailWrites: true, FailInputReads: true));
        (failing.Plant.FailReads, failing.Plant.FailWrites, failing.Plant.FailInputReads).Should().Be((true, true, true));
        failing.Plant.InjectedBusFailures.Should().Be(1);

        var partial = await PostAsync(client, "/bus", new BusFaultRequest(FailWrites: false));
        (partial.Plant.FailReads, partial.Plant.FailWrites, partial.Plant.FailInputReads).Should().Be((true, false, true), "a null leaves a setting alone");
    }

    [TestMethod]
    public async Task BusFaults_WithANegativeCount_AreRefused_AndChangeNothing()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostRefusedAsync(client, "/bus", new BusFaultRequest(FailReads: true, FailNextWrites: -1))).Should().Be("A failure count cannot be negative.");

        host.Session.Client.FailReads.Should().BeFalse("the request is checked before any of it applies");
        host.Session.Client.FailNextWrites.Should().Be(0);
    }

    [TestMethod]
    public async Task TheLink_TakesAResponseDelay_AnOutage_AndADisconnect()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var hat = host.ConnectRegisterClient();
        hat.ReadByte(SmI010Board.DigitalInputRegister);

        (await PostAsync(client, "/link", new LinkRequest(ResponseDelayMilliseconds: 250))).Link.ResponseDelayMilliseconds.Should().Be(250);
        host.Register.ResponseDelay.Should().Be(TimeSpan.FromMilliseconds(250));
        (await PostAsync(client, "/link", new LinkRequest(ResponseDelayMilliseconds: 0))).Link.ResponseDelayMilliseconds.Should().Be(0);

        var outage = await PostAsync(client, "/link", new LinkRequest(Outage: true));

        outage.Link.Outage.Should().BeTrue();
        outage.Link.OpenConnections.Should().Be(0, "an outage drops the open connections");
        hat.Invoking(h => h.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>();

        (await PostAsync(client, "/link", new LinkRequest(Outage: false))).Link.Outage.Should().BeFalse();
        hat.ReadByte(SmI010Board.DigitalInputRegister);
        hat.ConnectCount.Should().BeGreaterThan(1, "the client reconnects once the outage ends");

        var connects = hat.ConnectCount;
        (await PostAsync(client, "/link", new LinkRequest(Disconnect: true))).Link.OpenConnections.Should().Be(0);
        hat.Invoking(h => h.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>()
            .WithMessage("The HAT emulator closed the connection.", "a dropped connection fails the access in progress");
        hat.ReadByte(SmI010Board.DigitalInputRegister);
        hat.ConnectCount.Should().Be(connects + 1, "the next access reconnects");
        (await GetStatusAsync(client)).Link.Outage.Should().BeFalse("a disconnect is not an outage");
    }

    [TestMethod]
    [DataRow(-1.0)]
    [DataRow(60_001.0)]
    public async Task TheLink_RefusesAResponseDelayOutOfRange(double delay)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostRefusedAsync(client, "/link", new LinkRequest(Outage: true, ResponseDelayMilliseconds: delay)))
            .Should().Be("The response delay must be between 0 and 60000 ms.");
        host.Register.Outage.Should().BeFalse("nothing in a refused request applies");
    }

    [TestMethod]
    public async Task History_ReturnsTheLatestEvents_UpToTheLimit()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        await PostAsync(client, "/jam", new JamRequest(true));
        await PostAsync(client, "/jam", new JamRequest(false));
        await PostAsync(client, "/external-stop", new ExternalStopRequest(true));

        var latest = await client.GetFromJsonAsync<PlantEventResponse[]>($"{Api}/history?limit=2", Json);
        var one = await client.GetFromJsonAsync<PlantEventResponse[]>($"{Api}/history?limit=0", Json);
        var all = await client.GetFromJsonAsync<PlantEventResponse[]>($"{Api}/history?limit=100000", Json);

        latest!.Select(e => e.Detail).Should().Equal("jam off", "external STOP on");
        latest.Should().BeInAscendingOrder(e => e.At);
        one.Should().ContainSingle().Which.Detail.Should().Be("external STOP on", "the limit is at least 1");
        all!.Length.Should().Be(host.Session.Plant.History.Count);
    }

    [TestMethod]
    public async Task Violations_AreEmpty_ForAPlantNobodyHasMisused()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var violations = await client.GetFromJsonAsync<JsonElement>($"{Api}/violations");

        violations.ValueKind.Should().Be(JsonValueKind.Array);
        violations.GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    [DataRow("/drive/trip", """{"trip":"Bogus"}""")]
    [DataRow("/reset", """{"wiring":"NoSuchFault"}""")]
    [DataRow("/time-scale", """{"scale":"fast"}""")]
    // Enums by name only: a number could name a value the plant does not model.
    [DataRow("/drive/trip", """{"trip":2}""")]
    [DataRow("/limit-fault", """{"limit":5,"fault":"StuckActuated"}""")]
    [DataRow("/limit-fault", """{"limit":"Open","fault":64}""")]
    [DataRow("/relay-fault", """{"relay":1,"fault":1}""")]
    [DataRow("/wiring", """{"wiring":1024}""")]
    [DataRow("/reset", """{"wiring":2}""")]
    // A list of names only for a [Flags] enum: for any other, it reads as another value ("Open, Closed" is Closed).
    [DataRow("/limit-fault", """{"limit":"Open, Closed","fault":"StuckActuated"}""")]
    [DataRow("/drive/trip", """{"trip":"StartTooSoonAfterPowerUp, MotorOverload"}""")]
    [DataRow("/relay-fault", """{"relay":1,"fault":"None, Welded"}""")]
    public async Task ARequestThatDoesNotParse_IsABadRequest(string path, string body)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        var before = host.Session.GetStatus();

        using var response = await client.PostAsync(Api + path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Session.GetStatus().Should().BeEquivalentTo(before, o => o.Excluding(status => status.Elapsed), "a refused request changes nothing");
    }

    [TestMethod]
    public async Task AListOfNames_SetsEachFlag_OfAFlagsEnum()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        (await PostAsync(client, "/wiring", new { wiring = "SwappedLimitInputs, SwappedMotorLeads" })).Plant.Wiring
            .Should().Be(WiringFault.SwappedLimitInputs | WiringFault.SwappedMotorLeads);
    }

    [TestMethod]
    [DataRow("/time-scale", "{}", "scale")]
    [DataRow("/drive/power", "{}", "powered")]
    [DataRow("/hat/power", """{"power":false}""", "powered")]
    [DataRow("/external-stop", "{}", "open")]
    [DataRow("/jam", "{}", "jammed")]
    [DataRow("/limit-fault", """{"fault":"StuckActuated"}""", "limit")]
    [DataRow("/limit-fault", """{"limit":"Open"}""", "fault")]
    [DataRow("/relay-fault", """{"fault":"Welded"}""", "relay")]
    [DataRow("/relay-fault", """{"relay":2}""", "fault")]
    [DataRow("/wiring", "{}", "wiring")]
    public async Task ARequestWithoutARequiredField_IsRefused_AndChangesNothing(string path, string body, string field)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        var before = host.Session.GetStatus();

        using var response = await client.PostAsync(Api + path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Invalid emulator request");
        problem.GetProperty("detail").GetString().Should().Be($"The request must give \"{field}\".");
        host.Session.GetStatus().Should().BeEquivalentTo(before, o => o.Excluding(status => status.Elapsed), "a missing field is never read as false or a default");
    }

    [TestMethod]
    public async Task TheSettings_ShapeTheSession()
    {
        await using var host = new EmulatorHost(new()
        {
            ["Emulator:TimeScale"] = "2.5",
            ["Emulator:TravelMeters"] = "1.0",
            ["Emulator:InitialPosition"] = "0.5"
        });
        using var client = host.CreateClient();

        var status = await GetStatusAsync(client);

        status.Plant.TimeScale.Should().Be(2.5);
        status.Plant.TravelMeters.Should().Be(1.0);
        status.Plant.PositionMeters.Should().Be(0.5);
        status.Plant.OpenPercent.Should().Be(50);
    }

    [TestMethod]
    [DataRow("Emulator:RegisterAddress", "localhost", typeof(InvalidOperationException), "Emulator:RegisterAddress must be an IP address*")]
    [DataRow("Emulator:RegisterPort", "70000", typeof(InvalidOperationException), "Emulator:RegisterPort must be between 0 and 65535.")]
    [DataRow("Emulator:TimeScale", "500", typeof(ArgumentOutOfRangeException), "The time scale must be between 0.1 and 100.*")]
    [DataRow("Emulator:InitialPosition", "-1", typeof(ArgumentOutOfRangeException), "The roof must start between the hard stops.*")]
    public async Task InvalidSettings_StopTheStart(string key, string value, Type exception, string message)
    {
        await using var host = new EmulatorHost(new() { [key] = value });

        var start = FluentActions.Invoking(() => host.CreateClient());

        start.Should().Throw<Exception>().Which.Should().BeOfType(exception).And.Match<Exception>(e => e.Message.StartsWith(message.TrimEnd('*'), StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheHostOptions_DefaultToLoopback_OnTheDocumentedPort()
    {
        var options = new EmulatorHostOptions();

        options.RegisterEndPoint().Should().Be(new IPEndPoint(IPAddress.Loopback, HatEmulatorOptions.DefaultPort));
        EmulatorHostOptions.DefaultControlUrl.Should().Be("http://127.0.0.1:5290");
        options.SessionOptions().TimeScale.Should().Be(1);
        options.SessionOptions().Plant.Should().BeEquivalentTo(new RoofPlantOptions());
        new EmulatorHostOptions { RegisterAddress = "0.0.0.0", RegisterPort = 0 }.RegisterEndPoint().Should().Be(new IPEndPoint(IPAddress.Any, 0));
    }

    private static async Task<EmulatorStatusResponse> GetStatusAsync(HttpClient client)
        => (await client.GetFromJsonAsync<EmulatorStatusResponse>($"{Api}/status", Json))!;

    private static async Task<EmulatorStatusResponse> PostAsync(HttpClient client, string path, object request)
    {
        using var response = await client.PostAsJsonAsync(Api + path, request, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EmulatorStatusResponse>(Json))!;
    }

    /// <summary>Posts a request the API must refuse, and returns the problem's detail.</summary>
    private static async Task<string> PostRefusedAsync(HttpClient client, string path, object request)
    {
        using var response = await client.PostAsJsonAsync(Api + path, request, Json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Invalid emulator request");
        problem.GetProperty("status").GetInt32().Should().Be(400);
        return problem.GetProperty("detail").GetString()!;
    }
}
