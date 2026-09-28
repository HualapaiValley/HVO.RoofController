using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// The SMVector drive's faults, stops and settings, as the production controller sees them through the emulated plant.
/// Behaviour the drive manual does not settle is a named <see cref="SmVectorAssumptions"/> value, and tests that depend
/// on one run under each answer.
/// </summary>
[TestClass]
public class PlantDriveTests
{
    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DriveTrip_WhileOpening_LatchesDriveFault_AndClearFaultAllowsAnotherMove(bool restartAfterResetWithRunHeld)
    {
        // Whether a reset restarts a drive whose run input is still held is not stated; the controller never holds one
        // through Clear Fault, so the roof stays put either way.
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions
        {
            DriveAssumptions = new SmVectorAssumptions { RestartAfterResetWithRunHeld = restartAfterResetWithRunHeld }
        });
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        h.Plant.TripDrive();
        h.RunFor(TimeSpan.FromSeconds(1));

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        h.RelayRegister.Should().Be(0);
        h.Plant.Velocity.Should().Be(0);
        h.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        var position = h.Plant.Position;

        (await h.ClearFaultAsync()).IsSuccessful.Should().BeTrue("the Clear Fault pulse (RLY3, TB-13C) resets the drive");
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.RunFor(TimeSpan.FromSeconds(2));
        h.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        h.Plant.Position.Should().Be(position);

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DrivePowerLoss_LatchesDriveFault_UntilClearedAfterPowerReturns()
    {
        using var h = await PlantHarness.StartAsync();

        h.Plant.SetDrivePower(false);
        h.RunFor(TimeSpan.FromSeconds(1));
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault, "the unpowered fault relay reads as a trip");

