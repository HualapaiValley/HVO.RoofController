using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Supervision of the limit the roof departs from (the limit opposite to the direction of travel). Motion starting on a
/// limit must see that limit release for the debounce window; after that, any reassertion means the roof may be moving
/// the wrong way and stops with a latched <see cref="RoofControllerStopReason.StartLimitReasserted"/>. All timing is on
/// a <see cref="ManualTimeProvider"/> and supervision runs only when the test calls it.
/// </summary>
[TestClass]
public class RoofControllerLimitDepartureTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    // NC wiring: raw LOW = limit active.
    private static (bool In1, bool In2) AtLimit(bool open) => open ? (false, true) : (true, false);

    private static readonly (bool In1, bool In2) MidTravel = (true, true);

    private sealed record Rig(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time) : IDisposable
    {
        public void Set((bool In1, bool In2) limits) => Hat.SetInputs(limits.In1, limits.In2, false, false);

        public void Cycle() => Service.RunSupervisionCycle();

        public void Dispose() => Service.Dispose();
    }

    private static async Task<Rig> StartDepartingAsync(bool opening, TimeSpan? debounce = null)
    {
        var hat = new FakeRoofHat();
        var (in1, in2) = AtLimit(open: !opening); // opening departs from Closed, closing departs from Open
        hat.SetInputs(in1, in2, false, false);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time, opts => opts.LimitSwitchDebounce = debounce ?? Debounce);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Status.Should().Be(opening ? RoofControllerStatus.Closed : RoofControllerStatus.Open);

        (opening ? service.Open() : service.Close()).IsSuccessful.Should().BeTrue();
        service.IsMoving.Should().BeTrue("motion starting on the departure limit is allowed");
        return new Rig(service, hat, time);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "Opening from Closed")]
    [DataRow(false, DisplayName = "Closing from Open")]
    public async Task DepartureLimitStillAsserted_AtStart_ShouldNotStop(bool opening)
    {
        using var rig = await StartDepartingAsync(opening);

        rig.Cycle();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Cycle();

        rig.Service.IsMoving.Should().BeTrue("the departure limit takes time to release");
        rig.Hat.RelayMask.Should().Be(opening ? (byte)0x09 : (byte)0x0A);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VerifiedRelease_ThenReassertion_ShouldStopWithStartLimitReasserted(bool opening)
    {
        using var rig = await StartDepartingAsync(opening);

        rig.Set(MidTravel);
        rig.Cycle();                        // release first observed
        rig.Time.Advance(Debounce);
        rig.Cycle();                        // release held for the debounce window: verified
        rig.Service.IsMoving.Should().BeTrue();

        rig.Set(AtLimit(open: !opening));   // the departure limit asserts again
        rig.Cycle();

        rig.Service.IsMoving.Should().BeFalse();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        var snapshot = rig.Service.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        rig.Hat.RelayMask.Should().Be(0x00);
        (opening ? rig.Service.Open() : rig.Service.Close()).ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChatterInsideTheDebounceWindow_ShouldNotStop_AndShouldRestartTheWindow(bool opening)
    {
        using var rig = await StartDepartingAsync(opening);
        var departing = AtLimit(open: !opening);

        rig.Set(MidTravel);
        rig.Cycle();                                      // release observed at t0
        rig.Time.Advance(Debounce - TimeSpan.FromMilliseconds(10));
        rig.Set(departing);
        rig.Cycle();                                      // bounce back inside the window: chatter
        rig.Service.IsMoving.Should().BeTrue("reassertion before the release is verified is switch chatter");

        rig.Set(MidTravel);
        rig.Cycle();                                      // release observed again at t1: the window restarts
        rig.Time.Advance(Debounce - TimeSpan.FromMilliseconds(10));
        rig.Cycle();                                      // still inside the restarted window
        rig.Set(departing);
        rig.Cycle();
        rig.Service.IsMoving.Should().BeTrue("the window restarted, so this is still chatter");

        rig.Set(MidTravel);
        rig.Cycle();
        rig.Time.Advance(Debounce);
        rig.Cycle();                                      // stable for the full window: verified
        rig.Service.IsMoving.Should().BeTrue();

        rig.Set(departing);
        rig.Cycle();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChatterViaEdgeEvents_ShouldBehaveLikeDirectReads(bool opening)
    {
        using var rig = await StartDepartingAsync(opening);
        void Edge(bool rawHigh)
        {
            if (opening) rig.Service.SimReverseLimitRaw(rawHigh); // closed limit (IN2)
            else rig.Service.SimForwardLimitRaw(rawHigh);        // open limit (IN1)
        }

        Edge(true);                                     // release (NC: HIGH = released)
        rig.Time.Advance(TimeSpan.FromMilliseconds(20));
        Edge(false);                                    // chatter
        Edge(true);
        rig.Service.IsMoving.Should().BeTrue();

        rig.Time.Advance(Debounce);
        Edge(true);                                     // evaluated again after the window: verified
        Edge(false);                                    // genuine reassertion

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ZeroDebounce_ShouldVerifyReleaseOnFirstReading(bool opening)
    {
        using var rig = await StartDepartingAsync(opening, TimeSpan.Zero);

        rig.Set(MidTravel);
        rig.Cycle();
        rig.Set(AtLimit(open: !opening));
        rig.Cycle();

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OppositeLimitAssertingMidTravel_ShouldStopImmediately(bool opening)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.LimitSwitchDebounce = TimeSpan.FromMilliseconds(500));
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        (opening ? service.Open() : service.Close()).IsSuccessful.Should().BeTrue();

        // Started away from both limits: the limit behind the roof is verified released from the start.
        var (in1, in2) = AtLimit(open: !opening);
        hat.SetInputs(in1, in2, false, false);
        service.RunSupervisionCycle();

        service.IsMoving.Should().BeFalse();
        service.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReachingTheDestination_AfterDeparture_ShouldStopNormally(bool opening)
    {
        using var rig = await StartDepartingAsync(opening);

        rig.Set(MidTravel);
        rig.Cycle();
        rig.Time.Advance(Debounce);
        rig.Cycle();
        rig.Set(AtLimit(open: opening));
        rig.Cycle();

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        rig.Service.Status.Should().Be(opening ? RoofControllerStatus.Open : RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task DepartureLimitThatNeverReleases_IsCaughtByTheWatchdog()
    {
        using var rig = await StartDepartingAsync(opening: true);

        rig.Time.Advance(TimeSpan.FromSeconds(10));

        rig.Service.IsMoving.Should().BeFalse();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
    }
}
