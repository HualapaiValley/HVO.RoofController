using System.Diagnostics;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// #220 §21 acceptance tests 1 and 2 (N1, N2) for an Alpaca Open (phase 4), and §20 N8: an Alpaca Open that must wait out
/// the direction-change delay is admitted, waits without the lock, and is checked again before its relay (the gate, a
/// person's lease, the hold, the account, the Open policy on the newest readings); a Stop during the wait answers at once;
/// a second Alpaca Open joins it; a person's Open takes it over; a refusal of another request never cancels it; and an
/// Alpaca client's opposite command during an Alpaca reversal's wait goes through full reversal admission.
/// </summary>
[TestClass]
public sealed class RoofAlpacaOpenWaitingStartTests
{
    private const byte OpenRelay = 1;
    private const byte CloseRelay = 2;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>A rig whose roof was just stopped while closing (the Alpaca server's AbortSlew: no hold), so a start must wait.</summary>
    private static async Task<(AlpacaCommandRig Rig, List<(TimeSpan At, byte Register, byte Value)> Writes)> JustStoppedAsync(bool weatherSafe = true)
    {
        var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, weatherSafe: weatherSafe, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing))).IsSuccessful.Should().BeTrue();
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
        rig.Commands.NoteStop(rig.Server, peerTrusted: true);
        clock.Restart();
        lock (writes)
        {
            writes.Clear();
        }

        rig.Hat.ClearRelayWriteLog();
        return (rig, writes);
    }

    private static TimeSpan? FirstOn(List<(TimeSpan At, byte Register, byte Value)> writes, byte relay)
    {
        lock (writes)
        {
            return writes.FirstOrDefault(write => write.Register == 1 && write.Value == relay) is { At: var at, Register: 1 } ? at : null;
        }
    }

    private static Task WaitForThePendingStartAsync(AlpacaCommandRig rig)
        => AlpacaCommandRig.WaitUntilAsync(() => rig.Controller.GetCurrentStatusSnapshot().IsStartPending, "the waiting start");

    [TestMethod]
    public async Task AnAlpacaOpenWithinTheDelay_OpensAfterIt_AnsweringWithinBudgetPlusDelayPlus400Ms()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var _ = rig;
        var answered = Stopwatch.StartNew();

        var outcome = await rig.OpenAsync();

        answered.Stop();
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        FirstOn(writes, OpenRelay).Should().NotBeNull().And.BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20), "the relay waits the delay since the stop");
        answered.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600) + Delay + TimeSpan.FromMilliseconds(400));
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task TheDomeReadsOpeningAndSlewingDuringTheWait_AndARefusedContinuationReadsErrorWithItsReason()
    {
        var (rig, _) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        var waiting = rig.Controller.GetCurrentStatusSnapshot();
        DomeShutter.StateOf(waiting).Should().Be(DomeShutterState.Opening);
        DomeShutter.IsSlewing(waiting).Should().BeTrue();

        rig.RotateCommanderKey();
        (await open).Rule().Should().Be("AuthorizationChanged");

        var after = rig.Controller.GetCurrentStatusSnapshot();
        DomeShutter.StateOf(after).Should().Be(DomeShutterState.Error);
        after.LastError.Should().Contain("waiting opening was refused when its direction-change delay ended");
    }

    [TestMethod]
    public async Task AStopDuringTheWait_AnswersAtOnce_AndNoOnLands()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        var stop = Stopwatch.StartNew();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        stop.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        (await open).Code().Should().Be(RoofControllerErrorCode.Superseded);
        await Task.Delay(Delay);
        FirstOn(writes, OpenRelay).Should().BeNull("no ON lands after the Stop");
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task APersonTakingTheLeaseDuringTheWait_LeavesTheRoofStopped_WithAnAlert()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        (await open).Rule().Should().Be("LeaseHeld");
        FirstOn(writes, OpenRelay).Should().BeNull();
        rig.Hat.RelayMask.Should().Be(0x00);
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "ContinuationRefused" && alert.Command == RoofAlpacaCommandKind.Open);
    }

    [TestMethod]
    [DataRow(RoofAlpacaCommandMode.Off)]
    [DataRow(RoofAlpacaCommandMode.CloseOnly)]
    public async Task OpenTurnedOffDuringTheWait_LeavesTheRoofStopped(RoofAlpacaCommandMode mode)
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        rig.SetOptions(mode: mode);

        (await open).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        await Task.Delay(100);
        FirstOn(writes, OpenRelay).Should().BeNull();
    }

    [TestMethod]
    public async Task TheKeyRotatedDuringTheWait_LeavesTheRoofStopped()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        rig.RotateCommanderKey();

        (await open).Rule().Should().Be("AuthorizationChanged");
        FirstOn(writes, OpenRelay).Should().BeNull();
    }

    [TestMethod]
    public async Task TheWeatherTurningUnsafeDuringTheWait_RefusesTheContinuation_AndTheRoofStaysStopped()
    {
        // R8-4: the continuation judges the Open policy on the newest readings (no new re-read).
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        rig.Weather!.Safe = false;
        await rig.Safety.PollAsync(CancellationToken.None);

        var outcome = await open;
        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().Equal("weather-unsafe:Weather");
        FirstOn(writes, OpenRelay).Should().BeNull();
        rig.Commands.Alerts().Should().Contain(alert => alert.Kind == "ContinuationRefused" && alert.Command == RoofAlpacaCommandKind.Open);
    }

    [TestMethod]
    public async Task ASecondAlpacaOpenDuringTheWait_JoinsIt_ThenAPersonTakesTheLease_BothAreRefused()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var first = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);
        var second = rig.OpenAsync(client: new RoofAlpacaClient(9, 1, "192.168.1.52", "header"));
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("it joins the waiting start and gets its real answer, never an early success");

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);

        (await first).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        (await second).Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        FirstOn(writes, OpenRelay).Should().BeNull();
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ASecondAlpacaOpenDuringTheWait_JoinsIt_AndBothSucceed_WithOneStart()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var first = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        var second = await rig.OpenAsync(client: new RoofAlpacaClient(9, 1, "192.168.1.52", "header"));

        second.Result.IsSuccessful.Should().BeTrue();
        (await first).Result.IsSuccessful.Should().BeTrue();
        rig.RelayOnWrites(OpenRelay).Should().Be(1, "one continuation");
        FirstOn(writes, OpenRelay).Should().BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20));
    }

    [TestMethod]
    public async Task APersonTakingTheLeaseAndPressingOpen_DuringTheAlpacaWait_OpensUnderThePersonsLease_AfterTheDelay()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var alpaca = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        rig.Commands.Heartbeat(AlpacaCommandRig.Person(), input: true);
        var person = await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") });

        person.IsSuccessful.Should().BeTrue(person.Error?.Message);
        (await alpaca).Result.IsSuccessful.Should().BeFalse("the person's command took the waiting start over");
        FirstOn(writes, OpenRelay).Should().NotBeNull().And.BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20), "the delay is still respected");
        rig.Controller.GetCurrentStatusSnapshot().MotionOwner.Should().Be(new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada"));
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task ARefusalOfAnotherRequestDuringTheWait_NeverCancelsIt()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        (await rig.OpenAsync(rig.Ticket(RoofAlpacaCommandKind.Close))).Rule().Should().Be("TicketMismatch", "a wrong kind");
        (await rig.Commands.OpenAsync(new RoofAlpacaCommandRequest("no-such-ticket", rig.Controller.InstanceId, 1), rig.Commander, AlpacaCommandRig.Client))
            .Rule().Should().Be("TicketUnknown");
        (await rig.Commands.OpenAsync(new RoofAlpacaCommandRequest(null, rig.Controller.InstanceId, 1), rig.Commander, AlpacaCommandRig.Client))
            .Rule().Should().Be("TicketInvalid");

        (await open).Result.IsSuccessful.Should().BeTrue("a refusal never cancels a waiting start");
        FirstOn(writes, OpenRelay).Should().NotBeNull();
    }

    [TestMethod]
    public async Task AWaitingOpenTooLateForItsDeadline_IsRefused_BeforeTheGrantIsUsed_AndTheCloseContinues()
    {
        // N4: a POST delayed until near its deadline is refused before the use-up point, and the current motion runs on.
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, weatherSafe: false, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        await Task.Delay(Delay);
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Weather"]));
        var ticket = rig.Ticket(RoofAlpacaCommandKind.Open);
        await Task.Delay(TimeSpan.FromMilliseconds(1200)); // elapsed + the whole delay no longer fits

        var outcome = await rig.OpenAsync(ticket);

        outcome.Rule().Should().Be("DeadlineExceeded");
        rig.Commands.Grants().Should().ContainSingle("the deadline is checked before the use-up point");
        rig.Hat.RelayMask.Should().Be(0x0A, "a refused reversal leaves the Close running");
    }

    [TestMethod]
    public async Task AnAlpacaOpenReversingAnAlpacaClose_StopsTheRelay_ThenWaitsTheWholeDelay_BeforeTheOpenOne()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, weatherSafe: true, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        await Task.Delay(Delay);
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue();
        var clock = Stopwatch.StartNew();
        TimeSpan? off = null;
        TimeSpan? on = null;
        rig.Hat.Registers.RelayWriteObserver = (register, value, mask) =>
        {
            if ((mask & 0x02) == 0 && off is null) off = clock.Elapsed;
            if (register == 1 && value == OpenRelay && on is null) on = clock.Elapsed;
        };

        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();

        on.Should().NotBeNull();
        (on!.Value - off!.Value).Should().BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20));
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task AnAlpacaOpenDuringAnAlpacaClosesReversalWait_IsAFullReversal_AndTheDelayIsStillRespected()
    {
        // §20 N8 (phase 4): an Alpaca client's opposite command during an Alpaca reversal. The Alpaca Open runs, an Alpaca
        // Close reverses it (relays off, waiting out the delay), and an Alpaca Open during that wait is admitted as a
        // reversal of the waiting Close: the Close is superseded, and the Open still waits the delay since the relays went off.
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, weatherSafe: true, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        await Task.Delay(Delay);
        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();
        var clock = Stopwatch.StartNew();
        TimeSpan? off = null;
        var ons = new List<(TimeSpan At, byte Value)>();
        rig.Hat.Registers.RelayWriteObserver = (register, value, mask) =>
        {
            lock (ons)
            {
                if ((mask & 0x01) == 0 && off is null) off = clock.Elapsed;
                if (register == 1 && value is OpenRelay or CloseRelay) ons.Add((clock.Elapsed, value));
            }
        };

        var close = rig.CloseAsync();
        await WaitForThePendingStartAsync(rig);
        rig.Controller.GetCurrentStatusSnapshot().PendingMotion.Should().Be(RoofMotionDirection.Closing);
        var generation = rig.Controller.CommandGeneration;

        var reopen = await rig.OpenAsync(client: new RoofAlpacaClient(9, 2, "192.168.1.52", "basic"));

        reopen.Result.IsSuccessful.Should().BeTrue(reopen.Result.Error?.Message);
        (await close).Code().Should().Be(RoofControllerErrorCode.Superseded, "the opposite command replaced the waiting Close");
        rig.Controller.CommandGeneration.Should().BeGreaterThan(generation, "an admitted reversal moves the generation");
        lock (ons)
        {
            ons.Should().ContainSingle("the Close never energized").Which.Value.Should().Be(OpenRelay);
            (ons[0].At - off!.Value).Should().BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20), "the delay since the relays went off is respected");
        }

        rig.Hat.EverBothDirectionBits.Should().BeFalse();
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task AnUnsafeAlpacaOpenDuringAnAlpacaClosesReversalWait_IsRefused_AndTheCloseGoesAhead()
    {
        // §19 M2 / N8: the opposite command is admitted or refused per its policy; a refusal never cancels the wait.
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: Delay, weatherSafe: true, mode: RoofAlpacaCommandMode.OpenAndClose)).Ready();
        await Task.Delay(Delay);
        (await rig.OpenAsync()).Result.IsSuccessful.Should().BeTrue();
        var close = rig.CloseAsync();
        await WaitForThePendingStartAsync(rig);
        rig.Weather!.Safe = false;
        await rig.Safety.PollAsync(CancellationToken.None);
        var generation = rig.Controller.CommandGeneration;

        (await rig.OpenAsync()).Rule().Should().Be("NotSafe");

        rig.Controller.CommandGeneration.Should().Be(generation, "a refusal never moves the generation");
        (await close).Result.IsSuccessful.Should().BeTrue("the waiting Close goes ahead");
        rig.Hat.RelayMask.Should().Be(0x0A);
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnAlpacaCloseDuringAnAlpacaOpensWait_ReplacesIt_AndClosesAfterTheDelay()
    {
        var (rig, writes) = await JustStoppedAsync();
        await using var __ = rig;
        var open = rig.OpenAsync();
        await WaitForThePendingStartAsync(rig);

        var close = await rig.CloseAsync();

        close.Result.IsSuccessful.Should().BeTrue(close.Result.Error?.Message);
        (await open).Code().Should().Be(RoofControllerErrorCode.Superseded);
        FirstOn(writes, OpenRelay).Should().BeNull();
        FirstOn(writes, CloseRelay).Should().NotBeNull().And.BeGreaterThanOrEqualTo(Delay - TimeSpan.FromMilliseconds(20));
        rig.Hat.RelayMask.Should().Be(0x0A);
    }
}
