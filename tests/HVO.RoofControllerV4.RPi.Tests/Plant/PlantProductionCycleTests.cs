using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// The production controller and configuration running the emulated roof (<see cref="PlantHarness"/>) through normal
/// operation: full moves, stops, reversals, the watchdog and the operator lease.
/// </summary>
[TestClass]
public class PlantProductionCycleTests
{
    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    public async Task OpenAndClose_StopOnTheLimits_WellShortOfTheHardStops()
    {
        using var h = await PlantHarness.StartAsync();
        h.Status.Should().Be(RoofControllerStatus.Closed);

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.Status.Should().Be(RoofControllerStatus.Open);
        h.Snapshot.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        h.Plant.Elapsed.TotalSeconds.Should().BeApproximately(21.3, 0.5, "2 m at 0.1 m/s after a 2 s ramp");
        h.Plant.MaximumOpenOvertravel.Should().BeLessThan(0.02, "a coast stop from 0.1 m/s runs about 10 mm past the operating point");

        h.Close().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.Status.Should().Be(RoofControllerStatus.Closed);
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Plant.MaximumClosedOvertravel.Should().BeLessThan(0.02);
        h.RelayRegister.Should().Be(0, "RLY4 (the STOP permit) is energized only while moving");
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Stop_MidTravel_DropsEveryRelay_AndTheRoofComesToRest()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        h.Stop().IsSuccessful.Should().BeTrue();

        h.RelayRegister.Should().Be(0);
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();
        h.Snapshot.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        h.Snapshot.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Plant.Position.Should().BeInRange(0.3, 0.5);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Reversal_WhileTheDriveRuns_StopsTheRoof_AndRefusesTheNewDirectionUntilIN4Drops()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        var reverse = h.Close();

        reverse.ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);
        h.Controller.IsMoving.Should().BeFalse();
        h.RelayRegister.Should().Be(0);
        h.Snapshot.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);

        h.RunUntil(() => !h.DriveRunOutput, TimeSpan.FromSeconds(1)).Should().BeTrue("a coast stop removes the output at once");
        h.RunFor(h.Options.DigitalInputPollInterval * 2);

        h.Close().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Reversal_DuringARampStop_IsRefusedWhileTheDriveDecelerates()
    {
        var plant = new RoofPlantOptions
        {
            Drive = new SmVectorSettings { StopMethod = SmVectorStopMethod.Ramp, DecelerationTime = TimeSpan.FromMilliseconds(500) }
        };
        using var h = await PlantHarness.StartAsync(plant);
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));
        h.Stop();
        h.RunFor(TimeSpan.FromMilliseconds(100));

        h.Plant.Drive.Mode.Should().Be(SmVectorMode.Decelerating);
        h.Close().ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive, "the run output (P142 = 1) stays on during the ramp");

        h.RunFor(TimeSpan.FromSeconds(1));
        h.Close().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SafetyWatchdog_StopsAMoveThatRunsTooLong_AndLatches()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10));
        h.Open();

        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        h.Plant.Elapsed.TotalSeconds.Should().BeApproximately(10, 0.5);
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OperatorLease_StopsTheRoofWhenItIsNotRenewed_WithoutLatching()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.OperatorLeaseTimeout = TimeSpan.FromSeconds(5));
        h.Open();

        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Plant.Elapsed.TotalSeconds.Should().BeApproximately(5, 0.5);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OperatorLease_RenewedThroughTheMove_LetsTheRoofReachTheLimit()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.OperatorLeaseTimeout = TimeSpan.FromSeconds(5));
        h.Open();

        for (var i = 0; i < 20 && h.Controller.IsMoving; i++)
        {
            h.RunFor(TimeSpan.FromSeconds(2));
            if (h.Controller.IsMoving)
            {
                h.RenewLease().IsSuccessful.Should().BeTrue();
            }
        }

        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }
}
