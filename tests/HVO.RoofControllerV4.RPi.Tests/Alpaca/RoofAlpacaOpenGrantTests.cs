using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// Open override grants (#220, phase 4, §5, Roy's decision 4): separate from Close grants, each used up by one command,
/// reasons-bound (the weather's reasons too), UI-only, 1 to 15 minutes; and no grant ever opens the roof while the safety
/// interlock is Block: the grant route refuses an Open grant (403), a Warn to Block change drops one, and one that is
/// somehow still there is never honoured.
/// </summary>
[TestClass]
public sealed class RoofAlpacaOpenGrantTests
{
    private const byte OpenRelay = 1;

    private static RoofOverrideGrantRequest Open(params string[] keys) => new(RoofAlpacaCommandKind.Open, keys);

    private static RoofOverrideGrantRequest Close(params string[] keys) => new(RoofAlpacaCommandKind.Close, keys);

    private static Task<AlpacaCommandRig> RigAsync(bool parked = true, bool weatherSafe = true, int scopes = 1,
        RoofSafetyInterlockMode interlockMode = RoofSafetyInterlockMode.Warn, TimeProvider? time = null, TimeSpan? directionChangeDelay = null)
        => AlpacaCommandRig.StartAsync(scopes: scopes, parked: parked, weatherSafe: weatherSafe, mode: RoofAlpacaCommandMode.OpenAndClose,
            interlockMode: interlockMode, time: time, directionChangeDelay: directionChangeDelay);

    [TestMethod]
    public async Task AnOpenGrant_LetsOneRefusedOpen_Through_ThenIsGone()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        var refused = await rig.OpenAsync();
        var alert = rig.Commands.Alerts().Single();
        alert.Command.Should().Be(RoofAlpacaCommandKind.Open);

        var grant = rig.Commands.Grant(AlpacaCommandRig.Person(), Open([.. alert.ReasonKeys])).Grant!;
        var outcome = await rig.OpenAsync();

