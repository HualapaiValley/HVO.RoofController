using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C4: the drive fault input (IN3) on the fail-safe wiring (P140 = 3: TB-16/17 closed while the drive is healthy), with
/// the production setting (<c>FaultInputActiveHigh = false</c>) and with the code default.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C4FaultInputPolarityScenarios
{
    [TestMethod]
    [CommissioningCheck("C4", "1")]
    [CommissioningCheck("C4", "2")]
    [CommissioningCheck("C4", "3")]
    public async Task AHealthyDrive_ReadsHealthy_AndATripStopsTheMove_WithDriveFault()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        (await client.StatusAsync()).IsDriveFaultActive.Should().BeFalse();

        (await client.AcceptedAsync("Open")).CommandedMotion.Should().Be(RoofMotionDirection.Opening);
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        rig.Session.Plant.TripDrive(SmVectorTrip.External);

        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the trip to stop the open");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        stopped.IsDriveFaultActive.Should().BeTrue();
        stopped.IsFaultLatched.Should().BeTrue();
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        stopped.ShouldBeDeenergized(rig);
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.External, "only RLY3 resets the drive (C5)");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C4", "1")]
    public async Task TheCodeDefault_OnTheFailSafeWiring_LatchesDriveFaultAtStartup()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            settings: new Dictionary<string, string?> { ["RoofControllerOptionsV4:FaultInputActiveHigh"] = "true" }) with { InitialStatus = null });
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        var status = await client.StatusAsync();
        status.IsDriveFaultActive.Should().BeTrue("a healthy drive holds IN3 HIGH, which this setting reads as a fault");
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.None);
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive));
    }

    [TestMethod]
    [CommissioningCheck("C4", "4")]
    [DataRow("a broken IN3 wire")]
    [DataRow("drive power off")]
    public async Task WithTheFailSafeWiring_FailsSafe_AsAFault(string failure)
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        var plant = rig.Session.Plant;
        Action<bool> apply = failure == "drive power off"
            ? on => plant.SetDrivePower(!on)
            : on => plant.Wiring = on ? WiringFault.FaultMonitorWireBroken : WiringFault.None;

        apply(true);

        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched, $"{failure} to read as a fault");
        latched.IsDriveFaultActive.Should().BeTrue();
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        (await client.RefusedAsync("Open")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive));

        // C4 step 5: repaired, the latch waits for the operator.
        apply(false);
        await rig.WaitForControllerAsync(s => s.IsDriveFaultActive == false, "IN3 to read healthy");
        (await client.StatusAsync()).IsFaultLatched.Should().BeTrue();
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        plant.Violations.Should().BeEmpty();
    }
}
