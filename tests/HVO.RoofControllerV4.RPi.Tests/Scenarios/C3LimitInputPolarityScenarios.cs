using System.Collections.Generic;
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
/// C3: the limit inputs (IN1 open, IN2 closed) on the documented wiring, the ME-8108 normally open pairs, with the
/// production setting and with the two mistakes the emulated plant can show: the normally closed setting, and the input
/// commons on TB-4.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C3LimitInputPolarityScenarios
{
    [TestMethod]
    [CommissioningCheck("C3", "1")]
    [CommissioningCheck("C3", "2")]
    public async Task EachLimitInput_IsActiveOnlyWhileItsSwitchIsActuated()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        ShouldReadTheLimits(await client.StatusAsync(), open: false, closed: true);
        rig.Plant.ClosedLimitActuated.Should().BeTrue();

        // Departure: IN2 follows the closed limit's lever as it releases.
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => !p.ClosedLimitActuated, "the closed limit to release");
        await rig.WaitForControllerAsync(s => s.IsClosedLimitActive == false, "IN2 to follow the switch");
        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the stop");
        ShouldReadTheLimits(await client.StatusAsync(), open: false, closed: false);

        // Each switch actuated by hand, with the roof between the limits.
        foreach (var openLimit in new[] { true, false })
        {
            rig.Session.Plant.SetLimitFault(openLimit, LimitSwitchFault.StuckActuated);
            ShouldReadTheLimits(await rig.WaitForControllerAsync(s => (openLimit ? s.IsOpenLimitActive : s.IsClosedLimitActive) == true, "the actuated switch to read active"),
                open: openLimit, closed: !openLimit);
            rig.Session.Plant.SetLimitFault(openLimit, LimitSwitchFault.None);
            ShouldReadTheLimits(await rig.WaitForControllerAsync(s => (openLimit ? s.IsOpenLimitActive : s.IsClosedLimitActive) == false, "the released switch to read inactive"),
                open: false, closed: false);
        }

        // Arrival: IN1 follows the open limit's lever.
        await client.AcceptedAsync("Open");
        var open = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, "the roof to open");
        ShouldReadTheLimits(open, open: true, closed: false);
        rig.Plant.OpenLimitActuated.Should().BeTrue();
        open.IsFaultLatched.Should().BeFalse();
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C3", "3")]
    public async Task BothLimitsActuated_WhileMoving_StopsWithContradictoryLimitInputs_LatchesIt_AndRefusesMotion()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        // Both in one HAT poll sample, as a wiring fault that feeds both inputs would.
        var plant = rig.Session.Plant;
        lock (plant.SyncRoot)
        {
            plant.SetLimitFault(openLimit: true, LimitSwitchFault.StuckActuated);
            plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);
        }

        // The HAT library raises one edge event per input, IN1 first, and the controller evaluates each on its own, so
        // the open limit's edge stops the open as the destination limit before IN2's edge is seen. IN2's edge, with the
        // roof stopped, then latches the contradiction. The stop reason names the open limit; the latch names the fault.
        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.IsFaultLatched, "the contradictory limits to stop the open and latch");
        stopped.LastStopReason.Should().BeOneOf(RoofControllerStopReason.ContradictoryLimitInputs, RoofControllerStopReason.LimitSwitchReached);
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        stopped.ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the stop");
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive),
            "both limits still read active");

        // Released, the latch still holds until the operator clears it.
        lock (plant.SyncRoot)
        {
            plant.SetLimitFault(openLimit: true, LimitSwitchFault.None);
            plant.SetLimitFault(openLimit: false, LimitSwitchFault.None);
        }

        await rig.WaitForControllerAsync(s => s.IsOpenLimitActive == false && s.IsClosedLimitActive == false, "the released limits to read inactive");
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        await client.AcceptedAsync("Close");
        (await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, "the roof to close"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C3", "4")]
    public async Task ABrokenMonitorWire_ReadsNotAtTheLimit()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        // The known limitation: a normally open monitoring pair on a broken wire looks like a switch at rest.
        rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.BrokenMonitorWire);
        var broken = await rig.WaitForControllerAsync(s => s.IsClosedLimitActive == false, "IN2 to lose the closed limit");
        rig.Plant.ClosedLimitActuated.Should().BeTrue("the roof has not moved");
        broken.Status.Should().NotBe(RoofControllerStatus.Closed);
        broken.IsFaultLatched.Should().BeFalse("nothing distinguishes the broken wire from a released switch");
        rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.None);
        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed, "the repaired wire to read the closed limit");

        await client.AcceptedAsync("Open");
        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, "the roof to open");
        rig.Session.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.BrokenMonitorWire);
        await rig.WaitForControllerAsync(s => s.IsOpenLimitActive == false, "IN1 to lose the open limit");
        rig.Plant.OpenLimitActuated.Should().BeTrue();
        rig.Session.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.None);
        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open, "the repaired wire to read the open limit");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C3", "1")]
    public async Task TheNormallyClosedSetting_OnTheNormallyOpenWiring_ReadsTheLimitsInverted_AndTheRoofNeverMoves()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            settings: new Dictionary<string, string?> { ["RoofControllerOptionsV4:UseNormallyClosedLimitSwitches"] = "true" }) with { InitialStatus = null });
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        var status = await client.StatusAsync();
        status.Status.Should().Be(RoofControllerStatus.Open, "the closed roof reads as open");
        ShouldReadTheLimits(status, open: true, closed: false);

        await client.AcceptedAsync("Close");
        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the close");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning,
            "the closed limit's NC pair holds the reverse run input open, so the drive never starts");
        latched.ShouldBeDeenergized(rig);
        rig.Plant.PositionMeters.Should().Be(-0.01);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C3", "1")]
    public async Task InputCommonsOnTb4_ReadTheDriveAsFaulted_AndNothingMoves()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: p => p with { Wiring = WiringFault.InputCommonsOnTb4 }) with { InitialStatus = null });
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        var status = await client.StatusAsync();
        status.IsDriveFaultActive.Should().BeTrue("with the commons on +15 V, IN3 never conducts");
        status.IsFaultLatched.Should().BeTrue();
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        ShouldReadTheLimits(status, open: false, closed: false);
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive));
        rig.Plant.PositionMeters.Should().Be(-0.01);
        rig.Plant.RelayRegister.Should().Be(0);
    }

    private static void ShouldReadTheLimits(RoofStatusResponse status, bool open, bool closed)
    {
        status.InputsHealthy.Should().BeTrue();
        status.IsOpenLimitActive.Should().Be(open, "IN1 reads the open limit");
        status.IsClosedLimitActive.Should().Be(closed, "IN2 reads the closed limit");
    }
}