        h.Plant.SetDrivePower(true);
        h.RunFor(TimeSpan.FromSeconds(3));
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault, "the latch holds until an operator clears it");

        (await h.ClearFaultAsync()).IsSuccessful.Should().BeTrue();
        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task StartWithinTwoSecondsOfPowerUp_TripsTheDrive_AndLatchesDriveFault()
    {
        using var h = await PlantHarness.StartAsync();
        h.Plant.SetDrivePower(false);
        h.RunFor(TimeSpan.FromSeconds(1));
        h.Plant.SetDrivePower(true);
        h.RunFor(TimeSpan.FromMilliseconds(100));
        (await h.ClearFaultAsync()).IsSuccessful.Should().BeTrue();

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();

        h.Plant.Drive.Trip.Should().Be(SmVectorTrip.StartTooSoonAfterPowerUp, "F_UF: a start within 2 s of power-up");
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        h.RelayRegister.Should().Be(0);

        h.RunFor(TimeSpan.FromSeconds(2));
        (await h.ClearFaultAsync()).IsSuccessful.Should().BeTrue();
        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClearFaultPulse_ShorterThanTheDriveNeeds_LeavesTheFaultLatched()
    {
        // Assumption: the manual gives no minimum reset pulse. If the drive needs 300 ms, the 250 ms default is not
        // enough and the operator must send a longer pulse; the controller reports that rather than clearing the latch.
        var plant = new RoofPlantOptions { DriveAssumptions = new SmVectorAssumptions { MinimumClearFaultPulse = TimeSpan.FromMilliseconds(300) } };
        using var h = await PlantHarness.StartAsync(plant);
        h.Plant.TripDrive();
        h.RunFor(TimeSpan.FromSeconds(1));

        var shortPulse = await h.ClearFaultAsync();

        shortPulse.IsSuccessful.Should().BeFalse();
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);

        (await h.ClearFaultAsync(500)).IsSuccessful.Should().BeTrue();
        h.Snapshot.LatchedFaultReason.Should().BeNull();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExternalStop_LongerThanTheRunLossWindow_LatchesDriveNotRunning(bool restartWhenStopReleased)
    {
        using var h = await PlantHarness.StartAsync(ExternalStopPlant(restartWhenStopReleased));
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        h.Plant.ExternalStopOpen = true;
        var stopped = h.Elapsed;
        h.RunUntil(() => h.Snapshot.LatchedFaultReason is not null, TimeSpan.FromSeconds(1)).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        ShouldEnforceTheRunLossWindowFromTheDrop(h, stopped);
        h.RelayRegister.Should().Be(0);

        var position = h.Plant.Position;
        h.RunFor(TimeSpan.FromMilliseconds(150));
        h.Plant.ExternalStopOpen = false;
        h.RunFor(TimeSpan.FromSeconds(2));

        h.Plant.Position.Should().BeApproximately(position, 0.001, "the run command was removed before the stop was released");
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExternalStop_ShorterThanTheRunLossWindow_ResumesWhenTheDriveRestarts()
    {
        // By design: IN4 returning inside the 250 ms window cancels it. With the restart assumption the drive resumes on
        // its own once STOP is released with the run input held, and the move completes normally.
        using var h = await PlantHarness.StartAsync(ExternalStopPlant(restartWhenStopReleased: true));
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        h.Plant.ExternalStopOpen = true;
        h.RunFor(TimeSpan.FromMilliseconds(100));
        h.Plant.ExternalStopOpen = false;

        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExternalStop_ShorterThanTheRunLossWindow_WithoutRestart_LatchesDriveNotRunning()
    {
        using var h = await PlantHarness.StartAsync(ExternalStopPlant(restartWhenStopReleased: false));
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));

        h.Plant.ExternalStopOpen = true;
        var stopped = h.Elapsed;
        h.RunFor(TimeSpan.FromMilliseconds(100));
        h.Plant.ExternalStopOpen = false;

        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        ShouldEnforceTheRunLossWindowFromTheDrop(h, stopped);
        h.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// The controller releases RLY1 when the 250 ms run-loss window that began when IN4 dropped ends: it sees the drop at
    /// its next poll (the 25 ms interval plus a read) and reads the inputs once more before it writes the relay.
    /// </summary>
    private static void ShouldEnforceTheRunLossWindowFromTheDrop(PlantHarness h, TimeSpan stopped)
    {
        var window = RoofControllerServiceV4.RunLossConfirmationDelay;
        var transaction = TimeSpan.FromMilliseconds(16);
        var runLost = h.EventAt("IN4 LOW", stopped);

        (h.CoilOffAt(1, stopped) - runLost).Should().BeGreaterThanOrEqualTo(window)
            .And.BeLessThanOrEqualTo(window + h.Options.DigitalInputPollInterval + transaction * 2 + TimeSpan.FromMilliseconds(5),
                "the window starts when IN4 drops, not when STOP is released");
    }

    [TestMethod]
    public async Task RampStop_WithTheTwoSecondDeceleration_RunsIntoTheHardStop()
    {
        // Documented hazard: P111 = ramp with P105 = 2 s needs about 0.1 m to stop from 0.1 m/s, more than the 0.06 m
        // between each limit's operating point and its hard stop. The controller is not at fault; the setting is.
        var plant = new RoofPlantOptions { Drive = new SmVectorSettings { StopMethod = SmVectorStopMethod.Ramp } };
        using var h = await PlantHarness.StartAsync(plant);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().ContainSingle().Which.Kind.Should().Be(PlantViolationKind.HardStopContact);
    }

    [TestMethod]
    [DataRow(SmVectorStopMethod.Ramp, 300)]
    [DataRow(SmVectorStopMethod.CoastWithDcBrake, 500)]
    public async Task ShortRampOrDcBrake_StopsClearOfTheHardStops(SmVectorStopMethod method, int milliseconds)
    {
        var drive = method == SmVectorStopMethod.Ramp
            ? new SmVectorSettings { StopMethod = method, DecelerationTime = TimeSpan.FromMilliseconds(milliseconds) }
            : new SmVectorSettings { StopMethod = method, DcBrakeTime = TimeSpan.FromMilliseconds(milliseconds) };
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Drive = drive });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Plant.MaximumOpenOvertravel.Should().BeLessThan(0.03);
        h.Plant.MaximumClosedOvertravel.Should().BeLessThan(0.03);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SlowAcceleration_StillCompletesACycle()
    {
        var plant = new RoofPlantOptions { Drive = new SmVectorSettings { AccelerationTime = TimeSpan.FromSeconds(20) } };
        using var h = await PlantHarness.StartAsync(plant);

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RunOutputOffDuringDeceleration_ReversalDuringTheRamp_StillCompletesSafely()
    {
        // Assumption: whether the run output (P142 = 1) stays on during a ramp stop is not stated. If it drops, the
        // IN4 interlock lets a reversal start during the ramp; the drive ramps down and then reverses.
        var plant = new RoofPlantOptions
        {
            Drive = new SmVectorSettings { StopMethod = SmVectorStopMethod.Ramp, DecelerationTime = TimeSpan.FromMilliseconds(300) },
            DriveAssumptions = new SmVectorAssumptions { RunOutputDuringDeceleration = false }
        };
        using var h = await PlantHarness.StartAsync(plant);
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));
        h.Stop();
        h.RunFor(TimeSpan.FromMilliseconds(60));

        h.Close().IsSuccessful.Should().BeTrue();

        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Closed);
        h.Violations.Should().BeEmpty();
    }

    private static RoofPlantOptions ExternalStopPlant(bool restartWhenStopReleased)
        => new() { DriveAssumptions = new SmVectorAssumptions { RestartWhenStopReleasedWithRunHeld = restartWhenStopReleased } };
}
