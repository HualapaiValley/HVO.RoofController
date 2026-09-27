using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// Faults in the SM-I-010 HAT, its relays and the I2C bus, with the production controller and the real HAT library
/// driving the emulated plant.
/// </summary>
[TestClass]
public class PlantHatTests
{
    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    public async Task HatPowerLoss_WhileOpening_StopsTheRoof_LatchesRelayVerificationFailed_AndRestoresTheLedModes()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(5));
        h.Plant.Hat.LedModeBits.Should().Be(0x07, "LED1-3 show the controller's indicators");

        h.Plant.SetHatPower(false);
        h.RunFor(TimeSpan.FromMilliseconds(50));
        h.Plant.Drive.IsDriving.Should().BeFalse("the relays drop with the HAT, removing the run inputs and the STOP permit");

        h.RunFor(TimeSpan.FromSeconds(2));
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        h.Controller.IsMoving.Should().BeFalse();

        h.Plant.SetHatPower(true);
        h.RunFor(TimeSpan.FromSeconds(3));
        h.Plant.Hat.LedModeBits.Should().Be(0x07, "the HAT powers up with every LED following its input, and the controller re-applies its modes");
        h.RelayRegister.Should().Be(0);

        (await h.ClearFaultAsync()).IsSuccessful.Should().BeTrue();
        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DeadForwardRelay_TheDriveNeverStarts_AndDriveNotRunningLatches()
    {
        using var h = await PlantHarness.StartAsync();
        h.Plant.SetRelayFault(1, RelayContactFault.Dead);

        h.Open().IsSuccessful.Should().BeTrue("the register verifies; only the contact is dead");
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        h.Plant.Elapsed.TotalSeconds.Should().BeApproximately(3, 0.1, "AtSpeedConfirmationTimeout is 3 s");
        h.Plant.Position.Should().Be(-0.01);
        h.RelayRegister.Should().Be(0);
    }

    [TestMethod]
    public async Task DeadStopPermitRelay_TheDriveNeverStarts_AndDriveNotRunningLatches()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { InitialPosition = 1.0 });
        h.Plant.SetRelayFault(4, RelayContactFault.Dead);

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        h.Plant.Position.Should().Be(1.0);
    }

    [TestMethod]
    public async Task WeldedForwardRelay_TheStopPermitStillStopsTheRoof_AndTheDriveRefusesBothDirections()
    {
        // Known limitation: the relay register cannot show a welded contact. RLY4 (the STOP permit) still stops the
        // roof. A reversal then closes both direction contacts, which the drive refuses, so the roof does not move and
        // the at-speed check latches DriveNotRunning.
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(3));
        h.Plant.SetRelayFault(1, RelayContactFault.Welded);

        h.Stop().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();
        var position = h.Plant.Position;

        h.Close().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        h.Plant.Position.Should().Be(position);
        h.Violations.Should().ContainSingle().Which.Kind.Should().Be(PlantViolationKind.BothDirectionContactsClosed);
    }

    [TestMethod]
    public async Task ClearFaultRelayStuckOn_AbortsTheOpen()
    {
        using var h = await PlantHarness.StartAsync();
        h.Plant.Hat.StuckOnRelayBits = 0x04;

        var open = h.Open();

        open.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        h.RunFor(TimeSpan.FromSeconds(1));
        h.Plant.Position.Should().Be(-0.01);
    }

    [TestMethod]
    public async Task RelayRegisterChangedWhileMoving_LatchesRelayVerificationFailed_AndStops()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(3));

        lock (h.Plant.SyncRoot)
        {
            h.Plant.Hat.WriteRegister(SmI010Board.RelayValueRegister, 0x0D);
        }

        h.RunUntilStopped(TimeSpan.FromSeconds(3)).Should().BeTrue();
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OneFailedRelayWrite_IsRetried_AndTheRoofOpens()
    {
        using var h = await PlantHarness.StartAsync();
        h.Bus.FailNextWrites = 1;

        h.Open().IsSuccessful.Should().BeTrue();

        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IgnoredRelayWrites_AbortTheOpen()
    {
        using var h = await PlantHarness.StartAsync();
        h.Plant.Hat.IgnoreRelayWrites = true;

        h.Open().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        h.RunFor(TimeSpan.FromSeconds(1));
        h.Plant.Position.Should().Be(-0.01);
    }

    [TestMethod]
    public async Task TransientInputReadFailures_AreTolerated()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(2));

        h.Bus.FailNextInputReads = 2;

        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Status.Should().Be(RoofControllerStatus.Open);
        h.Snapshot.LatchedFaultReason.Should().BeNull();
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PersistentInputReadFailures_LatchInputReadFailure_AfterThreeCycles()
    {
        using var h = await PlantHarness.StartAsync();
        h.Open();
        h.RunFor(TimeSpan.FromSeconds(2));

        h.Bus.FailInputReads = true;
        var failingFrom = h.Plant.Elapsed;

        h.RunUntilStopped(TimeSpan.FromSeconds(10)).Should().BeTrue();
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.InputReadFailure);
        (h.Plant.Elapsed - failingFrom).Should().BeLessThan(TimeSpan.FromSeconds(3.5), "MaxConsecutiveInputReadFailures is 3, one per 1 s cycle");
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
    }
}
