using System.Diagnostics;
using System.Globalization;
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

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// An Alpaca client's CloseShutter end to end (#220, phase 3): an Alpaca client in front of the Alpaca server, the
/// production controller host, the emulated roof, drive and HAT, and the emulator's Alpaca mount and weather monitor. The
/// roof starts part way open, set through the emulator, and every run checks it moved only when it should.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class AlpacaCloseScenarios
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

        public Task<JsonElement> CloseAsync(uint clientId = 7) => Alpaca.PutAsync("closeshutter", $"ClientID={clientId}&ClientTransactionID=41", Basic());

        public async Task ClearHoldAsync() => (await Person.DeleteAsync("api/v4.0/alpaca/hold")).StatusCode.Should().Be(HttpStatusCode.OK);

        public async Task<RoofAlpacaStatus> AlpacaStatusAsync()
            => (await ApiJson.ReadAsync<RoofStatusResponse>(await Person.GetAsync("api/v4.0/RoofControl/Status"))).Alpaca!;
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
            ["RoofController:Alpaca:AllowCommands"] = "CloseOnly",
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
        await WaitForAsync(async () => (await setup.Person.GetAsync("api/v4.0/Safety")).IsSuccessStatusCode
            && (await ApiJson.ReadAsync<RoofSafetyStatus>(await setup.Person.GetAsync("api/v4.0/Safety"))).State == RoofSafetyState.Safe, "the safety sources' first reads");
        return setup;
    }

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
        var position = setup.Rig.Plant.PositionMeters;
        await Task.Delay(TimeSpan.FromSeconds(1));
        setup.Rig.Plant.PositionMeters.Should().BeApproximately(position, 0.001, because);
        setup.Rig.Plant.RelayRegister.Should().Be(0, because);
    }

    [TestMethod]
    public async Task ASafeClose_ClosesTheRoof_ReadsClosingAtOnce_AndIsRecordedAsAlpaca()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();

        var answer = await setup.CloseAsync();

        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        (await setup.Alpaca.GetAsync("shutterstatus", "ClientID=7")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Closing, "the status reads Closing straight after the call");
        (await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, "the roof to close"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        using var admin = setup.Rig.CreateApiClient(TestApiKeys.Admin);
        var history = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync("api/v4.0/RoofControl/History?take=20"));
        var entry = history.Entries.First(e => e.Command == RoofHistoryCommand.Close);
        entry.Source.Should().Be("Alpaca");
        entry.AlpacaClientId.Should().Be(7u);
        entry.AuthMethod.Should().Be("basic");
        setup.Rig.Logs.Entries.Should().NotContain(e => e.Message.Contains(ExternalKey, StringComparison.Ordinal));
        setup.Rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUnparkedTelescope_RefusesTheClose_NothingMovesOrIsQueued_AndTheUisAreAlerted()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetTelescope(0, parked: false);

        var answer = await setup.CloseAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Telescope 'Main'");
        (await setup.AlpacaStatusAsync()).Alerts.Should().Contain(alert => alert.Kind == "Refused" && alert.ReasonKeys.Contains("scope-not-clear:Main"));
        setup.Devices.Alpaca.SetTelescope(0, parked: true); // conditions turn safe: nothing was queued
        await ShouldNotMoveAsync(setup, "a refused Close is never queued");
    }

    [TestMethod]
    public async Task ACloseGrant_LetsTheRefusedCloseGo_AndIsUsedUp()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetTelescope(0, parked: false);
        Error(await setup.CloseAsync()).Should().Be(AlpacaErrors.InvalidOperation);
        var alert = (await setup.AlpacaStatusAsync()).Alerts.First(a => a.Kind == "Refused");

        (await setup.Person.PostAsJsonAsync("api/v4.0/alpaca/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, alert.ReasonKeys)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var answer = await setup.CloseAsync();

        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        (await setup.AlpacaStatusAsync()).Grants.Should().BeEmpty("used up");
        await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, "the roof to close");
    }

    [TestMethod]
    public async Task AGrantFor_OneReason_DoesNotCoverANewOne_TheCloseIsRefused_AndTheGrantIsUsed()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetTelescope(0, parked: false);
        (await setup.Person.PostAsJsonAsync("api/v4.0/alpaca/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"])))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        setup.Devices.Alpaca.SetTelescope(0, connected: false); // a new reason: the mount's state is unknown

        var answer = await setup.CloseAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        (await setup.AlpacaStatusAsync()).Grants.Should().BeEmpty("used up whatever happened next");
        await ShouldNotMoveAsync(setup, "the new reason was not lifted");
    }

    [TestMethod]
    public async Task UnsafeWeather_NeverGatesClose()
    {
        await using var setup = await StartAsync();
        await setup.ClearHoldAsync();
        setup.Devices.Alpaca.SetSafetyMonitor(0, safe: false);

        Error(await setup.CloseAsync()).Should().Be(0);
        await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, "the roof to close");
    }

    [TestMethod]
    public async Task TheStartUpHold_LeftSet_RefusesTheClose()
    {
        await using var setup = await StartAsync();

        var answer = await setup.CloseAsync();

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Stop hold");
        await ShouldNotMoveAsync(setup, "the hold refuses every Alpaca command");
    }

    [TestMethod]
    public async Task AnAbortSlewThenACloseWithinTheDirectionChangeDelay_ClosesAfterTheDelay()
    {
        await using var setup = await StartAsync(new Dictionary<string, string?> { ["RoofControllerOptionsV4:DirectionChangeDelay"] = "00:00:01.5" });
        await setup.ClearHoldAsync();
        (await setup.Person.PostAsync("api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await setup.Rig.WaitForControllerAsync(status => status.IsMoving, "the person's Open to start");
        (await setup.Person.DeleteAsync("api/v4.0/alpaca/lease")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Error(await setup.Alpaca.PutAsync("abortslew", "ClientID=8")).Should().Be(0);

        // During the wait a Close with a wrong key is refused at once and changes nothing (§19 M2, §20 N2).
        var answered = Stopwatch.StartNew();
        var close = setup.CloseAsync();
        await setup.Rig.WaitForControllerAsync(status => status.IsStartPending, "the Close to wait out the delay");
        Error(await setup.Alpaca.PutAsync("closeshutter", "ClientID=8&ClientTransactionID=1", setup.Basic("not-the-external-key-at-all-000000"))).Should().Be(0x40B);

        // And the External key from an untrusted address is refused at authentication, changing nothing (§19 M2).
        using var untrusted = setup.Rig.CreateApiClient(ExternalKey);
        untrusted.DefaultRequestHeaders.Add(RemoteAddressFilter.TestPeerHeader, "192.0.2.77");
        (await untrusted.PostAsync("api/v4.0/alpaca/shutter/ticket?kind=Close", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Error(await close).Should().Be(0);
        answered.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600) + TimeSpan.FromSeconds(1.5) + TimeSpan.FromMilliseconds(400),
            "the answer comes within the budget, the delay and 400 ms");

        var off = setup.Rig.ContactChanges(1).Last(change => !change.Closed).At;
        await setup.Rig.WaitForControllerAsync(status => status.CommandedMotion == RoofMotionDirection.Closing, "the Close to start");
        var on = setup.Rig.ContactChanges(2).First(change => change.Closed).At;
        (on - off).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.4), "the close relay waits out the direction-change delay");

        // And the roof closes, all the way.
        await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, TimeSpan.FromSeconds(30), "the roof to close");
        DomeShutter.StateOf(await setup.Rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed, "Closed")).Should().Be(DomeShutterState.Closed);
    }
}
