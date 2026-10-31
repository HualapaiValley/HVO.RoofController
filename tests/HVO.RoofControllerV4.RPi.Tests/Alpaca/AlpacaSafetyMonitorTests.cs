using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common.Alpaca;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The weather as Alpaca SafetyMonitor 0 (#220, phase 2), beside the roof's Dome 0 in the same server: IsSafe is the
/// controller's weather alone (<see cref="RoofSafetyStatus.WeatherState"/>), never the telescopes, so an observing program
/// that parks and closes on an unsafe monitor does not do so when a mount unparks; anything short of Safe, the controller
/// unknown included, reads unsafe; its connection is its own; and reading it never moves the roof.
/// </summary>
[TestClass]
public sealed class AlpacaSafetyMonitorTests
{
    private const string Monitor = "safetymonitor/0";

    [TestMethod]
    public async Task TheCommonMembers_AnswerBeforeConnecting()
    {
        await using var alpaca = await StartAsync(Controller());

        (await alpaca.GetDeviceAsync(Monitor, "connected")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await alpaca.GetDeviceAsync(Monitor, "connecting")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await alpaca.GetDeviceAsync(Monitor, "interfaceversion")).GetProperty("Value").GetInt32().Should().Be(3, "ISafetyMonitorV3");
        (await alpaca.GetDeviceAsync(Monitor, "name")).GetProperty("Value").GetString().Should().Be(AlpacaSafetyMonitor.DeviceName);
        var description = (await alpaca.GetDeviceAsync(Monitor, "description")).GetProperty("Value").GetString();
        description.Should().NotBeNullOrWhiteSpace();
        description!.Length.Should().BeLessThanOrEqualTo(64, "ASCOM limits Description to 64 characters");
        (await alpaca.GetDeviceAsync(Monitor, "driverversion")).GetProperty("Value").GetString().Should().MatchRegex(@"^\d+\.\d+$");
        (await alpaca.GetDeviceAsync(Monitor, "driverinfo")).GetProperty("Value").GetString()
            .Should().StartWith(AlpacaServerIdentity.ServerName + " ", "the controller's sources recognise the server's own devices by it");
        (await alpaca.GetDeviceAsync(Monitor, "supportedactions")).GetProperty("Value").EnumerateArray().Select(a => a.GetString())
            .Should().Equal(AlpacaSafetyMonitor.StatusAction);
        var state = (await alpaca.GetDeviceAsync(Monitor, "devicestate")).GetProperty("Value").EnumerateArray()
            .ToDictionary(item => item.GetProperty("Name").GetString()!, item => item.GetProperty("Value"));
        state.Keys.Should().BeEquivalentTo("IsSafe", "TimeStamp");
        state["IsSafe"].GetBoolean().Should().BeTrue();
        DateTime.TryParse(state["TimeStamp"].GetString(), out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task IsSafe_NeedsTheClientConnected_AndReadsUnsafeUntilThen()
    {
        await using var alpaca = await StartAsync(Controller());

        var answer = await alpaca.GetDeviceAsync(Monitor, "issafe");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);
        answer.GetProperty("Value").GetBoolean().Should().BeFalse("a client that overlooks ErrorNumber must read unsafe");
    }

    [TestMethod]
    public async Task IsSafe_IsTheWeatherAlone_NeverTheTelescopes()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        await using var alpaca = await StartAsync(Controller(mount: mount));
        await ConnectAsync(alpaca);

        var isSafe = await WaitForIsSafeAsync(alpaca, true);
        using var api = alpaca.Controller!.CreateApiClient(TestApiKeys.Viewer);
        var conditions = await ApiJson.ReadAsync<RoofSafetyStatus>(await api.GetAsync("/api/v4.0/Safety"));

        isSafe.Should().BeTrue("the weather is safe; an unparked mount is not weather (#220)");
        conditions.State.Should().Be(RoofSafetyState.Unsafe, "the conditions, with the mount, are not Safe");
    }

    [TestMethod]
    [DataRow("unsafe weather")]
    [DataRow("no required weather source")]
    [DataRow("no source at all")]
    public async Task IsSafe_IsFalse_WheneverTheWeatherIsNotSafe(string why)
    {
        var host = why switch
        {
            "unsafe weather" => Controller(weather: new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false)),
            "no required weather source" => Controller(weather: new SimulatedWeatherSafetySource("Cloud sensor") { Requirement = RoofWeatherSourceRequirement.Ignored }),
            _ => Controller(defaultWeather: false)
        };
        await using var alpaca = await StartAsync(host);
        await ConnectAsync(alpaca);

        // Give the controller's sources a poll to have been read.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var answer = await alpaca.GetDeviceAsync(Monitor, "issafe", "ClientID=1");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        answer.GetProperty("Value").GetBoolean().Should().BeFalse(why);
    }

    [TestMethod]
    public async Task IsSafe_StaysFalse_UntilTheControllerListsThisMonitorsUniqueId_SoItCanNeverBeReadBack()
    {
        // Safe weather, but a controller that does not list this monitor among its own Alpaca devices could read it back
        // as a weather source (a loop): the monitor says unsafe until it does, and says why.
        await using var alpaca = await StartAsync(Controller(ownIds: false));
        await ConnectAsync(alpaca);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var isSafe = await alpaca.GetDeviceAsync(Monitor, "issafe", "ClientID=1");
        var status = await alpaca.PutDeviceAsync(Monitor, "action", "Action=status&Parameters=&ClientID=1");
        using var page = await alpaca.Http.GetAsync("/setup");
        var html = await page.Content.ReadAsStringAsync();

        isSafe.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        isSafe.GetProperty("Value").GetBoolean().Should().BeFalse("the controller does not list this monitor's UniqueID");
        status.GetProperty("Value").GetString().Should().Be(
            $"Weather: Safe. IsSafe is false: the roof controller does not list this monitor's UniqueID {SafetyMonitorId} in RoofSafety:OwnAlpacaUniqueIds, so it could read this monitor back as its own weather.");
        html.Should().Contain($"RoofSafety__OwnAlpacaUniqueIds__0={DomeId}").And.Contain($"RoofSafety__OwnAlpacaUniqueIds__1={SafetyMonitorId}");
    }

    [TestMethod]
    public async Task IsSafe_FollowsTheWeather_BothWays()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        await using var alpaca = await StartAsync(Controller(weather: weather));
        await ConnectAsync(alpaca);
        (await WaitForIsSafeAsync(alpaca, true)).Should().BeTrue();

        weather.IsSafe = false;
        (await WaitForIsSafeAsync(alpaca, false)).Should().BeFalse();
        weather.IsSafe = true;
        (await WaitForIsSafeAsync(alpaca, true)).Should().BeTrue();
    }

    [TestMethod]
    public async Task AControllerThatCannotBeReached_IsAnError_ThatStillReadsUnsafe()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(controller: null, waitForStatus: false);
        (await alpaca.PutDeviceAsync(Monitor, "connected", "Connected=true&ClientID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);

        var answer = await alpaca.GetDeviceAsync(Monitor, "issafe", "ClientID=1");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ControllerUnavailable);
        answer.GetProperty("Value").GetBoolean().Should().BeFalse("the weather is not known, so it is not safe");
    }

    [TestMethod]
    public async Task TheSafetyMonitorsConnection_IsItsOwn()
    {
        await using var alpaca = await StartAsync(Controller());

        await alpaca.ConnectAsync();
        var afterDome = await alpaca.GetDeviceAsync(Monitor, "connected", "ClientID=1");
        await ConnectAsync(alpaca);
        await alpaca.PutAsync("connected", "Connected=false&ClientID=1");
        var afterDomeDisconnected = await alpaca.GetDeviceAsync(Monitor, "connected", "ClientID=1");

        afterDome.GetProperty("Value").GetBoolean().Should().BeFalse("connecting to the Dome is not connecting to the SafetyMonitor");
        afterDomeDisconnected.GetProperty("Value").GetBoolean().Should().BeTrue("disconnecting the Dome leaves the SafetyMonitor connected");
    }

    [TestMethod]
    public async Task TheStatusAction_DescribesTheWeather_AndNothingElseIsAnAction()
    {
        await using var alpaca = await StartAsync(Controller(weather: new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false)));
        await ConnectAsync(alpaca);
        await WaitForIsSafeAsync(alpaca, false);

        var status = await alpaca.PutDeviceAsync(Monitor, "action", "Action=status&Parameters=&ClientID=1");
        var other = await alpaca.PutDeviceAsync(Monitor, "action", "Action=open&Parameters=&ClientID=1");
        var command = await alpaca.PutDeviceAsync(Monitor, "commandblind", "Command=x&Raw=false&ClientID=1");

        status.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        status.GetProperty("Value").GetString().Should().Be("Weather: Unsafe: Weather monitor 'Cloud sensor' reports unsafe weather.");
        other.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ActionNotImplemented);
        command.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotImplemented);
    }

    [TestMethod]
    public async Task APutsParameterNames_AreCaseSensitive()
    {
        await using var alpaca = await StartAsync(Controller());

        using var lowerCase = await alpaca.PutDeviceRawAsync(Monitor, "connected", "connected=true&ClientID=1");

        lowerCase.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a PUT's connected is not its Connected");
        (await alpaca.GetDeviceAsync(Monitor, "connected", "ClientID=1")).GetProperty("Value").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    [DataRow("abortslew")]
    [DataRow("shutterstatus")]
    [DataRow("park")]
    public async Task ADomesMember_IsNotASafetyMonitorsMember(string member)
    {
        await using var alpaca = await StartAsync(Controller());

        using var get = await alpaca.Http.GetAsync($"/api/v1/{Monitor}/{member}?ClientID=1");
        using var put = await alpaca.PutDeviceRawAsync(Monitor, member, "ClientID=1");

        get.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public void OnlyTheDomesAbortSlew_IsExemptFromThePollingLimit()
    {
        static HttpContext Put(string deviceType)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Put;
            context.Request.RouteValues["deviceType"] = deviceType;
            context.Request.RouteValues[AlpacaEndpoints.MemberRouteValue] = AlpacaDome.AbortSlewMember;
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, null, "device"));
            return context;
        }

        HVO.RoofControllerV4.Alpaca.Program.IsAbortSlew(Put(AlpacaDome.DeviceType)).Should().BeTrue();
        HVO.RoofControllerV4.Alpaca.Program.IsAbortSlew(Put(AlpacaSafetyMonitor.DeviceType)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ReadingIt_NeverMovesOrStopsTheRoof()
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await StartAsync(Controller(roof: roof, weather: new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false)));
        await ConnectAsync(alpaca);

        for (var i = 0; i < 5; i++)
        {
            await alpaca.GetDeviceAsync(Monitor, "issafe", "ClientID=1");
            await alpaca.GetDeviceAsync(Monitor, "devicestate", "ClientID=1");
        }

        await alpaca.PutDeviceAsync(Monitor, "disconnect", "ClientID=1");
        roof.Verify(service => service.Open(), Times.Never());
        roof.Verify(service => service.Close(), Times.Never());
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task TheConfiguredDevices_AreTheDomeAndTheSafetyMonitor_EachWithItsOwnUniqueId()
    {
        await using var alpaca = await StartAsync(Controller());
        var ids = alpaca.Services.GetRequiredService<AlpacaDeviceIds>();

        using var response = await alpaca.Http.GetAsync("/management/v1/configureddevices?ClientTransactionID=4");
        var devices = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("Value");

        devices.EnumerateArray().Select(device => (
            device.GetProperty("DeviceType").GetString(),
            device.GetProperty("DeviceNumber").GetInt32(),
            device.GetProperty("DeviceName").GetString(),
            device.GetProperty("UniqueID").GetString())).Should().Equal(
            ("Dome", 0, AlpacaDome.DeviceName, ids.Dome),
            ("SafetyMonitor", 0, AlpacaSafetyMonitor.DeviceName, ids.SafetyMonitor));
        Guid.TryParse(ids.SafetyMonitor, out _).Should().BeTrue();
        ids.SafetyMonitor.Should().NotBe(ids.Dome);
    }

    [TestMethod]
    public void AnIdentityFileFromPhase1_GainsTheSafetyMonitor_WithoutChangingTheDome()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-ids-").FullName;
        try
        {
            var dome = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(directory, AlpacaDeviceIds.FileName), JsonSerializer.Serialize(new Dictionary<string, string> { [AlpacaDeviceIds.DomeKey] = dome }));

            var first = AlpacaDeviceIds.Load(directory);
            var again = AlpacaDeviceIds.Load(directory);

            first.Dome.Should().Be(dome, "a client must keep recognising the roof");
            Guid.TryParse(first.SafetyMonitor, out _).Should().BeTrue();
            again.Should().BeEquivalentTo(first, "made once, then kept");
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, AlpacaDeviceIds.FileName)))
                .Should().BeEquivalentTo(new Dictionary<string, string> { [AlpacaDeviceIds.DomeKey] = dome, [AlpacaDeviceIds.SafetyMonitorKey] = first.SafetyMonitor });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AnIdentityFileWithABadSafetyMonitorId_StopsTheServer_RatherThanBeingReplaced()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-ids-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, AlpacaDeviceIds.FileName), JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [AlpacaDeviceIds.DomeKey] = Guid.NewGuid().ToString("D"),
                [AlpacaDeviceIds.SafetyMonitorKey] = "not-a-guid"
            }));

            var load = () => AlpacaDeviceIds.Load(directory);

            load.Should().Throw<AlpacaSettingsException>().WithMessage($"*{AlpacaDeviceIds.SafetyMonitorKey}*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheSetupPages_ShowTheSafetyMonitor()
    {
        await using var alpaca = await StartAsync(Controller());
        var ids = alpaca.Services.GetRequiredService<AlpacaDeviceIds>();

        using var page = await alpaca.Http.GetAsync("/setup/v1/safetymonitor/0/setup");
        using var none = await alpaca.Http.GetAsync("/setup/v1/safetymonitor/1/setup");
        var html = await page.Content.ReadAsStringAsync();

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain(AlpacaSafetyMonitor.DeviceName).And.Contain(ids.SafetyMonitor).And.Contain(ids.Dome);
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("/api/v1/safetymonitor/1/issafe")]
    [DataRow("/api/v1/telescope/0/atpark")]
    [DataRow("/api/v1/SafetyMonitor/0/issafe")]
    [DataRow("/api/v1/safetymonitor/0/IsSafe")]
    public async Task ADeviceOrMemberThisServerDoesNotHave_IsMalformed(string path)
    {
        await using var alpaca = await StartAsync(Controller());

        using var response = await alpaca.Http.GetAsync(path + "?ClientID=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Support ---------------------------------------------------------------------------------------------------

    /// <summary>The server's devices' identities in these tests, kept as a server that ran before would have kept them.</summary>
    internal const string DomeId = "5b1f0c2a-3d4e-4f60-8a7b-9c0d1e2f3a4b";

    internal const string SafetyMonitorId = "c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f";

    /// <summary>The server in front of <paramref name="controller"/>, with <see cref="DomeId"/> and <see cref="SafetyMonitorId"/>.</summary>
    internal static Task<AlpacaTestHost> StartAsync(RoofApiTestHost controller)
        => AlpacaTestHost.StartAsync(controller, deviceIds: new Dictionary<string, string>
        {
            [AlpacaDeviceIds.DomeKey] = DomeId,
            [AlpacaDeviceIds.SafetyMonitorKey] = SafetyMonitorId
        });

    /// <summary>
    /// A controller with the safety service reading the simulated sources given every second: by default a required
    /// weather source that reads safe and no mount, and the server's devices' UniqueIDs listed as its own
    /// (<c>RoofSafety:OwnAlpacaUniqueIds</c>).
    /// </summary>
    internal static RoofApiTestHost Controller(
        Mock<IRoofControllerServiceV4>? roof = null,
        SimulatedTelescopeParkSource? mount = null,
        SimulatedWeatherSafetySource? weather = null,
        bool defaultWeather = true,
        bool ownIds = true)
    {
        weather ??= defaultWeather ? new SimulatedWeatherSafetySource("Cloud sensor") : null;
        var settings = new Dictionary<string, string?>
        {
            ["RoofSafety:PollInterval"] = "00:00:01",
            ["RoofSafety:ReadTimeout"] = "00:00:00.5",
            ["RoofSafety:MaxReadingAge"] = "00:00:05"
        };
        if (ownIds)
        {
            settings["RoofSafety:OwnAlpacaUniqueIds:0"] = DomeId;
            settings["RoofSafety:OwnAlpacaUniqueIds:1"] = SafetyMonitorId;
        }

        return RoofClientApiTests.CreateHost(
            roof,
            configureServices: services =>
            {
                if (mount is not null)
                {
                    services.AddSingleton<ITelescopeParkSource>(mount);
                }

                if (weather is not null)
                {
                    services.AddSingleton<IWeatherSafetySource>(weather);
                }
            },
            settings: settings);
    }

    private static async Task ConnectAsync(AlpacaTestHost alpaca, uint clientId = 1)
        => (await alpaca.PutDeviceAsync(Monitor, "connected", $"Connected=true&ClientID={clientId}&ClientTransactionID=1"))
            .GetProperty("ErrorNumber").GetInt32().Should().Be(0);

    /// <summary>Reads IsSafe (client 1, connected) until it is <paramref name="expected"/>, for 15 s at most.</summary>
    private static async Task<bool> WaitForIsSafeAsync(AlpacaTestHost alpaca, bool expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        JsonElement answer = default;
        while (DateTime.UtcNow < deadline)
        {
            answer = await alpaca.GetDeviceAsync(Monitor, "issafe", "ClientID=1");
            if (answer.GetProperty("ErrorNumber").GetInt32() == 0 && answer.GetProperty("Value").GetBoolean() == expected)
            {
                return expected;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"IsSafe did not become {expected}: {answer}");
    }
}
