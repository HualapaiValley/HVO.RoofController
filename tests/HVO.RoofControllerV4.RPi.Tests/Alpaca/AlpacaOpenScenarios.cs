using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// An Alpaca client's OpenShutter end to end (#220, phase 4): an Alpaca client in front of the Alpaca server, the
/// production controller host with <c>AllowCommands</c> <c>OpenAndClose</c>, the emulated roof, drive and HAT, and the
/// emulator's Alpaca mount and weather monitor. The roof starts part way open, set through the emulator, and every run
/// checks it moved only when it should: a safe Open opens it and reads Opening at once; unsafe or failing weather and an
/// unparked mount refuse it with nothing moving or queued; an Open grant lets one through; Block refuses it whatever was
/// granted; the weather turning mid-Open lets it finish with an alert; and an Alpaca Open then an Alpaca Close reverse it.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class AlpacaOpenScenarios
{
    private const string ExternalKey = "test-alpaca-external-key-not-a-real-secret";
    private const string ServerKey = "test-alpaca-server-key-not-a-real-secret-01";

    private sealed record Setup(EmulatedAlpacaHost Devices, EmulatedRoofRig Rig, AlpacaTestHost Alpaca, HttpClient Person) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Person.Dispose();
            await Alpaca.DisposeAsync();
            await Rig.DisposeAsync();
            await Devices.DisposeAsync();
        }

        public Dictionary<string, string> Basic(string key = ExternalKey)
            => new() { ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{key}")) };

        public Task<JsonElement> OpenAsync(uint clientId = 7) => Alpaca.PutAsync("openshutter", $"ClientID={clientId}&ClientTransactionID=51", Basic());

        public Task<JsonElement> CloseAsync(uint clientId = 7) => Alpaca.PutAsync("closeshutter", $"ClientID={clientId}&ClientTransactionID=52", Basic());

        public async Task ClearHoldAsync() => (await Person.DeleteAsync("api/v4.0/alpaca/hold")).StatusCode.Should().Be(HttpStatusCode.OK);

        public async Task<RoofAlpacaStatus> AlpacaStatusAsync()
            => (await ApiJson.ReadAsync<RoofStatusResponse>(await Person.GetAsync("api/v4.0/RoofControl/Status"))).Alpaca!;

        /// <summary>Changes a controller setting at runtime, as a reload of its configuration does.</summary>
        public void Reconfigure(string key, string value)
        {
            var configuration = (IConfigurationRoot)Rig.App.Services.GetRequiredService<IConfiguration>();
            configuration[key] = value;
            configuration.Reload();
        }
    }

    private static async Task<Setup> StartAsync(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var devices = await EmulatedAlpacaHost.StartAsync();
        var url = devices.BaseAddress.ToString();
        var settings = new Dictionary<string, string?>
        {
            [RoofApiTestHost.SafetyInterlockModeKey] = null,
            ["RoofSafety:PollInterval"] = "00:00:01",
            ["RoofSafety:ReadTimeout"] = "00:00:00.5",
            ["RoofSafety:MaxReadingAge"] = "00:00:05",
            ["RoofSafety:Telescopes:0:Name"] = "Main",
            ["RoofSafety:Telescopes:0:Url"] = url,
            ["RoofSafety:WeatherSources:0:Name"] = "Cloud sensor",
            ["RoofSafety:WeatherSources:0:Url"] = url,
            ["RoofController:Alpaca:AllowCommands"] = "OpenAndClose",
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "alpaca-external",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofAlpacaContract.CommanderRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = ExternalKey,
            ["RoofControllerSecurity:ApiKeys:5:Name"] = "alpaca-server",
            ["RoofControllerSecurity:ApiKeys:5:Role"] = RoofAlpacaContract.ServerRole,
            ["RoofControllerSecurity:ApiKeys:5:Key"] = ServerKey,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(plant => plant with { InitialPosition = 0.15 }, settings) with
        {
            InitialStatus = null,
            RemoteAddress = IPAddress.Loopback
        });
        var alpaca = await AlpacaTestHost.StartAsync(() => rig.App.Server, apiKey: ServerKey);
        await alpaca.ConnectAsync(7);
        await alpaca.ConnectAsync(8);

        using var admin = rig.CreateApiClient(TestApiKeys.Admin);
        (await admin.PostAsJsonAsync("api/v4.0/Identity/Users", new RoofUserCreateRequest { Name = "ada", Role = RoofControllerApiContract.OperatorRole, Password = TestSecrets.Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        using var anonymous = rig.CreateApiClient();
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(await anonymous.PostAsJsonAsync("api/v4.0/Auth/Session", new RoofSignInRequest { Name = "ada", Password = TestSecrets.Password }));
        var person = rig.CreateApiClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, session.Token);

        var setup = new Setup(devices, rig, alpaca, person);
        await WaitForSafetyAsync(setup, RoofSafetyState.Safe, "the safety sources' first reads");
        return setup;
    }

    private static Task WaitForSafetyAsync(Setup setup, RoofSafetyState state, string what)
        => WaitForAsync(async () => (await setup.Person.GetAsync("api/v4.0/Safety")).IsSuccessStatusCode
            && (await ApiJson.ReadAsync<RoofSafetyStatus>(await setup.Person.GetAsync("api/v4.0/Safety"))).State == state, what);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }

    private static int Error(JsonElement answer) => answer.GetProperty("ErrorNumber").GetInt32();

    private static async Task ShouldNotMoveAsync(Setup setup, string because)
    {
        // A roof that just stopped (at a limit, or after an AbortSlew) coasts to rest after its relays go off: what must
        // not happen is motion after that, so the check starts once the plant is at rest.
        await setup.Rig.WaitForPlantAsync(plant => Math.Abs(plant.VelocityMetersPerSecond) < 1e-6, "the roof to come to rest");
        var position = setup.Rig.Plant.PositionMeters;
        await Task.Delay(TimeSpan.FromSeconds(1));
        setup.Rig.Plant.PositionMeters.Should().BeApproximately(position, 0.001, because);
        setup.Rig.Plant.RelayRegister.Should().Be(0, because);
    }

    [TestMethod]
    public async Task ASafeOpen_OpensTheRoof_ReadsOpeningAtOnce_AndIsRecordedAsAlpaca()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        var answered = Stopwatch.StartNew();

        var answer = await setup.OpenAsync();

        answered.Stop();
        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        answered.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600 + 400 + 1000), "the answer comes within the budget and 400 ms (plus the test host's overhead)");
        (await setup.Alpaca.GetAsync("shutterstatus", "ClientID=7")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Opening, "the status reads Opening straight after the call");
        (await setup.Alpaca.GetAsync("slewing", "ClientID=7")).GetProperty("Value").GetBoolean().Should().BeTrue();
        (await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Open && !status.IsMoving, TimeSpan.FromSeconds(60), "the roof to open"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        await WaitForAsync(async () => (await setup.Alpaca.GetAsync("shutterstatus", "ClientID=7")).GetProperty("Value").GetInt32() == (int)DomeShutterState.Open,
            "the Dome to read Open from the hub");
        using var admin = setup.Rig.CreateApiClient(TestApiKeys.Admin);
        var history = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync("api/v4.0/RoofControl/History?take=20"));
        var entry = history.Entries.First(e => e.Command == RoofHistoryCommand.Open);
        entry.Source.Should().Be("Alpaca");
        entry.Accepted.Should().BeTrue();
        entry.AlpacaClientId.Should().Be(7u);
        entry.AuthMethod.Should().Be("basic");
        setup.Rig.Logs.Entries.Should().NotContain(e => e.Message.Contains(ExternalKey, StringComparison.Ordinal));
        setup.Rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task UnsafeWeather_RefusesTheOpen_NothingMovesOrIsQueued_AndTheUisAreAlerted()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: false);
        await WaitForSafetyAsync(setup, RoofSafetyState.Unsafe, "the unsafe weather");
        var before = (await setup.Alpaca.GetAsync("shutterstatus", "ClientID=7")).GetProperty("Value").GetInt32();

        var answer = await setup.OpenAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Weather monitor 'Cloud sensor'");
        (await setup.Alpaca.GetAsync("shutterstatus", "ClientID=7")).GetProperty("Value").GetInt32().Should().Be(before, "a refused start leaves the status as it was");
        (await setup.Alpaca.GetAsync("slewing", "ClientID=7")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await setup.AlpacaStatusAsync()).Alerts.Should().Contain(alert => alert.Kind == "Refused" && alert.Command == RoofAlpacaCommandKind.Open
            && alert.ReasonKeys.Contains("weather-unsafe:Cloud sensor"));
        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: true); // conditions turn safe: nothing was queued
        await WaitForSafetyAsync(setup, RoofSafetyState.Safe, "the safe weather");
        await ShouldNotMoveAsync(setup, "a refused Open is never queued or replayed");
    }

    [TestMethod]
    public async Task AWeatherMonitorThatStopsAnswering_RefusesTheOpen_BeforeItsReadingIsStale()
    {
        await using var setup = await StartAsync(new Dictionary<string, string?> { ["RoofSafety:MaxReadingAge"] = "00:05:00" });
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetSafetyMonitor(0, connected: false);
        await Task.Delay(TimeSpan.FromSeconds(2.5)); // two polls (1 s apart) fail; the safe reading is still within 5 min
        (await ApiJson.ReadAsync<RoofSafetyStatus>(await setup.Person.GetAsync("api/v4.0/Safety"))).WeatherState
            .Should().Be(RoofSafetyState.Safe, "the ordinary conditions keep the last good reading until MaxReadingAge");

        var answer = await setup.OpenAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("latest read failed");
        await ShouldNotMoveAsync(setup, "a weather source whose latest read failed is not fresh");
    }

    [TestMethod]
    public async Task AnUnparkedTelescope_RefusesTheOpen_AndNothingMoves()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetTelescope(0, parked: false);

        var answer = await setup.OpenAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Telescope 'Main'");
        await ShouldNotMoveAsync(setup, "an unparked mount is not clear of the roof");
    }

    [TestMethod]
    public async Task AnOpenGrant_LetsTheRefusedOpenGo_AndIsUsedUp()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: false);
        await WaitForSafetyAsync(setup, RoofSafetyState.Unsafe, "the unsafe weather");
        Error(await setup.OpenAsync()).Should().Be(AlpacaErrors.InvalidOperation);
        var alert = (await setup.AlpacaStatusAsync()).Alerts.First(a => a.Kind == "Refused" && a.Command == RoofAlpacaCommandKind.Open);

        (await setup.Person.PostAsJsonAsync("api/v4.0/alpaca/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, alert.ReasonKeys)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var answer = await setup.OpenAsync();

        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        (await setup.AlpacaStatusAsync()).Grants.Should().BeEmpty("used up");
        await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Open && !status.IsMoving, TimeSpan.FromSeconds(60), "the roof to open");
    }

    [TestMethod]
    public async Task AGrantMadeInWarn_IsDroppedWhenTheModeBecomesBlock_AndTheOpenIsRefused()
    {
        await using var setup = await StartAsync(new Dictionary<string, string?> { [RoofApiTestHost.SafetyInterlockModeKey] = "Warn" });
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: false);
        await WaitForSafetyAsync(setup, RoofSafetyState.Unsafe, "the unsafe weather");
        (await setup.Person.PostAsJsonAsync("api/v4.0/alpaca/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Cloud sensor"])))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        setup.Reconfigure(RoofApiTestHost.SafetyInterlockModeKey, "Block");

        var status = await setup.AlpacaStatusAsync();
        status.Grants.Should().BeEmpty("the mode change dropped the grant");
        status.OpenGrantUnavailable.Should().Contain("Block");
        (await setup.Person.PostAsJsonAsync("api/v4.0/alpaca/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Cloud sensor"])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "no Open grant is made in Block");
        var answer = await setup.OpenAsync();
        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Block");
        await ShouldNotMoveAsync(setup, "no grant opens the roof in Block");
    }

    [TestMethod]
    public async Task WeatherTurningUnsafeMidOpen_TheRoofKeepsOpening_AndTheUisAreAlerted()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        Error(await setup.OpenAsync()).Should().Be(0);
        await setup.Rig.WaitForControllerAsync(status => status.IsMoving && status.CommandedMotion == RoofMotionDirection.Opening, "the Open to start");

        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: false);

        await WaitForAsync(async () => (await setup.AlpacaStatusAsync()).Alerts.Any(alert => alert.Kind == "MidMotion" && alert.Command == RoofAlpacaCommandKind.Open),
            "the mid-Open alert");
        var status = await setup.Rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, TimeSpan.FromSeconds(60), "the roof to finish opening");
        status.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached, "a soft condition never stops the roof, and nothing moves on its own");
        await ShouldNotMoveAsync(setup, "nothing closes the roof on its own");
    }

    [TestMethod]
    public async Task AnAlpacaOpenThenAnAlpacaClose_ReversesTheRoof_WithNoOverlap()
    {
        // The emulator reversal run (#220 §7, §8): the Alpaca Close reverses the Alpaca account's own Open.
        await using var setup = await StartAsync(new Dictionary<string, string?> { ["RoofControllerOptionsV4:DirectionChangeDelay"] = "00:00:01" });
        await setup.ClearHoldAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Error(await setup.OpenAsync()).Should().Be(0);
        await setup.Rig.WaitForControllerAsync(status => status.IsMoving && status.CommandedMotion == RoofMotionDirection.Opening, "the Open to start");
        await Task.Delay(TimeSpan.FromSeconds(1));

        var answered = Stopwatch.StartNew();
        var answer = await setup.CloseAsync();

        answered.Stop();
        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        answered.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600) + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(400) + TimeSpan.FromMilliseconds(500),
            "a reversal answers within the budget, the delay and 400 ms (plus the test host's overhead)");
        var off = setup.Rig.ContactChanges(1).Last(change => !change.Closed).At;
        var on = setup.Rig.ContactChanges(2).First(change => change.Closed).At;
        (on - off).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.95), "the close relay waits out the direction-change delay");
        await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, TimeSpan.FromSeconds(60), "the roof to close");
        setup.Rig.Session.Plant.Violations.Should().BeEmpty("never both directions at once");
    }

    [TestMethod]
    public async Task AnAbortSlewDuringAnAlpacaOpen_StopsIt_AndSetsNoHold()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        Error(await setup.OpenAsync()).Should().Be(0);
        await setup.Rig.WaitForControllerAsync(status => status.IsMoving, "the Open to start");

        Error(await setup.Alpaca.PutAsync("abortslew", "ClientID=8")).Should().Be(0);

        await setup.Rig.WaitForControllerAsync(status => !status.IsMoving && status.RelayRegisterMask == 0, "the stop");
        (await setup.AlpacaStatusAsync()).Hold.Held.Should().BeFalse("Alpaca's own AbortSlew holds nothing off");
        await ShouldNotMoveAsync(setup, "stopped");
    }

    [TestMethod]
    public async Task CloseOnly_RefusesAlpacaOpen_AndNothingMoves()
    {
        await using var setup = await StartAsync(new Dictionary<string, string?> { ["RoofController:Alpaca:AllowCommands"] = "CloseOnly" });
        await setup.ClearHoldAsync();

        var answer = await setup.OpenAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("CloseOnly");
        await ShouldNotMoveAsync(setup, "CloseOnly never opens");
    }
}
