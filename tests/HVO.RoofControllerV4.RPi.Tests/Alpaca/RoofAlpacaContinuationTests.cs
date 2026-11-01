using System.Diagnostics;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The rest of #220's phase 3 acceptance (§21.4), at the controller (PR #230 review: Opus F2 and F9, Sol's §21 table):
/// a waiting start's continuation judges the newest readings and the grant it used (§18 R8-4); a Stop during a held re-read
/// and during an Alpaca reversal's delay; a person's opposite command during an Alpaca reversal's delay (§20 N8); a hanging
/// re-read while the drive faults (§10 S2); and a duplicate in progress across the ticket's five seconds (§19 L1).
/// </summary>
[TestClass]
public sealed class RoofAlpacaContinuationTests
{
    private const byte OpenRelay = 1;
    private const byte CloseRelay = 2;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>A rig whose roof was just stopped part way open, so a Close must wait out the delay.</summary>
    private static async Task<AlpacaCommandRig> JustStoppedAsync(bool parked = true)
    {
        var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, parked: parked)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        rig.Hat.ClearRelayWriteLog();
        return rig;
    }

    private static Task WaitingAsync(AlpacaCommandRig rig)
        => AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

    /// <summary>
    /// A newer reading of the telescope during the wait (another command's re-read records it): the continuation judges
    /// the newest readings, with no re-read of its own (§18 R8-4).
    /// </summary>
    private static async Task NewerReadingAsync(AlpacaCommandRig rig)
    {
        var before = rig.Scope.Rereads;
        rig.Ticket();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Rereads > before, "a newer reading");
        await Task.Delay(50);
    }

    [TestMethod]
    [DataRow(false, "scope-not-clear:Main", DisplayName = "not clear")]
    [DataRow(true, "scope-unknown:Main", DisplayName = "unknown")]
    public async Task ABlockingTelescopeChangingDuringTheWait_RefusesTheContinuation_AndTheRoofStaysStopped(bool fails, string reason)
    {
        await using var rig = await JustStoppedAsync();
        var close = rig.CloseAsync();
        await WaitingAsync(rig);

        if (fails)
        {
            rig.Scope.Fails = true;
        }
        else
        {
            rig.Scope.Parked = false;
        }

        await NewerReadingAsync(rig);
        var outcome = await close;

        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().Equal(reason);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Hat.RelayMask.Should().Be(0x00);
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "ContinuationRefused" && alert.ReasonKeys.Contains(reason));
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task ANewReasonDuringTheWait_IsNotLiftedByTheGrantTheCloseUsed()
    {
        await using var rig = await JustStoppedAsync(parked: false);
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        var close = rig.CloseAsync();
        await WaitingAsync(rig);
        rig.Commands.Grants().Should().BeEmpty("the waiting Close used the grant at its admission");

        rig.Scope.Fails = true; // now unknown: a reason the grant was not given for
        await NewerReadingAsync(rig);
        var outcome = await close;

        outcome.Rule().Should().Be("NotSafe");
        outcome.Result.Error!.Message.Should().Contain("covered other reasons");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task TheSameReasonDuringTheWait_StaysLiftedByTheGrantTheCloseUsed()
    {
        await using var rig = await JustStoppedAsync(parked: false);
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        var close = rig.CloseAsync();
        await WaitingAsync(rig);

        await NewerReadingAsync(rig); // still not parked
        var outcome = await close;

        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task AStopDuringAHeldReRead_AnswersAtOnce_AndTheCloseIsSuperseded()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Scope.HoldRereads = true;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Rereads >= 1, "the held re-read");

        var watch = Stopwatch.StartNew();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "a re-read never holds the controller's lock");

        rig.Scope.ReleaseOne(true);
        (await close).Code().Should().Be(RoofControllerErrorCode.Superseded);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AStopDuringAnAlpacaReversalsDelay_LeavesTheRoofStopped()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay)).Ready();
        await Task.Delay(Delay); // the delay since the controller's start
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();
        rig.Hat.ClearRelayWriteLog();
        var close = rig.CloseAsync();
        await WaitingAsync(rig);
        rig.Hat.RelayMask.Should().Be(0x00, "the reversal switched the Opening off first");

        var watch = Stopwatch.StartNew();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue(); // the Alpaca server's AbortSlew
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        (await close).Code().Should().Be(RoofControllerErrorCode.Superseded);
        await Task.Delay(Delay);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task APersonsOpenDuringAnAlpacaReversalsDelay_ReplacesIt_AndOpensUnderThatPerson_AfterTheDelay()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay)).Ready();
        await Task.Delay(Delay);
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();
        rig.Hat.ClearRelayWriteLog();
        var close = rig.CloseAsync();
        await WaitingAsync(rig);
        var since = Stopwatch.StartNew();

        var open = await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") });

        open.IsSuccessful.Should().BeTrue(open.Error?.Message);
        since.Elapsed.Should().BeLessThan(Delay + TimeSpan.FromMilliseconds(500), "it waited only what was left of the delay");
        (await close).Code().Should().Be(RoofControllerErrorCode.Superseded);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Hat.RelayMask.Should().Be(0x09);
        rig.Controller.GetCurrentStatusSnapshot().MotionOwner.Should().Be(new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada"));
    }

    [TestMethod]
    public async Task AHangingReReadWhileTheDriveFaults_RefusesTheClose_AndNothingMoves()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Scope.HoldRereads = true;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Rereads >= 1, "the held re-read");

        rig.Hat.SetInputs(true, true, true, false);
        rig.Controller.SimFaultRaw(true);
        rig.Scope.ReleaseOne(true);

        (await close).Code().Should().Be(RoofControllerErrorCode.FaultLatched);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ADuplicateStillInProgressAcrossTheTicketsFiveSeconds_GetsTheFirstAnswer_AndOneStart()
    {
        var time = new ManualTimeProvider();
        var delay = TimeSpan.FromSeconds(6);
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: delay, time: time)).Ready();
        await AdvanceAsync(time, delay + TimeSpan.FromSeconds(1));
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        rig.Hat.ClearRelayWriteLog();

        var ticket = rig.Ticket();
        var first = rig.CloseAsync(ticket);
        await WaitingAsync(rig);
        await AdvanceAsync(time, TimeSpan.FromMilliseconds(5500));
        var duplicate = rig.CloseAsync(ticket);
        await Task.Delay(100);
        duplicate.IsCompleted.Should().BeFalse("a duplicate of a command still in progress waits for its answer, even past five seconds");

        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1000));
        var answers = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(10));

        answers.Should().OnlyContain(answer => answer.Result.IsSuccessful);
        rig.RelayOnWrites(CloseRelay).Should().Be(1, "one start");
    }

    [TestMethod]
    public async Task UsedAndExpiredTicketsDuringTheWait_ChangeNothing_AndTheWaitingCloseGoesAhead()
    {
        // §19 M2 (PR #230 rereview O-F9): a request refused for its own fault during the wait never touches the waiting
        // start: an expired ticket, and a used ticket (which only gets its own stored answer).
        var time = new ManualTimeProvider();
        var delay = TimeSpan.FromSeconds(3);
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: delay, time: time)).Ready();
        await AdvanceAsync(time, delay + TimeSpan.FromMilliseconds(500));
        var stale = rig.Ticket();
        var used = rig.Ticket();
        (await rig.CloseAsync(used)).Result.IsSuccessful.Should().BeTrue(); // its first request: a Close, from mid-travel
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        await AdvanceAsync(time, TimeSpan.FromSeconds(6)); // the stale ticket expires; the delay since the Stop has run
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        rig.Hat.ClearRelayWriteLog();

        var close = rig.CloseAsync();
        await WaitingAsync(rig);
        (await rig.CloseAsync(stale)).Rule().Should().Be("TicketExpired");
        (await rig.CloseAsync(used)).Result.IsSuccessful.Should().BeTrue("a used ticket gets its first request's stored answer, no second start");
        close.IsCompleted.Should().BeFalse();

        await AdvanceAsync(time, delay + TimeSpan.FromMilliseconds(500));
        (await close.WaitAsync(TimeSpan.FromSeconds(10))).Result.IsSuccessful.Should().BeTrue("neither refusal cancelled the waiting Close");
        rig.RelayOnWrites(CloseRelay).Should().Be(1, "one start, the waiting Close's");
    }

    private static async Task AdvanceAsync(ManualTimeProvider time, TimeSpan by)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var done = TimeSpan.Zero; done < by; done += step)
        {
            time.Advance(step);
            await Task.Delay(5);
        }
    }
}
