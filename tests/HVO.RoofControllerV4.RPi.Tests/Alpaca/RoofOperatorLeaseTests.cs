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
}
