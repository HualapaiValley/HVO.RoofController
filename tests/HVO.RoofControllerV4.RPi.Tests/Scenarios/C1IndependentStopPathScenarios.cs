using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C1: the drive stops without the controller. An external stop in the drive's STOP loop, the HAT losing power and the
/// hardwired limit contacts each stop the drive on their own, and the controller cannot restart it.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C1IndependentStopPathScenarios
{
    [TestMethod]
    [CommissioningCheck("C1", "2")]
    public async Task TheExternalStop_StopsTheDrive_AndNothingTheControllerCommandsRestartsIt()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.1, "the roof to travel");

        rig.Session.Plant.ExternalStopOpen = true;

        await rig.WaitForPlantAsync(p => p.OutputFrequencyHz == 0 && p.VelocityMetersPerSecond == 0, "the drive to stop on the external stop");
        var stopped = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the controller to see the drive stop");
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning, "the drive stopped running without the open limit");
        stopped.ShouldBeDeenergized(rig);
        var position = rig.Plant.PositionMeters;

        // The drive is not faulted, so the latch clears; the open that follows cannot start the drive.
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        await client.AcceptedAsync("Open");
        var refused = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the open");
        refused.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        refused.ShouldBeDeenergized(rig);
        rig.Plant.PositionMeters.Should().Be(position, "the drive does not run while the external stop is open");

        // Resetting the external stop does not restart the drive: the controller has removed the run command.
        rig.Session.Plant.ExternalStopOpen = false;
        await Task.Delay(TimeSpan.FromSeconds(1));
        rig.Plant.PositionMeters.Should().Be(position);
        rig.Plant.OutputFrequencyHz.Should().Be(0);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C1", "3")]
    public async Task HatPowerLoss_WhileOpening_DropsEveryRelay_AndStopsTheDrive()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.1, "the roof to travel");

        rig.Session.Plant.SetHatPower(false);

        var plant = await rig.WaitForPlantAsync(p => p.OutputFrequencyHz == 0 && p.VelocityMetersPerSecond == 0, "the drive to stop");
        plant.RelayContacts.Should().AllBeEquivalentTo(false, "the relays drop with the HAT, removing the run inputs and the STOP permit");
        plant.OpenLimitActuated.Should().BeFalse("the drive stopped before the open limit");
        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the controller to see the HAT fail");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);

        // With power back, the controller verifies the relays off; the fault waits for the operator.
        rig.Session.Plant.SetHatPower(true);
        var recovered = await rig.WaitForControllerAsync(
            s => s.InputsHealthy && s.RelayRegisterReadsHealthy && s.RelayRegisterState == RoofRelayRegisterState.Verified,
            "the controller to read the HAT again");
        recovered.IsFaultLatched.Should().BeTrue();
        recovered.ShouldBeDeenergized(rig);
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        await client.AcceptedAsync("Close");
        var closed = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, "the roof to close");
        closed.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C1", "4")]
    public async Task TheHardwiredLimitContacts_StopTheDrive_WithTheControllersLimitInputsDisconnected()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        rig.Session.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.BrokenMonitorWire);
        rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.BrokenMonitorWire);
        await rig.WaitForControllerAsync(s => s.IsClosedLimitActive == false, "the controller to lose the closed limit input");

        await client.AcceptedAsync("Open");

        // The open limit's NC contact in the forward run circuit stops the drive; the controller only sees the run loss.
        var open = await rig.WaitForPlantAsync(p => p.OpenLimitActuated && p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0, "the open limit's contact to stop the drive");
        open.PositionMeters.Should().BeLessThan(rig.Session.Plant.OpenHardStop, "the roof stops short of the hard stop");
        var afterOpen = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the controller to see the run loss");
        afterOpen.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        afterOpen.IsOpenLimitActive.Should().BeFalse("the monitoring wire is broken");
        afterOpen.ShouldBeDeenergized(rig);

        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        await client.AcceptedAsync("Close");

        var closed = await rig.WaitForPlantAsync(p => p.ClosedLimitActuated && p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0, "the closed limit's contact to stop the drive");
        closed.PositionMeters.Should().BeGreaterThan(rig.Session.Plant.ClosedHardStop);
        var afterClose = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the controller to see the run loss");
        afterClose.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        afterClose.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().BeEmpty("the hardwired contacts stop the drive before either hard stop");
    }
}
