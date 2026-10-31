using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// An Alpaca Close through the controller's command rules (#220, phase 3): commands on, the human Stop hold, a person's
/// operator lease, the Close policy (blocking telescopes only, never the weather), the repeat and no-op rules, the use-up
/// rule of override grants, and the controller's own protections.
/// </summary>
[TestClass]
public sealed class RoofAlpacaCloseTests
{
    private const byte CloseRelay = 2;

    [TestMethod]
    public async Task ASafeClose_StartsTheRoofClosing_AsTheAlpacaAccountsMotion()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        rig.Hat.RelayMask.Should().Be(0x0A);
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.Status.Should().Be(RoofControllerStatus.Closing);
        status.MotionOwner.Should().Be(new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external"));
        status.LeaseSecondsRemaining.Should().BeNull("the Alpaca account's lease is permanent: its motion has no renewal deadline");
        rig.Commands.Alerts().Should().BeEmpty();
    }

    [TestMethod]
    public async Task CommandsOff_RefusesTheClose_WithNoAlert_AndNothingMoves()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(mode: RoofAlpacaCommandMode.Off)).Ready();

        var outcome = await rig.CloseAsync();

        outcome.Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        outcome.Rule().Should().Be("CommandsOff");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Alerts().Should().BeEmpty("commands off are log-only");
    }

    [TestMethod]
    public async Task TheStopHoldSetAtStart_RefusesTheClose_WithAnAlert_UntilAPersonClearsIt()
    {
        await using var rig = await AlpacaCommandRig.StartAsync();

        var held = await rig.CloseAsync();

        held.Rule().Should().Be("StopHold");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Alerts().Should().ContainSingle(alert => alert.Kind == "Refused");
        rig.Commands.GetStatus().Hold.Held.Should().BeTrue();

        rig.Commands.ClearHold(AlpacaCommandRig.Person());
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task APersonsStop_SetsTheHold_ButTheAlpacaServersOwnAbortSlewDoesNot()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();

        rig.Commands.NoteStop(rig.Server, peerTrusted: true);
        rig.Commands.GetStatus().Hold.Held.Should().BeFalse("the Alpaca server's own AbortSlew never holds Alpaca off");

        rig.Commands.NoteStop(rig.Server, peerTrusted: false);
        rig.Commands.GetStatus().Hold.Held.Should().BeTrue("only from a trusted Alpaca server address");
        rig.Commands.ClearHold(AlpacaCommandRig.Person());

        rig.Commands.NoteStop(rig.Principal(AlpacaCommandRig.CommanderKey), peerTrusted: true);
        rig.Commands.GetStatus().Hold.Held.Should().BeTrue("any other account's Stop holds");
        rig.Commands.ClearHold(AlpacaCommandRig.Person());

        rig.Commands.NoteStop(AlpacaCommandRig.Person("grace"), peerTrusted: true);
        var hold = rig.Commands.GetStatus().Hold;
        hold.Held.Should().BeTrue();
        hold.SetBy.Should().Contain("grace");
        rig.Commands.ClearHold(AlpacaCommandRig.Person());

        rig.Commands.NoteStop(null, peerTrusted: false);
        rig.Commands.GetStatus().Hold.Held.Should().BeTrue("an anonymous Stop holds too");
    }

    [TestMethod]
    public async Task APersonHoldingTheOperatorLease_RefusesTheClose_UntilTheyReleaseIt()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ada = AlpacaCommandRig.Person("ada");
        rig.Commands.Heartbeat(ada, input: true);

        var refused = await rig.CloseAsync();

        refused.Rule().Should().Be("LeaseHeld");
        ((RoofControllerException)refused.Result.Error!).Message.Should().Contain("Operator lease held by ada");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Alerts().Should().Contain(alert => alert.Message.Contains("operator lease", StringComparison.Ordinal));

        rig.Commands.ReleaseLease(ada);
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task ABlockingTelescopeNotParked_RefusesTheClose_WithItsReason_AndAnAlert()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();

        var outcome = await rig.CloseAsync();

        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().Equal("scope-not-clear:Main");
        ((RoofControllerException)outcome.Result.Error!).ReasonKeys.Should().Equal("scope-not-clear:Main");
        rig.RelayOnWrites(CloseRelay).Should().Be(0, "a refused Close is never queued");
        var alert = rig.Commands.Alerts().Should().ContainSingle().Subject;
        alert.ReasonKeys.Should().Equal("scope-not-clear:Main");
        alert.ClientId.Should().Be(7u);
        alert.ClientAddress.Should().Be("192.168.1.50");
    }

    [TestMethod]
    public async Task ABlockingTelescopeThatCannotBeRead_IsUnknown_AndRefusesTheClose()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Scope.Fails = true;

        var outcome = await rig.CloseAsync();

        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().Equal("scope-unknown:Main");
    }

    [TestMethod]
    public async Task ParkThenClose_TheLastMomentReReadSeesThePark_ThoughThePollSawItUnparked()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Safety.GetCurrent().Sources.Single().State.Should().Be(RoofSafetyState.Unsafe, "the background poll saw it unparked");

        rig.Scope.Parked = true; // the mount parks; the next poll is ten minutes away
        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        rig.Scope.Rereads.Should().Be(1);
    }

    [TestMethod]
    public async Task UnsafeOrMissingWeather_NeverGatesClose()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(weatherSafe: false)).Ready();

        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue("weather never gates Close");
    }

    [TestMethod]
    public async Task NoBlockingTelescope_TheCloseGoes_AndTheStatusWarnsThatItChecksNone()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(scopes: 0)).Ready();

        rig.Commands.GetStatus().Warnings.Should().Contain(RoofAlpacaCommands.NoBlockingScopeWarning);
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task AConfiguredBlockingTelescopeMissingFromTheLiveList_IsUnknown_AndRefusesTheClose()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(configuredTelescopes: ["Guide"])).Ready();

        var outcome = await rig.CloseAsync();

        outcome.Rule().Should().Be("NoFreshEvidence", "a configured telescope that is not read has no read after the command arrived");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task TheSafetyServiceNotStarted_RefusesTheClose()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(startSafety: false)).Ready();

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeFalse();
        outcome.Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task ARepeatWhileClosing_Succeeds_WithNoRelayWrite_AndUsesNoGrant()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
        var writes = rig.Hat.RelayWriteLog.Count;
        var generation = rig.Controller.CommandGeneration;
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]));
        rig.Scope.Parked = false;

        var repeat = await rig.CloseAsync();

        repeat.Result.IsSuccessful.Should().BeTrue("the same direction while moving succeeds");
        rig.Hat.RelayWriteLog.Count.Should().Be(writes, "a repeat writes no relay");
        rig.Controller.CommandGeneration.Should().Be(generation, "a repeat moves no generation");
        rig.Commands.Grants().Should().ContainSingle("a repeat uses no grant");
    }

    [TestMethod]
    public async Task AlreadyClosed_IsANoOpSuccess_SendsNothing_AndUsesNoGrant()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Hat.SetInputs(true, false, false, false); // the closed limit (NC: LOW = active)
        rig.Controller.ForceStatusRefresh();
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]));
        rig.Hat.ClearRelayWriteLog();

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeTrue();
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Grants().Should().ContainSingle("a no-op uses no grant");
    }

    [TestMethod]
    public async Task RepeatsAndNoOps_AreRefused_UnderCommandsOff_TheHold_OrAPersonsLease()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Hat.SetInputs(true, false, false, false);
        rig.Controller.ForceStatusRefresh();

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        (await rig.CloseAsync()).Rule().Should().Be("LeaseHeld");
        rig.Commands.ReleaseLease(AlpacaCommandRig.Person());

        rig.Commands.NoteStop(AlpacaCommandRig.Person(), peerTrusted: false);
        (await rig.CloseAsync()).Rule().Should().Be("StopHold");
        rig.Commands.ClearHold(AlpacaCommandRig.Person());

        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.Off });
        (await rig.CloseAsync()).Rule().Should().Be("CommandsOff");
    }

    [TestMethod]
    public async Task ALatchedFault_RefusesTheClose_BeforeAnyGrantIsUsed()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        rig.Hat.SetInputs(true, true, true, false); // the drive fault latches
        rig.Controller.ForceStatusRefresh();

        var outcome = await rig.CloseAsync();

        outcome.Code().Should().Be(RoofControllerErrorCode.FaultLatched);
        rig.Commands.Grants().Should().ContainSingle("a hard protection refuses before the use-up point");
        rig.Commands.Alerts().Should().ContainSingle(alert => alert.ReasonKeys.Contains("fault-latched"), "a latch refusing an Alpaca Close is an alert (§6)");
    }

    [TestMethod]
    public async Task APersonsMotion_IsNeverReversedByAlpaca_AndContinues()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") }))
            .IsSuccessful.Should().BeTrue();

        var outcome = await rig.CloseAsync();

        outcome.Rule().Should().Be("PersonsMotion");
        rig.Hat.RelayMask.Should().Be(0x09, "the person's Open continues");
    }

    [TestMethod]
    public async Task AMotionStartedWithoutALease_CountsAsAPersons_AndIsNeverReversed()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Controller.Open().IsSuccessful.Should().BeTrue(); // a local input: no lease, no person named

        (await rig.CloseAsync()).Rule().Should().Be("PersonsMotion");
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task TheAlpacaAccountsOwnOpening_IsReversed_WithFullAdmission()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        // Test-only: there is no Alpaca Open in phase 3, so the Alpaca-owned Opening is started directly.
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        rig.Hat.RelayMask.Should().Be(0x0A);
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnAlpacaReversal_ThatIsNotSafe_LeavesTheCurrentMotionUnchanged()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();
        rig.Scope.Parked = false;

        (await rig.CloseAsync()).Rule().Should().Be("NotSafe");
        rig.Hat.RelayMask.Should().Be(0x09, "a refused reversal leaves the motion as it was");
    }

    [TestMethod]
    public async Task UnderThePermanentAlpacaLease_TheWatchdogStillStopsAndLatches_AndNoLeaseExpiryApplies()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(operatorLeaseTimeout: TimeSpan.FromSeconds(2), watchdog: TimeSpan.FromSeconds(5))).Ready();
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        rig.Controller.RunSupervisionCycle();
        rig.Controller.IsMoving.Should().BeTrue("the Alpaca account's lease is permanent: no renewal is due");

        await AlpacaCommandRig.WaitUntilAsync(() => { rig.Controller.RunSupervisionCycle(); return !rig.Controller.IsMoving; }, "the watchdog", TimeSpan.FromSeconds(10));
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        status.IsFaultLatched.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ChangesDuringAnAlpacaClose_NeverStopIt_ButApplyToLaterCommands()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();

        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.Off });
        rig.RotateCommanderKey();
        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        rig.Hat.RelayMask.Should().Be(0x0A, "only Stop and the protective stops stop a motion");
        rig.Controller.IsMoving.Should().BeTrue();
    }

    [TestMethod]
    public async Task ABlockingTelescopeUnparkingMidClose_LetsTheRoofContinue_AndAlertsTheUis()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();

        rig.Scope.Parked = false;
        await rig.Safety.PollAsync(CancellationToken.None);
        rig.Commands.Tick();

        rig.Hat.RelayMask.Should().Be(0x0A, "a soft condition changing mid-motion never stops it");
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "MidMotion" && alert.ReasonKeys.SequenceEqual(new[] { "scope-not-clear:Main" }));
    }
}
