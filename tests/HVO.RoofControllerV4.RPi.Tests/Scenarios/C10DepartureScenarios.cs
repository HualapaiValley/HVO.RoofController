using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C10: a move that starts at a limit, whose lever must release as the roof departs: from both limits, a start limit
/// that is actuated again, one that never releases, and the <c>DepartureReleaseTimeout</c> window against swapped
/// motor leads.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C10DepartureScenarios
{
    [TestMethod]
    [CommissioningCheck("C10", "1")]
    [CommissioningCheck("C10", "2")]
    public async Task FromEitherLimit_TheStartLimitReleases_WithoutAFalseStop_AndTheRoofStopsAtTheOther()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);

        // Each start limit's normally open pair breaks once as the roof departs. (The arriving limit's pair bounces as it
        // makes, which is not a release.)
        var opened = rig.ContactChanges(1).Single(c => c.Closed).At;
        var closed = rig.ContactChanges(2).Single(c => c.Closed).At;
        rig.EventTimes(PlantEventKind.Input, "IN2 LOW").Where(t => t > opened && t < closed).Should().ContainSingle("the closed limit released once, on departure");
        rig.EventTimes(PlantEventKind.Input, "IN1 LOW").Where(t => t > closed).Should().ContainSingle("the open limit released once, on departure");

        rig.Logs.Entries.Should().NotContain(e => e.Message.Contains(nameof(RoofControllerStopReason.StartLimitReasserted), StringComparison.Ordinal));
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C10", "3")]
    [DataRow(RoofControllerStatus.Closed)]
    [DataRow(RoofControllerStatus.Open)]
    public async Task TheStartLimit_ActuatedAgainAfterItReleased_StopsTheMove_WithStartLimitReasserted(RoofControllerStatus start)
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        if (start == RoofControllerStatus.Open)
        {
            await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        }

        var fromClosed = start == RoofControllerStatus.Closed;
        await client.AcceptedAsync(fromClosed ? "Open" : "Close");
        await rig.WaitForControllerAsync(s => (fromClosed ? s.IsClosedLimitActive : s.IsOpenLimitActive) == false, "the start limit to release");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        rig.Session.Plant.SetLimitFault(openLimit: !fromClosed, LimitSwitchFault.StuckActuated);

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the reasserted start limit to stop the move");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        stopped.ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.SetLimitFault(openLimit: !fromClosed, LimitSwitchFault.None);
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C10", "4")]
    public async Task AStartLimitThatNeverReleases_WithTheDepartureTimeout_StopsWithDepartureLimitNotReleased()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            settings: new Dictionary<string, string?> { ["RoofControllerOptionsV4:DepartureReleaseTimeout"] = "00:00:02" }));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

        await client.AcceptedAsync("Open");

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the departure timeout to stop the open");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        stopped.ShouldBeDeenergized(rig);
        var forward = rig.ContactChanges(1);
        (forward.Last().At - forward.First().At).TotalSeconds.Should().BeApproximately(2, 0.3);
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C10", "4")]
    public async Task AStartLimitThatNeverReleases_WithoutTheDepartureTimeout_RunsUntilTheWatchdog()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await rig.ConfigureAsync(r => r with { SafetyWatchdogTimeoutSeconds = 5 });
        rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

        await client.AcceptedAsync("Open");

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the watchdog to stop the open");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout, "production has no departure timeout; the watchdog is the backstop");
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.PositionMeters.Should().BeGreaterThan(0.2, "the roof travelled with the closed limit still reading active");
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C10")]
    public async Task SwappedMotorLeads_FromTheClosedLimit_TheDepartureTimeoutStopsTheRoof_BeforeTheHardStop()
    {
        // The window the procedure gives: longer than the release (about 1.0 s at P104 = 2 s, plus the 25 ms debounce),
        // shorter than a wrong-way move takes to reach the stop behind the limit (about 1.5 s).
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: p => p with { Wiring = WiringFault.SwappedMotorLeads },
            settings: new Dictionary<string, string?> { ["RoofControllerOptionsV4:DepartureReleaseTimeout"] = "00:00:01.300" }));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await client.AcceptedAsync("Open");

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the departure timeout to stop the wrong-way move");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        await rig.WaitForRestAsync("the stop");
        rig.Plant.PositionMeters.Should().BeLessThan(-0.01, "the roof moved the wrong way");
        rig.Plant.PositionMeters.Should().BeGreaterThan(rig.Session.Plant.ClosedHardStop, "the roof stopped short of the hard stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C10")]
    public async Task SwappedMotorLeads_FromTheClosedLimit_WithoutTheDepartureTimeout_ReachTheHardStop()
    {
        // The known limitation without DepartureReleaseTimeout: the roof drives into the stop behind the closed limit
        // until the stall trip (IN3) stops it.
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(plant: p => p with { Wiring = WiringFault.SwappedMotorLeads }));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await client.AcceptedAsync("Open");

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.IsFaultLatched, "the stall trip to stop the wrong-way move");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        stopped.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().Contain(v => v.Kind == PlantViolationKind.HardStopContact);
        rig.Session.Plant.Violations.Should().NotContain(v => v.Kind == PlantViolationKind.BothDirectionContactsClosed);
    }
}
