using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The operator lease a person holds from a UI (#220, §4 U2/U3/W6, §14.4, §17 AA6, §19 L7): it keeps Alpaca commands
/// off while held; it lapses when the UI's heartbeats stop, or, only while Alpaca commands are on, after the idle limit
/// without input (heartbeats never reset it); an idle lapse never stops a motion and waits until the person's own motion
/// ends; taking it never stops a motion.
/// </summary>
[TestClass]
public sealed class RoofOperatorLeaseTests
{
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(10);

    private static async Task<(AlpacaCommandRig Rig, ManualTimeProvider Time)> StartAsync(RoofAlpacaCommandMode mode = RoofAlpacaCommandMode.CloseOnly)
    {
        var time = new ManualTimeProvider();
        var rig = (await AlpacaCommandRig.StartAsync(mode: mode, time: time)).Ready();
        return (rig, time);
    }

    /// <summary>Heartbeats every 10 s for <paramref name="span"/>, with no input, ticking the state as the timer would.</summary>
    private static void KeepBeating(AlpacaCommandRig rig, ManualTimeProvider time, TimeSpan span, string person = "ada")
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += Beat)
        {
            time.Advance(Beat);
            rig.Commands.Heartbeat(AlpacaCommandRig.Person(person), input: false);
            rig.Commands.Tick();
        }
    }

    [TestMethod]
    public async Task WhenTheUisHeartbeatsStop_TheLeaseLapses_AndAlpacaMayClose()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld");

        time.Advance(RoofAlpacaCommands.LeaseHeartbeatTimeout);
        rig.Commands.Tick();

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse();
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnOpenSessionWithHeartbeats_ButNoInput_LapsesAfterTheIdleLimit_ThenAlpacaMayClose()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        KeepBeating(rig, time, TimeSpan.FromMinutes(9));
        var lease = rig.Commands.GetStatus().OperatorLease;
        lease.Held.Should().BeTrue("heartbeats keep the UI's presence, not the person's");
        lease.IdleSecondsRemaining.Should().BeLessThanOrEqualTo(60);
        RoofAlpacaText.DescribeIdleWarning(lease, "ada").Should().Contain("lapses in");

        KeepBeating(rig, time, TimeSpan.FromMinutes(1) + Beat); // the UI keeps beating: a lapsed lease is taken again only on input
        lease = rig.Commands.GetStatus().OperatorLease;
        lease.Held.Should().BeFalse();
        lease.IdleLapsedHolder.Should().Be("ada");
        RoofAlpacaText.DescribeIdleWarning(lease, "ada").Should().Be(RoofAlpacaText.MayNowMove);
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue("Alpaca may now move the roof");

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeTrue("the person's input takes it again");
    }

    [TestMethod]
    public async Task InputResetsTheIdleClock_AHeartbeatAloneDoesNot()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        KeepBeating(rig, time, TimeSpan.FromMinutes(8));

        time.Advance(Beat);
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        KeepBeating(rig, time, TimeSpan.FromMinutes(8));

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeTrue("the input 8 min ago restarted the 10 min");
    }

    [TestMethod]
    public async Task WithCommandsOff_AnIdleLeaseIsKept_AndTheIdleClockStartsWhenCommandsGoOn()
    {
        var (rig, time) = await StartAsync(RoofAlpacaCommandMode.Off);
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        KeepBeating(rig, time, TimeSpan.FromMinutes(30));
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeTrue("the idle limit applies only while Alpaca commands are on");
        rig.Commands.GetStatus().OperatorLease.IdleSecondsRemaining.Should().BeNull();

        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.CloseOnly });
        KeepBeating(rig, time, TimeSpan.FromMinutes(9));
        var lease = rig.Commands.GetStatus().OperatorLease;
        lease.Held.Should().BeTrue("the full idle limit runs from the moment commands went on");
        lease.IdleSecondsRemaining.Should().BeLessThanOrEqualTo(60, "with the 1-minute warning");

        KeepBeating(rig, time, TimeSpan.FromMinutes(1) + Beat);
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnIdleLapse_NeverStopsThePersonsMotion_AndWaitsUntilItEnds()
    {
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(time: time, watchdog: TimeSpan.FromMinutes(5))).Ready();
        rig.Lease.Set(new RoofOperatorLeaseOptions { OperatorLeaseIdleMinutes = 1 });
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") }))
            .IsSuccessful.Should().BeTrue();

        KeepBeating(rig, time, TimeSpan.FromMinutes(2));

        rig.Controller.IsMoving.Should().BeTrue("an idle lapse never stops a motion");
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeTrue("it waits until the person's motion ends");

        rig.Controller.Stop();
        time.Advance(TimeSpan.FromSeconds(1));
        rig.Commands.Tick();
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    public async Task TakingTheLease_DuringAnAlpacaClose_LeavesItRunning_AndRefusesFurtherAlpacaCommands()
    {
        var (rig, _) = await StartAsync();
        await using var __ = rig;
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        rig.Controller.IsMoving.Should().BeTrue("taking the lease never stops a motion");
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld");
    }

    [TestMethod]
    public async Task APersonWhoGrants_StepsAway_InEverySessionOfTheirs_AndTheGrantedCloseGoes()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Scope.Parked = false;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        var tui = AlpacaCommandRig.Person("ada", sessionId: "ada-tui");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Heartbeat(tui, input: true);
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld", "a person at a UI holds Alpaca off");

        var grant = rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"])).Grant!;
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("a grant never lifts a person's lease, so theirs ends with it");
        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.Heartbeat(tui, input: false);
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("heartbeats alone do not take it back");
        rig.Commands.Grants().Should().ContainSingle();

        var outcome = await rig.CloseAsync();
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        outcome.GrantId.Should().Be(grant.Id);
    }

    [TestMethod]
    public async Task APersonComingBackAfterGranting_TakesTheLease_AndTheGrantIsDropped()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Scope.Parked = false;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: true);

        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada");
        rig.Commands.Grants().Should().BeEmpty("a person taking the lease drops every grant");
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld");
        rig.RelayOnWrites(2).Should().Be(0);
    }

    [TestMethod]
    public async Task AGrant_LeavesAnotherPersonsLease_WhichStillRefusesTheClose_WithoutUsingTheGrant()
    {
        var (rig, _) = await StartAsync();
        await using var __ = rig;
        rig.Scope.Parked = false;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true);
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: true);

        rig.Commands.Grant(AlpacaCommandRig.Person("ada"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("grace");
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld");
        rig.Commands.Grants().Should().ContainSingle("a person's lease refuses before any grant is used");
    }

    [TestMethod]
    public async Task EachPersonsHeartbeat_AnswersTheirOwnLease_SoASecondOperatorSeesTheirOwnIdleWarning()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true);
        time.Advance(TimeSpan.FromMinutes(2));
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: true);

        time.Advance(TimeSpan.FromMinutes(5));
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true); // grace keeps hers fresh
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30)); // ada: 9 min 30 s without input
        var ada = rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: false);

        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("grace", "the status names the oldest holder");
        ada.Holder.Should().Be("ada", "a heartbeat answers its own holder");
        ada.IdleSecondsRemaining.Should().BeApproximately(30, 1);
        RoofAlpacaText.DescribeIdleWarning(ada, "ada").Should().Contain("lapses in");
    }

    [TestMethod]
    [DataRow("used")]
    [DataRow("expired")]
    [DataRow("revoked")]
    [DataRow("dropped")]
    public async Task SteppingAway_EndsWithTheGrant_AndTheNextHeartbeatTakesTheLeaseBack(string end)
    {
        // PR #230 review, Opus F4: stepping away was for that one grant; once it is gone, the person's UI holds the lease
        // again without needing their input.
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Scope.Parked = false;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("stepped away while the grant lasts");

        switch (end)
        {
            case "used":
                (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
                break;
            case "expired":
                time.Advance(TimeSpan.FromMinutes(6));
                rig.Commands.Tick();
                break;
            case "revoked":
                rig.Commands.Revoke(RoofAlpacaCommandKind.Close, AlpacaCommandRig.Person("grace")).Should().BeTrue();
                break;
            default:
                rig.Commands.NoteStop(AlpacaCommandRig.Person("grace"), peerTrusted: true);
                break;
        }

        rig.Commands.Grants().Should().BeEmpty();
        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada", "the grant is gone, so the person at the UI holds the lease again");
    }

    [TestMethod]
    public async Task AnIdleLapse_IsNotUndoneWhenAGrantEnds()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: true);
        KeepBeating(rig, time, TimeSpan.FromMinutes(10) + Beat, "ada");
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("lapsed for want of input");

        rig.Commands.Grant(AlpacaCommandRig.Person("grace", sessionId: "grace-kiosk"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        rig.Commands.Revoke(RoofAlpacaCommandKind.Close, AlpacaCommandRig.Person("grace", sessionId: "grace-kiosk"));
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("only input takes back a lease that lapsed for want of it");
    }

    [TestMethod]
    public async Task AGrantedCloseThatWaitsOutTheDelay_IsNotRefusedByTheGrantersOwnHeartbeat()
    {
        // PR #230 rereview, Opus F11: the step-away lasts until the granted Close is decided, not just until its grant is
        // used, so a routine no-input heartbeat during the wait does not take the lease back and refuse it.
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: TimeSpan.FromSeconds(1), parked: false)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue(); // the AbortSlew
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the granted Close to wait");
        rig.Commands.Heartbeat(web, input: false); // the UI's routine heartbeat during the wait

        var outcome = await close;
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        rig.Hat.RelayMask.Should().Be(0x0A);

        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada", "once the granted Close is decided, the UI holds the lease again");
    }

    [TestMethod]
    public async Task GrantingAgain_KeepsTheGrantersOtherSessionsSteppedAway()
    {
        // PR #230 rereview, Opus F12: a grant made again replaces the first, and the person stays stepped away in every
        // session, so their other UI's heartbeat does not take the lease back and drop the new grant.
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        rig.Scope.Parked = false;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        var kiosk = AlpacaCommandRig.Person("ada", pin: true, sessionId: "ada-kiosk");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Heartbeat(kiosk, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        time.Advance(Beat);
        rig.Commands.Heartbeat(kiosk, input: false);
        rig.Commands.Heartbeat(web, input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse();
        rig.Commands.Grants().Should().ContainSingle("the new grant stands");
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Open, RoofAlpacaCommandKind.Close)]
    [DataRow(RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Open, RoofAlpacaCommandKind.Open)]
    [DataRow(RoofAlpacaCommandKind.Open, RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Close)]
    [DataRow(RoofAlpacaCommandKind.Open, RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Open)]
    public async Task WithAnOpenAndACloseGrant_EndingOne_KeepsThePersonSteppedAwayForTheOther(
        RoofAlpacaCommandKind first, RoofAlpacaCommandKind second, RoofAlpacaCommandKind ended)
    {
        // PR #232 review S2: the step-away belongs to every grant the person made, not only the first; ending one grant
        // leaves the person away while the other lasts, so a routine heartbeat does not take the lease back and drop it.
        var (rig, time) = await StartAsync(RoofAlpacaCommandMode.OpenAndClose);
        await using var _ = rig;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        var kiosk = AlpacaCommandRig.Person("ada", pin: true, sessionId: "ada-kiosk");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Heartbeat(kiosk, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(first, ["scope-not-clear:Main"])).Grant.Should().NotBeNull();
        rig.Commands.Grant(kiosk, new RoofOverrideGrantRequest(second, ["scope-not-clear:Main"])).Grant.Should().NotBeNull();
        rig.Commands.Grants().Should().HaveCount(2);

        rig.Commands.Revoke(ended, AlpacaCommandRig.Person("grace")).Should().BeTrue();
        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.Heartbeat(kiosk, input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("the other grant still lasts");
        var kept = ended == RoofAlpacaCommandKind.Open ? RoofAlpacaCommandKind.Close : RoofAlpacaCommandKind.Open;
        rig.Commands.Grants().Should().ContainSingle().Which.Kind.Should().Be(kept);

        // Once the other grant ends too, the person's UIs hold the lease again with their next heartbeat.
        rig.Commands.Revoke(kept, AlpacaCommandRig.Person("grace")).Should().BeTrue();
        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: false);
        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada");
    }

    [TestMethod]
    public async Task WithAnOpenAndACloseGrant_TheCloseUsingItsGrant_KeepsThePersonSteppedAwayForTheOpen()
    {
        var (rig, time) = await StartAsync(RoofAlpacaCommandMode.OpenAndClose);
        await using var _ = rig;
        rig.Scope.Parked = false;
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["scope-not-clear:Main", RoofAlpacaContract.NoRequiredWeatherKey]));

        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
        time.Advance(Beat);
        rig.Commands.Heartbeat(web, input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("the Open grant still lasts");
        rig.Commands.Grants().Should().ContainSingle().Which.Kind.Should().Be(RoofAlpacaCommandKind.Open);
    }

    [TestMethod]
    public async Task WithAnOpenAndACloseGrant_TheOpenUsingItsGrant_LeavesTheCloseGrant_AndThePersonAway()
    {
        // PR #232 review F1 (Opus's R1): "Alpaca may open and close the roof once each" holds for one person's two grants.
        await using var rig = (await AlpacaCommandRig.StartAsync(weatherSafe: false, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Weather"])).Grant.Should().NotBeNull();
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"])).Grant.Should().NotBeNull();

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();
        rig.Commands.Heartbeat(web, input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("the Close grant still lasts");
        rig.Commands.Grants().Should().ContainSingle(grant => grant.Kind == RoofAlpacaCommandKind.Close);
        rig.Scope.Parked = false;
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue("the Close grant is used as granted");
    }

    [TestMethod]
    public async Task AnotherPersonsUi_TakingTheLeaseBackWhenTheirOwnGrantEnds_DropsEveryGrant_AsAnyPersonTakingItDoes()
    {
        // PR #232 review F1 (Opus's R1b), documented: ada's own Open grant is used, so her UI holds the lease again with its
        // next heartbeat; a person taking the lease drops every grant in force (#220, §5 O12), grace's Close grant too. It
        // fails safe: ada's lease would refuse that Close anyway, and grace grants again once ada has stepped away.
        await using var rig = (await AlpacaCommandRig.StartAsync(weatherSafe: false, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        var ada = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        var grace = AlpacaCommandRig.Person("grace", pin: true, sessionId: "grace-kiosk");
        rig.Commands.Heartbeat(ada, input: true);
        rig.Commands.Heartbeat(grace, input: true);
        rig.Commands.Grant(ada, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Weather"]));
        rig.Commands.Grant(grace, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();
        rig.Commands.Heartbeat(ada, input: false);

        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada");
        rig.Commands.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnIdleLapse_IsNotUndone_WhenOneOfTheIdlePersonsTwoGrantsEnds()
    {
        // An idle lapse stays one (PR #230 review, Opus F4), also with two grants of the same person.
        var (rig, time) = await StartAsync(RoofAlpacaCommandMode.OpenAndClose);
        await using var _ = rig;
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: true);
        KeepBeating(rig, time, TimeSpan.FromMinutes(10) + Beat, "ada");
        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("lapsed for want of input");

        rig.Commands.Grant(AlpacaCommandRig.Person("ada", sessionId: "ada-kiosk", pin: true), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        rig.Commands.Grant(AlpacaCommandRig.Person("ada", sessionId: "ada-kiosk", pin: true), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["scope-not-clear:Main"]));
        rig.Commands.Revoke(RoofAlpacaCommandKind.Close, AlpacaCommandRig.Person("grace"));
        rig.Commands.Revoke(RoofAlpacaCommandKind.Open, AlpacaCommandRig.Person("grace"));
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("ada"), input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("only input takes back a lease that lapsed for want of it");
    }

    [TestMethod]
    [DataRow(RoofAlpacaCommandKind.Close)]
    [DataRow(RoofAlpacaCommandKind.Open)]
    public async Task AGrantMadeAgain_WhileTheUsedGrantsCommandWaitsOutTheDelay_SurvivesThatCommandStarting(RoofAlpacaCommandKind kind)
    {
        // PR #232 rereview S5: phase 3's known limitation (Opus F21, a re-grant dropped once the used grant's command was
        // decided) is gone with the S2 fix: the new grant holds the person's sessions too, so they stay away after it.
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: TimeSpan.FromMilliseconds(900), parked: false,
            weatherSafe: true, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(kind == RoofAlpacaCommandKind.Open ? RoofMotionDirection.Closing : RoofMotionDirection.Opening)))
            .IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue(); // the AbortSlew: the next start waits out the delay
        var web = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(web, input: true);
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(kind, ["scope-not-clear:Main"])).Grant.Should().NotBeNull();

        var command = kind == RoofAlpacaCommandKind.Open ? rig.OpenAsync() : rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the granted command to wait");
        rig.Commands.Grant(web, new RoofOverrideGrantRequest(kind, ["scope-not-clear:Main"])).Grant.Should().NotBeNull("granted again during the wait");
        (await command).Result.IsSuccessful.Should().BeTrue();
        rig.Commands.Heartbeat(web, input: false);

        rig.Commands.GetStatus().OperatorLease.Held.Should().BeFalse("the new grant still holds the person away");
        rig.Commands.Grants().Should().ContainSingle(grant => grant.Kind == kind, "the grant made again survives");
    }

    [TestMethod]
    public async Task AnotherPersonsGrant_ReplacingOne_ReleasesTheFirstGrantersStepAway()
    {
        var (rig, time) = await StartAsync();
        await using var _ = rig;
        var ada = AlpacaCommandRig.Person("ada", sessionId: "ada-web");
        rig.Commands.Heartbeat(ada, input: true);
        rig.Commands.Grant(ada, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));

        rig.Commands.Grant(AlpacaCommandRig.Person("grace", sessionId: "grace-kiosk"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        time.Advance(Beat);
        rig.Commands.Heartbeat(ada, input: false);

        rig.Commands.GetStatus().OperatorLease.Holder.Should().Be("ada", "ada's grant is gone, so her step-away is over");
    }
}
