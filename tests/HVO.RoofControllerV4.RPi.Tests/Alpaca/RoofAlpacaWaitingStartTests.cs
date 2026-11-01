using System.Diagnostics;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// #220 §21 required acceptance tests 1 and 2 (N1, N2), at the controller: an Alpaca Close that must wait out the
/// direction-change delay is admitted, waits without the lock, and is checked again before its relay; Stop, a person
/// taking the lease, commands turned off or the key rotated during the wait leave the roof stopped; a second Alpaca Close
/// joins the wait; a person's Close takes it over; a refusal of another request never cancels it.
/// </summary>
[TestClass]
public sealed class RoofAlpacaWaitingStartTests
{
    private const byte CloseRelay = 2;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>A rig whose roof was just stopped (the Alpaca server's AbortSlew: no hold), so a start must wait.</summary>
    private static async Task<(AlpacaCommandRig Rig, Stopwatch SinceStop, List<(TimeSpan At, byte Register, byte Value)> Writes)> JustStoppedAsync()
    {
        var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        var writes = new List<(TimeSpan, byte, byte)>();
        var clock = Stopwatch.StartNew();
        rig.Hat.Registers.RelayWriteObserver = (register, value, _) =>
        {
            lock (writes)
            {
                writes.Add((clock.Elapsed, register, value));
            }
        };
        rig.Controller.Stop().IsSuccessful.Should().BeTrue(); // the AbortSlew
        clock.Restart();
        lock (writes)
        {
            writes.Clear();
        }

        rig.Hat.ClearRelayWriteLog();
        return (rig, clock, writes);
    }

    private static TimeSpan? FirstCloseOn(List<(TimeSpan At, byte Register, byte Value)> writes)
    {
        lock (writes)
        {
            return writes.FirstOrDefault(write => write.Register == 1 && write.Value == CloseRelay) is { At: var at, Register: 1 } ? at : null;
        }
    }

    [TestMethod]
    public async Task AnAlpacaCloseWithinTheDelay_ClosesAfterIt_AnsweringWithinBudgetPlusDelayPlus400Ms()
    {
        var (rig, sinceStop, writes) = await JustStoppedAsync();
        await using var _ = rig;
        var answered = Stopwatch.StartNew();

        var outcome = await rig.CloseAsync();

        answered.Stop();
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        FirstCloseOn(writes).Should().NotBeNull().And.BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20), "the relay waits the delay since the stop (measured from Stop's return)");
        answered.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600) + Delay + TimeSpan.FromMilliseconds(400));
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task AStopDuringTheWait_AnswersAtOnce_AndNoOnLands()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        var stop = Stopwatch.StartNew();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        stop.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        (await close).Code().Should().Be(RoofControllerErrorCode.Superseded);
        await Task.Delay(Delay);
        FirstCloseOn(writes).Should().BeNull("no ON lands after the Stop");
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task APersonTakingTheLeaseDuringTheWait_LeavesTheRoofStopped_WithAnAlert()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        (await close).Rule().Should().Be("LeaseHeld");
        FirstCloseOn(writes).Should().BeNull();
        rig.Hat.RelayMask.Should().Be(0x00);
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "ContinuationRefused");
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.IsStartPending.Should().BeFalse();
        status.Status.Should().NotBe(RoofControllerStatus.Closing, "a refused continuation reads stopped, with its reason");
    }

    [TestMethod]
    public async Task TheDomeReadsClosingAndSlewingDuringTheWait_AndARefusedContinuationReadsErrorWithItsReason()
    {
        // N6 (PR #230 review F6): the accepted start reads as moving while it waits; refused when the wait ends, the roof
        // reads Error and the status keeps why.
        var (rig, _, _) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        var waiting = rig.Controller.GetCurrentStatusSnapshot();
        HVO.RoofControllerV4.Alpaca.Devices.DomeShutter.StateOf(waiting).Should().Be(HVO.RoofControllerV4.Alpaca.Devices.DomeShutterState.Closing);
        HVO.RoofControllerV4.Alpaca.Devices.DomeShutter.IsSlewing(waiting).Should().BeTrue("the start is accepted and waits out the delay");

        rig.RotateCommanderKey();
        (await close).Rule().Should().Be("AuthorizationChanged");

        var after = rig.Controller.GetCurrentStatusSnapshot();
        HVO.RoofControllerV4.Alpaca.Devices.DomeShutter.StateOf(after).Should().Be(HVO.RoofControllerV4.Alpaca.Devices.DomeShutterState.Error);
        HVO.RoofControllerV4.Alpaca.Devices.DomeShutter.IsSlewing(after).Should().BeFalse();
        after.LastError.Should().Contain("refused when its direction-change delay ended").And.Contain("changed after the ticket was issued");
    }

    [TestMethod]
    public async Task CommandsTurnedOffDuringTheWait_LeaveTheRoofStopped()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.Off });

        (await close).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        FirstCloseOn(writes).Should().BeNull();
    }

    [TestMethod]
    public async Task TheKeyRotatedDuringTheWait_LeavesTheRoofStopped()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        rig.RotateCommanderKey();

        (await close).Rule().Should().Be("AuthorizationChanged");
        FirstCloseOn(writes).Should().BeNull();
    }

    [TestMethod]
    public async Task ASecondAlpacaCloseDuringTheWait_JoinsIt_ThenAPersonTakesTheLease_BothAreRefused()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var first = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");
        var second = rig.CloseAsync(client: new RoofAlpacaClient(9, 1, "192.168.1.52", "header"));
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("it joins the waiting start and gets its real answer, never an early success");

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        (await first).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        (await second).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        FirstCloseOn(writes).Should().BeNull();
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task APersonTakingTheLeaseAndPressingClose_DuringTheAlpacaWait_ClosesUnderThePersonsLease_AfterTheDelay()
    {
        var (rig, sinceStop, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var alpaca = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        var person = await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") });

        person.IsSuccessful.Should().BeTrue(person.Error?.Message);
        (await alpaca).Result.IsSuccessful.Should().BeFalse("the person's command took the waiting start over");
        FirstCloseOn(writes).Should().NotBeNull().And.BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20), "the delay is still respected (measured from Stop's return)");
        rig.Controller.GetCurrentStatusSnapshot().MotionOwner.Should().Be(new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada"));
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task ARefusalOfAnotherRequestDuringTheWait_NeverCancelsIt()
    {
        var (rig, _, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var close = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

        rig.Commands.IssueTicket(RoofAlpacaCommandKind.Open, rig.Commander, AlpacaCommandRig.Client).Ticket.Should().BeNull("a wrong kind");
        (await rig.Commands.CloseAsync(new RoofAlpacaCommandRequest("no-such-ticket", rig.Controller.InstanceId, 1), rig.Commander, AlpacaCommandRig.Client))
            .Rule().Should().Be("TicketUnknown");

        (await close).Result.IsSuccessful.Should().BeTrue("a refusal never cancels a waiting start");
        FirstCloseOn(writes).Should().NotBeNull();
    }

    [TestMethod]
    public async Task AWaitingStartTooLateForItsDeadline_IsRefused_BeforeTheGrantIsUsed_AndTheMotionContinues()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, parked: false)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        var ticket = rig.Ticket();
        await Task.Delay(TimeSpan.FromMilliseconds(1200)); // the command arrives late: elapsed + the whole delay no longer fits

        var outcome = await rig.CloseAsync(ticket);

        outcome.Rule().Should().Be("DeadlineExceeded");
        rig.Commands.Grants().Should().ContainSingle("the deadline is checked before the use-up point");
        rig.Hat.RelayMask.Should().Be(0x09, "a refused reversal leaves the Opening running");
    }

    [TestMethod]
    public async Task AnAlpacaReversal_StopsTheRelay_ThenWaitsTheWholeDelay_BeforeTheOppositeOne()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay)).Ready();
        await Task.Delay(Delay); // the delay since initialization
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, "alpaca-external") }))
            .IsSuccessful.Should().BeTrue();
        var clock = Stopwatch.StartNew();
        TimeSpan? off = null;
        TimeSpan? on = null;
        rig.Hat.Registers.RelayWriteObserver = (register, value, mask) =>
        {
            if ((mask & 0x01) == 0 && off is null) off = clock.Elapsed;
            if (register == 1 && value == CloseRelay && on is null) on = clock.Elapsed;
        };

        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();

        on.Should().NotBeNull();
        (on!.Value - off!.Value).Should().BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20));
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
    }
}
