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
/// Wiring mistakes and wrong settings, from each start position: closed, mid-travel and open. The roof must never
/// reach a hard stop. Each fault either leaves the roof still with a latched fault, stops it and latches, or (for the
/// redundant paths the controller cannot see) operates normally. Swapped motor leads from a limit are the one exception,
/// covered on their own below.
/// </summary>
[TestClass]
public class PlantWiringFaultTests
{
    private const double Closed = -0.01;
    private const double MidTravel = 1.0;
    private const double Open = 2.01;

    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    [DataRow(WiringFault.MonitorOnNcContacts, Closed, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.MonitorOnNcContacts, MidTravel, RoofControllerStopReason.ContradictoryLimitInputs)]
    [DataRow(WiringFault.MonitorOnNcContacts, Open, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.SwappedDirectionRelays, Closed, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.SwappedDirectionRelays, MidTravel, RoofControllerStopReason.StartLimitReasserted)]
    [DataRow(WiringFault.SwappedDirectionRelays, Open, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.SwappedLimitInputs, Closed, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.SwappedLimitInputs, MidTravel, RoofControllerStopReason.StartLimitReasserted)]
    [DataRow(WiringFault.SwappedLimitInputs, Open, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.SwappedMotorLeads, MidTravel, RoofControllerStopReason.StartLimitReasserted)]
    [DataRow(WiringFault.StopPermitBypassed, Closed, RoofControllerStopReason.None)]
    [DataRow(WiringFault.StopPermitBypassed, MidTravel, RoofControllerStopReason.None)]
    [DataRow(WiringFault.StopPermitBypassed, Open, RoofControllerStopReason.None)]
    [DataRow(WiringFault.InputCommonsOnTb4, Closed, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.InputCommonsOnTb4, MidTravel, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.InputCommonsOnTb4, Open, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.NoHardwiredEndStops, Closed, RoofControllerStopReason.None)]
    [DataRow(WiringFault.NoHardwiredEndStops, MidTravel, RoofControllerStopReason.None)]
    [DataRow(WiringFault.NoHardwiredEndStops, Open, RoofControllerStopReason.None)]
    [DataRow(WiringFault.FaultMonitorWireBroken, Closed, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.FaultMonitorWireBroken, MidTravel, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.FaultMonitorWireBroken, Open, RoofControllerStopReason.DriveFault)]
    [DataRow(WiringFault.RunMonitorWireBroken, Closed, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.RunMonitorWireBroken, MidTravel, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.RunMonitorWireBroken, Open, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.StopPermitOnTb2, Closed, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.StopPermitOnTb2, MidTravel, RoofControllerStopReason.DriveNotRunning)]
    [DataRow(WiringFault.StopPermitOnTb2, Open, RoofControllerStopReason.DriveNotRunning)]
    public async Task WiringFault_OpenThenClose_NeverReachesAHardStop(WiringFault fault, double start, RoofControllerStopReason expected)
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Wiring = fault, InitialPosition = start });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();

        (h.Snapshot.LatchedFaultReason ?? RoofControllerStopReason.None).Should().Be(expected);
        h.Controller.IsMoving.Should().BeFalse();
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
        if (expected == RoofControllerStopReason.None)
        {
            h.Status.Should().Be(RoofControllerStatus.Closed, "the lost redundancy is invisible in normal operation");
        }
    }

    [TestMethod]
    [DataRow(Closed)]
    [DataRow(Open)]
    public async Task SwappedMotorLeads_FromALimit_ReachTheHardStop_BeforeTheStallTripLatchesDriveFault(double start)
    {
        // The named exception: the roof drives away from the limit it rests on, into the hard stop behind it, and the
        // limit it is on never releases. Only the drive's stall trip ends it. DepartureReleaseTimeout (below) closes
        // this gap when it is set from the measured release time.
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Wiring = WiringFault.SwappedMotorLeads, InitialPosition = start });

        var move = start < MidTravel ? h.Open() : h.Close();
        move.IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        var violation = h.Violations.Should().ContainSingle().Which;
        violation.Kind.Should().Be(PlantViolationKind.HardStopContact);
        violation.At.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
        h.Plant.Drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    [DataRow(Closed)]
    [DataRow(Open)]
    public async Task SwappedMotorLeads_FromALimit_WithADepartureReleaseTimeout_StopBeforeTheHardStop(double start)
    {
        using var h = await PlantHarness.StartAsync(
            new RoofPlantOptions { Wiring = WiringFault.SwappedMotorLeads, InitialPosition = start },
            o => o.DepartureReleaseTimeout = TimeSpan.FromSeconds(1.2));

        var move = start < MidTravel ? h.Open() : h.Close();
        move.IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DepartureReleaseTimeout_AboveTheReleaseTime_AllowsNormalCycles()
    {
        // With P104 = 2 s the limit releases about 0.9 s after the command and a wrong-way move reaches the hard stop
        // at about 1.4 s, so 1.2 s sits between them.
        using var h = await PlantHarness.StartAsync(configure: o => o.DepartureReleaseTimeout = TimeSpan.FromSeconds(1.2));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.Open().IsSuccessful.Should().BeTrue();
            h.RunUntilStopped(Travel).Should().BeTrue();
            h.Status.Should().Be(RoofControllerStatus.Open);

            h.Close().IsSuccessful.Should().BeTrue();
            h.RunUntilStopped(Travel).Should().BeTrue();
            h.Status.Should().Be(RoofControllerStatus.Closed);
        }

        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DepartureReleaseTimeout_BelowTheReleaseTime_StopsEveryStart()
    {
        // The timeout must be measured against the installed ramp: with P104 = 20 s the limit takes about 2.7 s to
        // release, so a 1.2 s timeout stops a correctly wired roof.
        using var h = await PlantHarness.StartAsync(
            new RoofPlantOptions { Drive = new SmVectorSettings { AccelerationTime = TimeSpan.FromSeconds(20) } },
            o => o.DepartureReleaseTimeout = TimeSpan.FromSeconds(1.2));

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(1, RoofControllerStopReason.DriveFault, RoofControllerErrorCode.FaultLatched)]
    [DataRow(2, RoofControllerStopReason.None, RoofControllerErrorCode.InterlockActive)]
    [DataRow(3, RoofControllerStopReason.DriveFault, RoofControllerErrorCode.FaultLatched)]
    public async Task DriveOutputInversion_P144_RefusesToMove(int inversion, RoofControllerStopReason latched, RoofControllerErrorCode refusal)
    {
        // P144 = 1 inverts the fault relay, so a healthy drive reads as tripped; 2 inverts TB-14, so a stopped drive
        // reads as running and the run interlock refuses the start.
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Drive = new SmVectorSettings { OutputInversion = inversion } });

        (h.Snapshot.LatchedFaultReason ?? RoofControllerStopReason.None).Should().Be(latched);
        h.Open().ErrorCode().Should().Be(refusal);
        h.Close().ErrorCode().Should().Be(refusal);

        h.RunFor(TimeSpan.FromSeconds(5));
        h.Plant.Position.Should().Be(Closed);
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(2, RoofControllerStopReason.None)]
    [DataRow(20, RoofControllerStopReason.DriveNotRunning)]
    public async Task AtSpeedOutput_P142Is6_ConfirmsOnlyOnceTheDriveReachesSpeed(int accelerationSeconds, RoofControllerStopReason expected)
    {
        // The wiring doc's alternative for TB-14. At speed comes at the end of the P104 ramp, so the 3 s confirmation
        // window must exceed P104; with the 20 s factory ramp every start stops.
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions
        {
            Drive = new SmVectorSettings { Tb14Output = SmVectorOutputFunction.AtSpeed, AccelerationTime = TimeSpan.FromSeconds(accelerationSeconds) }
        });

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        (h.Snapshot.LatchedFaultReason ?? RoofControllerStopReason.None).Should().Be(expected);
        h.Status.Should().Be(expected == RoofControllerStopReason.None ? RoofControllerStatus.Open : RoofControllerStatus.Error);
        h.RelayRegister.Should().Be(0);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NormallyClosedSetting_OnTheNoWiring_ReadsTheLimitsInverted_AndTheRoofNeverMoves()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.UseNormallyClosedLimitSwitches = true);
        h.Status.Should().Be(RoofControllerStatus.Open, "the closed roof reads as open");

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();
        h.Close();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning,
            "the closed limit's NC pair holds the REV run input open, so the drive never starts");
        h.Plant.Position.Should().Be(-0.01);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FaultActiveHighSetting_OnTheP140Wiring_LatchesDriveFaultAtStartup()
    {
        using var h = await PlantHarness.StartAsync(configure: o => o.FaultInputActiveHigh = true);

        h.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault, "a healthy drive holds IN3 HIGH");
        h.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }
}