        refused.Rule().Should().Be("NotSafe");
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Open);
        grant.GrantedByName.Should().Be("ada", "the person's own name, so each UI tells its person's grants (PR #232 rereview F7)");
        grant.GrantedBy.Should().Be("ada (signed in)");
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        outcome.GrantId.Should().Be(grant.Id, "the history links the Open to its grant");
        outcome.ReasonKeys.Should().Equal("weather-unsafe:Weather");
        rig.Commands.Grants().Should().BeEmpty("one command each");
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task AnOpenGrant_LiftsEveryKindOfOpenReason_ItWasGiven()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(scopes: 0, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        var refused = await rig.OpenAsync();
        refused.ReasonKeys.Should().BeEquivalentTo([RoofAlpacaContract.NoBlockingScopesKey, RoofAlpacaContract.NoRequiredWeatherKey]);

        rig.Commands.Grant(AlpacaCommandRig.Person(), Open([.. refused.ReasonKeys])).StatusCode.Should().Be(StatusCodes.Status201Created);

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnOpenGrant_LiftsOnlyTheReasonsShown_ANewReasonRefuses_AndTheGrantIsStillUsedUp()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Scope.Parked = false; // a new reason after the grant: the mount unparked

        var outcome = await rig.OpenAsync();

        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().BeEquivalentTo("weather-unsafe:Weather", "scope-not-clear:Main");
        rig.RelayOnWrites(OpenRelay).Should().Be(0);
        rig.Commands.Grants().Should().BeEmpty("used up whatever happens next");
        rig.Commands.Alerts().Should().Contain(alert => alert.Command == RoofAlpacaCommandKind.Open && alert.ReasonKeys.Contains("scope-not-clear:Main"));
    }

    [TestMethod]
    public async Task AnOpenGrant_StillApplies_WhenOnlyTheReasonsWordsChange()
    {
        // A weather source whose reading went stale and one whose latest read failed are the same key: weather-unknown.
        await using var rig = (await AlpacaCommandRig.StartAsync(weatherSafe: true, mode: RoofAlpacaCommandMode.OpenAndClose, maxReadingAge: TimeSpan.FromMilliseconds(1200))).Ready();
        await Task.Delay(TimeSpan.FromMilliseconds(1400));
        (await rig.OpenAsync()).ReasonKeys.Should().Equal("weather-unknown:Weather");
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unknown:Weather"));

        rig.Weather!.Fails = true;
        await rig.Safety.PollAsync(CancellationToken.None);

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue("a failed latest read and a stale reading are the same reason key");
    }

    [TestMethod]
    public async Task OpenAndCloseGrants_AreSeparate_EachOnlyForItsOwnKind()
    {
        await using var rig = (await RigAsync(parked: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));

        (await rig.OpenAsync()).Rule().Should().Be("NotSafe", "a Close grant never lifts an Open's reason");
        rig.Commands.Grants().Should().ContainSingle(grant => grant.Kind == RoofAlpacaCommandKind.Close, "an Open never uses a Close grant");

        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("scope-not-clear:Main"));
        rig.Commands.Grants().Select(grant => grant.Kind).Should().BeEquivalentTo([RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Open], "one of each kind");

        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
        rig.Commands.Grants().Should().ContainSingle(grant => grant.Kind == RoofAlpacaCommandKind.Open, "the Close used only its own grant");
        rig.Commands.Revoke(RoofAlpacaCommandKind.Open, AlpacaCommandRig.Person()).Should().BeTrue();
        rig.Commands.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public void AnOpenGrant_NamesOnlyOpenReasons()
    {
        var rig = RigAsync().GetAwaiter().GetResult();
        try
        {
            var person = AlpacaCommandRig.Person();
            rig.Commands.Grant(person, Open()).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            rig.Commands.Grant(person, Open("fault-latched")).StatusCode.Should().Be(StatusCodes.Status400BadRequest, "a grant never lifts a hard protection");
            rig.Commands.Grant(person, Open("lease-held:ada")).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            rig.Commands.Grant(person, Open("weather-unsafe:")).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            rig.Commands.Grant(person, Close(RoofAlpacaContract.NoBlockingScopesKey)).StatusCode.Should().Be(StatusCodes.Status400BadRequest, "Close has no such reason");
            rig.Commands.Grants().Should().BeEmpty();
            rig.Commands.Grant(person, Open("weather-unsafe:Weather", "weather-unknown:Other", "scope-not-clear:Main", "scope-unknown:Guide",
                RoofAlpacaContract.NoRequiredWeatherKey, RoofAlpacaContract.NoBlockingScopesKey)).StatusCode.Should().Be(StatusCodes.Status201Created);
        }
        finally
        {
            rig.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task InBlockMode_TheGrantRouteRefusesAnOpenGrant_ButNotACloseGrant()
    {
        await using var rig = (await RigAsync(weatherSafe: false, interlockMode: RoofSafetyInterlockMode.Block)).Ready();

        var refused = rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));

        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refused.Rule.Should().Be("BlockMode");
        refused.Detail.Should().Contain("Block");
        rig.Commands.Grants().Should().BeEmpty();
        rig.Commands.GetStatus().OpenGrantUnavailable.Should().Contain("Block", "the UIs disable the Open grant control with this reason");
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-unknown:Main")).StatusCode.Should().Be(StatusCodes.Status201Created, "Block concerns Open only");
        (await rig.OpenAsync()).Rule().Should().Be("NotSafe");
        rig.RelayOnWrites(OpenRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AWarnToBlockChange_RacingAnOpenGrant_NeverLeavesAnOpenGrantInBlock()
    {
        // PR #232 review S1: the mode changes to Block between the grant's Block check and its installation. Either the
        // change wins (the grant is refused) or the grant does and the change then drops it; never an Open grant in Block.
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        Thread? change = null;
        rig.Commands.BeforeGrantInstallHook = () =>
        {
            rig.Commands.BeforeGrantInstallHook = null;
            change = new Thread(() => rig.SetInterlockMode(RoofSafetyInterlockMode.Block));
            change.Start();
            change.Join(TimeSpan.FromMilliseconds(300)); // done at once when nothing holds the gate; blocked while the grant holds it
        };

        var outcome = rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        change!.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

        rig.Commands.GetStatus().OpenGrantUnavailable.Should().NotBeNull("the mode is Block now");
        rig.Commands.Grants().Should().NotContain(grant => grant.Kind == RoofAlpacaCommandKind.Open, $"no Open grant is in force in Block (the grant answered {outcome.StatusCode})");
        (await rig.OpenAsync()).Rule().Should().Be("NotSafe");
    }

    [TestMethod]
    public async Task InBlockMode_AMatchingOpenGrantInjectedAnyway_NeverOpensTheRoof()
    {
        // Roy's decision 4 (W4d/X3): the admission itself refuses a grant in Block, not only the grant route. The grant is
        // made in Warn, then the mode becomes Block without its change notification (so nothing drops the grant).
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather")).StatusCode.Should().Be(StatusCodes.Status201Created);
        var current = rig.SafetyOptions.CurrentValue;
        rig.SafetyOptions.SetWithoutNotifying(new RoofSafetyOptions
        {
            InterlockMode = RoofSafetyInterlockMode.Block,
            PollInterval = current.PollInterval,
            ReadTimeout = current.ReadTimeout,
            MaxReadingAge = current.MaxReadingAge
        });
        rig.Commands.Grants().Should().ContainSingle("the grant was not dropped: it is the injected one");

        var outcome = await rig.OpenAsync();

        outcome.Rule().Should().Be("NotSafe");
        ((RoofControllerException)outcome.Result.Error!).Message.Should().Contain("Block");
        rig.RelayOnWrites(OpenRelay).Should().Be(0, "no grant opens the roof in Block");
        rig.Commands.Grants().Should().BeEmpty("the use-up point still spends it");
    }

    [TestMethod]
    public async Task AWarnToBlockChangeAtRuntime_DropsTheOpenGrant_AndTheOpenIsRefused()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));

        rig.SetInterlockMode(RoofSafetyInterlockMode.Block);

        rig.Commands.Grants().Should().BeEmpty("a mode change drops every grant");
        (await rig.OpenAsync()).Rule().Should().Be("NotSafe");
        rig.RelayOnWrites(OpenRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task InBlockMode_ASafeOpenStillGoes()
    {
        await using var rig = (await RigAsync(interlockMode: RoofSafetyInterlockMode.Block)).Ready();

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue("Block needs Safe, and Safe needs no override");
    }

    [TestMethod]
    public async Task AnOpenGrant_NeverBypassesAPersonsLease_OrTheStopHold()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true);
        rig.Commands.Grants().Should().BeEmpty("a person taking the lease drops every grant");
        rig.Commands.ReleaseLease(AlpacaCommandRig.Person("grace"));

        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Commands.NoteStop(AlpacaCommandRig.Person(), peerTrusted: false);
        rig.Commands.Grants().Should().BeEmpty("a person's Stop drops every grant");
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        (await rig.OpenAsync()).Rule().Should().Be("StopHold");
        rig.Commands.Grants().Should().ContainSingle("the hold refuses before the use-up point");
    }

    [TestMethod]
    public async Task CloseOnly_RefusesAnOpen_BeforeItsGrantIsUsed()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-unknown:Main"));

        rig.SetOptions(mode: RoofAlpacaCommandMode.CloseOnly);
        rig.Commands.Grants().Should().BeEmpty("AllowCommands changed");
        rig.Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));

        (await rig.OpenAsync()).Rule().Should().Be("OpenNotAllowed");
        rig.Commands.Grants().Should().ContainSingle("refused at the gate, before the use-up point");
        rig.RelayOnWrites(OpenRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AllowOpenWithoutBlockingScopesChanging_DropsEveryGrant()
    {
        await using var rig = (await RigAsync(scopes: 0)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open(RoofAlpacaContract.NoBlockingScopesKey));

        rig.SetOptions(allowOpenWithoutBlockingScopes: true);

        rig.Commands.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnOpenGrant_LapsesAfterTheOverrideWindow_OnTheMonotonicClock()
    {
        var time = new SteppingWallClockTimeProvider();
        await using var rig = await RigAsync(time: time);
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather")).Grant!.SecondsRemaining.Should().Be(300, "5 min by default");

        time.StepWallClock(TimeSpan.FromHours(-1));
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        rig.Commands.Tick();
        rig.Commands.Grants().Should().ContainSingle();

        time.Advance(TimeSpan.FromSeconds(2));
        rig.Commands.Tick();
        rig.Commands.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task ConcurrentOpens_WithOneGrant_GiveExactlyOneStart()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => rig.OpenAsync()));

        rig.RelayOnWrites(OpenRelay).Should().Be(1, "one start");
        outcomes.Count(outcome => outcome.GrantId is not null).Should().Be(1, "one command used the grant");
    }

    [TestMethod]
    public async Task AnOpenGrantStaysUsed_AfterAFailedRelayStart()
    {
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Hat.Registers.FailNextRelayWrites = 2;

        var outcome = await rig.OpenAsync();

        outcome.Result.IsSuccessful.Should().BeFalse();
        rig.Commands.Grants().Should().BeEmpty();
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task AnOpenGrant_IsUsedUp_ByASupersededOpen()
    {
        // Y8: the use-up point comes before the generation check.
        await using var rig = (await RigAsync(weatherSafe: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        var ticket = rig.Ticket(RoofAlpacaCommandKind.Open);
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        rig.Commands.NoteStop(rig.Server, peerTrusted: true);

        (await rig.OpenAsync(ticket)).Rule().Should().Be("Superseded");
        rig.Commands.Grants().Should().BeEmpty();
        rig.RelayOnWrites(OpenRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AGrantedOpenThatWaitsOutTheDelay_IsRefusedIfTheModeBecomesBlockDuringTheWait()
    {
        // R8-4: the continuation re-checks InterlockMode; the grant it used no longer lifts anything in Block.
        var delay = TimeSpan.FromSeconds(1);
        await using var rig = (await RigAsync(weatherSafe: false, directionChangeDelay: delay)).Ready();
        await Task.Delay(delay);
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        rig.Commands.NoteStop(rig.Server, peerTrusted: true);
        rig.Commands.Grant(AlpacaCommandRig.Person(), Open("weather-unsafe:Weather"));
        rig.Hat.ClearRelayWriteLog();

        var open = rig.OpenAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting Open");
        rig.SetInterlockMode(RoofSafetyInterlockMode.Block);

        var outcome = await open;
        outcome.Rule().Should().Be("NotSafe");
        rig.RelayOnWrites(OpenRelay).Should().Be(0, "the roof stays stopped");
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "ContinuationRefused" && alert.Command == RoofAlpacaCommandKind.Open);
    }
}
