using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.LimitSwitches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// The ME-8108 limit switches: contact behaviour the datasheet leaves open, switch and wire faults, and a jammed roof.
/// </summary>
[TestClass]
public class PlantLimitSwitchTests
{
    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    public async Task SlowActionContacts_StillStopOnTheLimits()
    {
        // Assumption: the ME-8108 is sold with snap-action contacts; slow-action ones leave a gap with neither pair
        // closed and have no differential travel.
        var slowAction = new Me8108Options { ContactAction = Me8108ContactAction.SlowAction };
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { OpenLimit = slowAction, ClosedLimit = slowAction });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(20, 100)]
    public async Task ContactBounceAndTransferTime_StillStopOnTheLimits(int bounceMilliseconds, int transferMilliseconds)
    {
        // Assumption: the datasheet gives neither bounce nor the time between the NC pair opening and the NO pair closing
        // (defaults 3 ms and 5 ms). The NC pair removes the run input first, so IN4 drops before the limit input rises;
        // the 250 ms run-loss window covers the transfer.
        var limit = new Me8108Options
        {
            BounceTime = TimeSpan.FromMilliseconds(bounceMilliseconds),
            TransferTime = TimeSpan.FromMilliseconds(transferMilliseconds)
        };
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { OpenLimit = limit, ClosedLimit = limit });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Closed);

        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SlowDriveInputResponse_StillStopsShortOfTheHardStop()
    {
        // Assumption: the drive responds to its terminals within 4 ms. At 100 ms the roof runs 10 mm further.
        var plant = new RoofPlantOptions { DriveAssumptions = new SmVectorAssumptions { InputResponseTime = TimeSpan.FromMilliseconds(100) } };
        using var h = await PlantHarness.StartAsync(plant);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Plant.MaximumOpenOvertravel.Should().BeLessThan(0.03);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OpenLimitStuckReleased_ReachesTheHardStop_AndOnlyTheStallTripStopsIt()
    {
        // Known limitation: a switch that never operates removes neither the run input nor the monitor signal, so
        // nothing the controller reads changes before the hard stop. A travel-time or position check would catch it.
        using var h = await PlantHarness.StartAsync();
        h.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.StuckReleased);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Violations.Should().ContainSingle().Which.Kind.Should().Be(PlantViolationKind.HardStopContact);
        h.Plant.Drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task ClosedLimitStuckActuated_IsCaughtAtTheOpenLimit_AsContradictoryInputs()
    {
        using var h = await PlantHarness.StartAsync();
        h.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClosedLimitStuckActuated_WithADepartureReleaseTimeout_IsCaughtLeavingTheLimit()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.DepartureReleaseTimeout = TimeSpan.FromSeconds(1.2));
        h.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        h.Plant.Position.Should().BeLessThan(0.1);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OpenLimitMonitorWireBroken_TheHardwiredStopHolds_AndRunLossLatchesDriveNotRunning()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { InitialPosition = 1.0 });
        h.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.BrokenMonitorWire);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning,
            "the NC pair stopped the drive at the limit, but IN1 never reported it");
        h.Plant.MaximumOpenOvertravel.Should().BeLessThan(0.02);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClosedLimitNcWireBroken_TheDriveRefusesToClose_AndDriveNotRunningLatches()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { InitialPosition = 1.0 });
        h.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.BrokenNcWire);

        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        h.Plant.Position.Should().Be(1.0);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task JammedRoof_TripsTheStallProtection_AndLatchesDriveFault()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { InitialPosition = 1.0 });
        h.Plant.Jammed = true;

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Plant.Drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        h.Plant.Position.Should().Be(1.0);
    }
}
