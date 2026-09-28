using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C6: the drive-running input (IN4, from TB-14 with P142 = 1, Run) and the production
/// <c>AtSpeedConfirmationTimeout</c> of 3 s: the start confirmation, the loss of the run input, the stop confirmation
/// and the start interlock.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C6DriveRunningInputScenarios
{
    private static readonly TimeSpan AtSpeedWindow = TimeSpan.FromSeconds(3);

    [TestMethod]
    [CommissioningCheck("C6", "1")]
    public async Task TheRunInput_ConfirmsEveryStart_WellInsideTheWindow_TenTimesEachWay()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(timeScale: 2));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
            await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        }

        // From each direction contact closing to the drive's run output reaching IN4.
        var starts = rig.ContactChanges(1).Concat(rig.ContactChanges(2)).Where(c => c.Closed).Select(c => c.At).Order().ToArray();
        var runs = rig.EventTimes(PlantEventKind.Input, "IN4 HIGH");
        starts.Should().HaveCount(20);
        runs.Should().HaveCount(20, "the drive ran once per move");
        var confirmations = starts.Select(start => runs.First(run => run >= start) - start).ToArray();
        confirmations.Should().AllSatisfy(delay => delay.Should().BeLessThan(TimeSpan.FromMilliseconds(250), "P142 = 1 turns TB-14 on as the drive starts"));
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6", "2")]
    public async Task ABrokenRunInputWire_BeforeTheStart_StopsTheMove_WithDriveNotRunning_AtTheWindow()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        rig.Session.Plant.Wiring = WiringFault.RunMonitorWireBroken;

        await client.AcceptedAsync("Open");

        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the open");
        latched.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        latched.ShouldBeDeenergized(rig);
        var forward = rig.ContactChanges(1);
        (forward.Last(c => !c.Closed).At - forward.First(c => c.Closed).At).TotalSeconds.Should().BeApproximately(AtSpeedWindow.TotalSeconds, 0.3,
            "the drive ran, but IN4 never confirmed it");
        rig.Plant.PositionMeters.Should().BeGreaterThan(0, "the drive ran for the window");
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6")]
    public async Task ABrokenRunInputWire_DuringTravel_StopsTheMove_WithDriveNotRunning_After250Ms()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        rig.Session.Plant.Wiring = WiringFault.RunMonitorWireBroken;

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the lost run input to stop the open");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        stopped.ShouldBeDeenergized(rig);
        var lost = rig.EventTimes(PlantEventKind.Input, "IN4 LOW").Single();
        var released = rig.ContactChanges(1).Last(c => !c.Closed).At;
        (released - lost).TotalMilliseconds.Should().BeInRange(250, 600, "the controller waits 250 ms for IN4 to return, then stops");
        rig.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Drive run input (IN4) dropped while Opening", StringComparison.Ordinal));
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6", "3")]
    public async Task AfterACoastStop_TheRunInputDropsAtOnce_AndAReversalProceedsAtOnce()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08 && p.VelocityMetersPerSecond > 0.09, "the roof to reach speed");

        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
        var stoppedAt = rig.ContactChanges(1).Last(c => !c.Closed).At;
        (rig.EventTimes(PlantEventKind.Input, "IN4 LOW").Single() - stoppedAt).Should().BeLessThan(TimeSpan.FromMilliseconds(50), "P111 = 0 (coast) shuts the output off at once");

        (await client.AcceptedAsync("Close")).CommandedMotion.Should().Be(RoofMotionDirection.Closing);
        (rig.ContactChanges(2).Single(c => c.Closed).At - stoppedAt).Should().BeLessThan(TimeSpan.FromMilliseconds(250),
            "nothing holds IN4 after a coast stop, so the interlock lets the reversal through without waiting");
        (await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, "the roof to close"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        await rig.WaitForRestAsync("the close");
        rig.Logs.Entries.Should().NotContain(e => e.Level == LogLevel.Critical);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6")]
    public async Task AfterARampStop_AReversalIsRefused_UntilTheRunInputDrops()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: Scenario.DriveSettings(d => d with { StopMethod = SmVectorStopMethod.Ramp, DecelerationTime = TimeSpan.FromSeconds(1) })));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08 && p.VelocityMetersPerSecond > 0.09, "the roof to reach speed");

        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);

        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive),
            "the run output (P142 = 1) stays on while the drive ramps down");
        rig.Plant.DriveMode.Should().Be(SmVectorMode.Decelerating);
        await rig.WaitForPlantAsync(p => (p.InputBits & 0b1000) == 0, "IN4 to drop at the end of the ramp", TimeSpan.FromSeconds(3));
        await client.AcceptedAsync("Close");
        (await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, "the roof to close"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Logs.Entries.Should().NotContain(e => e.Level == LogLevel.Critical, "the ramp (1 s) ends inside the stop window (3 s)");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6", "3")]
    public async Task AContinuousDcBrake_ThatKeepsTheRunOutputOn_IsLoggedCriticalOnce_AndRefusesEveryMove()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: p => p with
            {
                Drive = p.Drive with { StopMethod = SmVectorStopMethod.CoastWithDcBrake, DcBrakeTime = SmVectorSettings.ContinuousDcBrake },
                DriveAssumptions = p.DriveAssumptions with { RunOutputDuringDcBrake = true }
            }));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);

        await Task.Delay(AtSpeedWindow + TimeSpan.FromSeconds(1));
        rig.Plant.DriveMode.Should().Be(SmVectorMode.DcBraking);
        rig.Plant.VelocityMetersPerSecond.Should().Be(0);
        rig.Logs.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
            .Which.Message.Should().Contain("Drive still reports running (IN4)").And.Contain("NormalStop");
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive),
            "P175 = 999.9 keeps IN4 HIGH until the next run");
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive));
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C6")]
    public async Task TheAtSpeedOutput_WithATwentySecondAcceleration_OutlastsTheWindow_AndLatchesDriveNotRunning()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: Scenario.DriveSettings(d => d with { Tb14Output = SmVectorOutputFunction.AtSpeed, AccelerationTime = TimeSpan.FromSeconds(20) })));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await client.AcceptedAsync("Open");

        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the open");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning, "P142 = 6 needs a window longer than P104");
        rig.EventTimes(PlantEventKind.Input, "IN4 HIGH").Should().BeEmpty("the drive never reached speed");
        await rig.WaitForRestAsync("the stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
