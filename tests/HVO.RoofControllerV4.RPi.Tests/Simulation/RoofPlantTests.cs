using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Simulation;

/// <summary>
/// The whole plant driven by raw relay writes (no controller): the documented circuit, its invariants, and what each
/// wiring fault does. Relay bits: RLY1 forward (0x01), RLY2 reverse (0x02), RLY3 Clear Fault (0x04), RLY4 STOP
/// permit (0x08). Inputs: IN1 open limit, IN2 closed limit, IN3 drive fault relay (closed = healthy), IN4 TB-14 Run.
/// </summary>
[TestClass]
public class RoofPlantTests
{
    private const byte Open = 0x09;
    private const byte Close = 0x0A;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static PlantRig MidTravel(WiringFault wiring = WiringFault.None, SmVectorSettings? drive = null)
        => new(new RoofPlantOptions { InitialPosition = 1.0, Wiring = wiring, Drive = drive ?? new SmVectorSettings() });

    [TestMethod]
    public void AtRestOnTheClosedLimit_InputsShowTheClosedLimitAndAHealthyDrive()
    {
        var rig = new PlantRig();

        rig.Inputs.Should().Be(0x06);
        rig.Plant.ClosedLimit.Actuated.Should().BeTrue();
        rig.Plant.OpenLimit.Actuated.Should().BeFalse();
        rig.Plant.Position.Should().Be(-0.01);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public void Geometry_HardStopsSitInsideTheSwitchOvertravel()
    {
        var plant = new PlantRig().Plant;
        var arm = plant.Options.ClosedLimit.LeverArmMeters;
        var angleAtHardStop = 20 + plant.Options.Mechanics.HardStopBeyondOperatePoint / arm * 180 / Math.PI;

        plant.ClosedHardStop.Should().Be(-0.06);
        plant.OpenHardStop.Should().Be(2.06);
        angleAtHardStop.Should().BeApproximately(88.75, 0.01).And.BeLessThan(95);
    }

    [TestMethod]
    public void RunStarts13MsAfterTheRelayWrite()
    {
        var rig = MidTravel();
        rig.Relays(Open);

        rig.AdvanceMs(12);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.DriveInputs.Should().Be(new SmVectorInputs(true, true, false, false), "the contacts closed at 10 ms");

        rig.AdvanceMs(1);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Running);
        (rig.Inputs & 0x08).Should().Be(0x08, "TB-14 (Run) sinks IN4");
    }

    [TestMethod]
    [DataRow(Open, true)]
    [DataRow(Close, false)]
    public void RelaysHeldToTheLimit_TheLimitsNcContactStopsTheDrive(byte relays, bool opening)
    {
        var rig = MidTravel();
        rig.Relays(relays);

        rig.RunUntil(() => rig.Plant.Drive.Mode == SmVectorMode.Stopped && rig.Plant.Velocity == 0 && rig.Plant.Elapsed > TimeSpan.FromSeconds(1), Minute)
            .Should().BeTrue();

        var plant = rig.Plant;
        plant.Hat.RelayRegister.Should().Be(relays, "the relays are still held");
        plant.Violations.Should().BeEmpty();
        if (opening)
        {
            plant.Position.Should().BeInRange(2.0, 2.02);
            plant.MaximumOpenOvertravel.Should().BeLessThan(plant.Options.Mechanics.HardStopBeyondOperatePoint);
            rig.Inputs.Should().Be(0x05, "IN1 (open limit) and IN3 (healthy)");
        }
        else
        {
            plant.Position.Should().BeInRange(-0.02, 0.0);
            plant.MaximumClosedOvertravel.Should().BeLessThan(plant.Options.Mechanics.HardStopBeyondOperatePoint);
            rig.Inputs.Should().Be(0x06, "IN2 (closed limit) and IN3 (healthy)");
        }

        rig.AdvanceMs(5000);
        plant.Drive.Mode.Should().Be(SmVectorMode.Stopped, "the open NC contact keeps the run input off");
    }

    [TestMethod]
    public void FullOpenFromTheClosedLimit_TakesAbout21Seconds()
    {
        var rig = new PlantRig();
        rig.Relays(Open);

        rig.RunUntil(() => rig.Plant.OpenLimit.Actuated, Minute).Should().BeTrue();

        rig.Plant.Elapsed.TotalSeconds.Should().BeApproximately(21.1, 0.1);
    }

    [TestMethod]
    public void WithoutHardwiredEndStops_TheRoofHitsTheHardStop_AndTheDriveTripsOnTheStall()
    {
        var rig = new PlantRig(new RoofPlantOptions { InitialPosition = 1.9, Wiring = WiringFault.NoHardwiredEndStops });
        rig.Relays(Open);

        rig.RunUntil(() => rig.Plant.Drive.Trip == SmVectorTrip.MotorOverload, Minute).Should().BeTrue();

        var plant = rig.Plant;
        plant.Position.Should().Be(plant.OpenHardStop);
        plant.Violations.Select(v => v.Kind).Should().Equal(PlantViolationKind.HardStopContact);
        plant.OpenLimit.OvertravelExceeded.Should().BeFalse("the lever is at about 89° at the hard stop");
        rig.Inputs.Should().Be(0x01, "IN3 opens on the trip");
    }

    [TestMethod]
    public void HardStopBeyondTheSwitchOvertravel_ReportsLimitSwitchOvertravel()
    {
        var rig = new PlantRig(new RoofPlantOptions
        {
            InitialPosition = 1.9,
            Wiring = WiringFault.NoHardwiredEndStops,
            Mechanics = new RoofMechanicsOptions { HardStopBeyondOperatePoint = 0.1 }
        });
        rig.Relays(Open);

        rig.RunUntil(() => rig.Plant.Drive.Trip != SmVectorTrip.None, Minute).Should().BeTrue();

        rig.Plant.Violations.Select(v => v.Kind).Should().Contain(PlantViolationKind.LimitSwitchOvertravel);
        rig.Plant.OpenLimit.OvertravelExceeded.Should().BeTrue();
    }

    [TestMethod]
    public void BothDirectionRelays_AreAViolation_AndTheDriveDoesNotRun()
    {
        var rig = MidTravel();
        rig.Relays(0x0B);

        rig.AdvanceMs(100);

        rig.Plant.Violations.Select(v => v.Kind).Should().Equal(PlantViolationKind.BothDirectionContactsClosed);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void WeldedDirectionRelay_IsStoppedByTheRly4Permit()
    {
        var rig = MidTravel();
        rig.Plant.SetRelayFault(1, RelayContactFault.Welded);
        rig.Relays(Open);
        rig.AdvanceMs(1000);

        rig.Relays(0x00);
        rig.AdvanceMs(20);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.DriveInputs.Tb13ARunForward.Should().BeTrue("the welded contact still feeds TB-13A");
        rig.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public void WeldedDirectionRelay_WithThePermitBypassed_IsADriveNotStoppedViolation()
    {
        var rig = MidTravel(WiringFault.StopPermitBypassed);
        rig.Plant.SetRelayFault(1, RelayContactFault.Welded);
        rig.Relays(Open);
        rig.AdvanceMs(1000);

        rig.Relays(0x00);
        rig.AdvanceMs(200);

        rig.Plant.Drive.IsDriving.Should().BeTrue();
        rig.Plant.Violations.Select(v => v.Kind).Should().Equal(PlantViolationKind.DriveNotStoppedAfterStop);
    }

    [TestMethod]
    public void RampStop_IsAllowedItsRampTimeBeforeTheStopInvariantFires()
    {
        var rig = MidTravel(drive: new SmVectorSettings { StopMethod = SmVectorStopMethod.Ramp });
        rig.Relays(0x01 | 0x08);
        rig.AdvanceMs(3000);

        // RLY4 stays closed so the permit holds and the drive ramps (TB-13A removed).
        rig.Relays(0x08);
        rig.AdvanceMs(1500);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Decelerating);

        rig.AdvanceMs(1000);
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public void ExternalStop_RemovesThePermit()
    {
        var rig = MidTravel();
        rig.Relays(Open);
        rig.AdvanceMs(1000);

        rig.Plant.ExternalStopOpen = true;
        rig.AdvanceMs(10);

        rig.Plant.DriveInputs.Tb1StopPermit.Should().BeFalse();
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.History.Should().Contain(e => e.Kind == PlantEventKind.Injected && e.Detail == "external STOP on");
    }

    [TestMethod]
    public void Jam_StallsTheMotor_AndTheDriveTrips()
    {
        var rig = MidTravel();
        rig.Relays(Open);
        rig.AdvanceMs(1000);

        rig.Plant.Jammed = true;
        rig.AdvanceMs(10);
        rig.Plant.Velocity.Should().Be(0);

        rig.AdvanceMs(3000);
        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
        (rig.Inputs & 0x04).Should().Be(0, "the fault relay opens");
    }

    [TestMethod]
    public void DrivePowerLoss_DropsEveryMonitorInput()
    {
        var rig = new PlantRig();

        rig.Plant.SetDrivePower(false);
        rig.Inputs.Should().Be(0x00, "TB-11 feeds the limit monitors and TB-16");

        rig.Plant.SetDrivePower(true);
        rig.Inputs.Should().Be(0x06);
        rig.Plant.Drive.SincePowerUp.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void HatPowerLoss_DropsTheRelays_StopsTheDrive_AndFailsTheBus()
    {
        var rig = MidTravel();
        rig.Relays(Open);
        rig.AdvanceMs(1000);

        rig.Plant.SetHatPower(false);
        rig.AdvanceMs(20);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Bus.Invoking(b => b.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>();
        rig.Plant.Violations.Should().BeEmpty();

        rig.Plant.SetHatPower(true);
        rig.Bus.ReadByte(SmI010Board.RelayValueRegister).Should().Be(0);
    }

    [TestMethod]
    public void DeadRelay_NeverStartsTheDrive()
    {
        var rig = MidTravel();
        rig.Plant.SetRelayFault(1, RelayContactFault.Dead);
        rig.Relays(Open);

        rig.AdvanceMs(500);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
        rig.Plant.History.Should().Contain(e => e.Kind == PlantEventKind.Injected && e.Detail == "RLY1 fault Dead");
    }

    [TestMethod]
    [DataRow(WiringFault.MonitorOnNcContacts, 0x05)]
    [DataRow(WiringFault.SwappedLimitInputs, 0x05)]
    [DataRow(WiringFault.InputCommonsOnTb4, 0x00)]
    [DataRow(WiringFault.FaultMonitorWireBroken, 0x02)]
    [DataRow(WiringFault.None, 0x06)]
    public void WiringFaults_ChangeTheInputsAtRestOnTheClosedLimit(WiringFault wiring, int inputs)
    {
        var rig = new PlantRig(new RoofPlantOptions { Wiring = wiring });

        rig.Inputs.Should().Be((byte)inputs);
    }

    [TestMethod]
    public void LimitFaults_ChangeTheMonitorInput()
    {
        var rig = new PlantRig();

        rig.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.BrokenMonitorWire);
        rig.Inputs.Should().Be(0x04);

        rig.Plant.SetLimitFault(openLimit: true, LimitSwitchFault.StuckActuated);
        rig.Inputs.Should().Be(0x05);
    }

    [TestMethod]
    public void RunMonitorWireBroken_KeepsIn4Low()
    {
        var rig = MidTravel(WiringFault.RunMonitorWireBroken);
        rig.Relays(Open);

        rig.AdvanceMs(100);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Running);
        (rig.Inputs & 0x08).Should().Be(0);
    }

    [TestMethod]
    public void SwappedDirectionRelays_Rly1RunsReverse()
    {
        var rig = MidTravel(WiringFault.SwappedDirectionRelays);
        rig.Relays(Open);

        rig.AdvanceMs(500);

        rig.Plant.Drive.OutputDirection.Should().Be(-1);
        rig.Plant.Velocity.Should().BeNegative();
    }

    [TestMethod]
    public void SwappedDirectionRelays_OnTheClosedLimit_TheClosedLimitBlocksIt()
    {
        var rig = new PlantRig(new RoofPlantOptions { Wiring = WiringFault.SwappedDirectionRelays });
        rig.Relays(Open);

        rig.AdvanceMs(500);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped, "TB-13B passes through the actuated closed limit's NC contact");
    }

    [TestMethod]
    public void SwappedMotorLeads_ForwardClosesTheRoof_PastTheWrongLimit()
    {
        var rig = MidTravel(WiringFault.SwappedMotorLeads);
        rig.Relays(Open);
        rig.AdvanceMs(500);

        rig.Plant.Drive.OutputDirection.Should().Be(1);
        rig.Plant.Velocity.Should().BeNegative();

        rig.RunUntil(() => rig.Plant.Drive.Trip != SmVectorTrip.None, Minute).Should().BeTrue();
        rig.Plant.Position.Should().Be(rig.Plant.ClosedHardStop, "the closed limit is not in the forward path");
        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
    }

    [TestMethod]
    public void StopPermitBypassed_RunsWithoutRly4()
    {
        var rig = MidTravel(WiringFault.StopPermitBypassed);
        rig.Relays(0x01);

        rig.AdvanceMs(20);

        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void StopPermitOnTb2_NeverPermits()
    {
        var rig = MidTravel(WiringFault.StopPermitOnTb2);
        rig.Relays(Open);

        rig.AdvanceMs(100);

        rig.Plant.DriveInputs.Tb1StopPermit.Should().BeFalse();
        rig.Plant.Drive.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void WiringChangedDuringARun_IsRecorded_AndTakesEffect()
    {
        var rig = MidTravel();
        rig.Relays(Open);
        rig.AdvanceMs(100);

        rig.Plant.Wiring = WiringFault.RunMonitorWireBroken;

        (rig.Inputs & 0x08).Should().Be(0);
        rig.Plant.Wiring.Should().Be(WiringFault.RunMonitorWireBroken);
        rig.Plant.History.Should().Contain(e => e.Kind == PlantEventKind.Injected && e.Detail == "wiring None -> RunMonitorWireBroken");
    }

    [TestMethod]
    public void TripDrive_OpensTheFaultRelay()
    {
        var rig = new PlantRig();

        rig.Plant.TripDrive();

        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.External);
        rig.Inputs.Should().Be(0x02);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearFaultHeldWithoutATrip_ConsumesTheClosure_WhetherTheTimeIsSteppedOrFastForwarded(bool oneMillisecondSteps)
    {
        var rig = new PlantRig();
        rig.Relays(0x04);
        if (oneMillisecondSteps)
        {
            for (var i = 0; i < 100; i++)
            {
                rig.AdvanceMs(1);
            }
        }
        else
        {
            rig.AdvanceMs(100);
        }

        rig.Plant.TripDrive();
        rig.AdvanceMs(100);

        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.External, "Clear Fault acts once per closure, and this one was held 100 ms before the trip");
    }

    [TestMethod]
    public void SameScript_SameHistory()
    {
        static IReadOnlyList<PlantEvent> Run()
        {
            var rig = MidTravel();
            rig.Relays(Open);
            rig.AdvanceMs(3000);
            rig.Plant.TripDrive();
            rig.Relays(0x00);
            rig.AdvanceMs(500);
            rig.Relays(0x04);
            rig.AdvanceMs(250);
            rig.Relays(Close);
            rig.RunUntil(() => rig.Plant.ClosedLimit.Actuated && rig.Plant.Velocity == 0, Minute);
            return rig.Plant.History;
        }

        var first = Run();

        first.Should().NotBeEmpty();
        Run().Should().Equal(first);
    }

    [TestMethod]
    public void ScheduledActions_RunInTimeThenInsertionOrder_AtTheirTime()
    {
        var rig = new PlantRig();
        var ran = new List<(string Name, TimeSpan At)>();
        rig.Plant.Schedule(TimeSpan.FromMilliseconds(10), p => ran.Add(("b", p.Elapsed)));
        rig.Plant.Schedule(TimeSpan.FromMilliseconds(10), p => ran.Add(("c", p.Elapsed)));
        rig.Plant.Schedule(TimeSpan.FromMilliseconds(5), p => ran.Add(("a", p.Elapsed)));

        rig.AdvanceMs(20);
        rig.Plant.ScheduleAfter(TimeSpan.FromMilliseconds(5), p => ran.Add(("d", p.Elapsed)));
        rig.AdvanceMs(4);
        ran.Should().HaveCount(3);
        rig.AdvanceMs(1);

        ran.Should().Equal(
            ("a", TimeSpan.FromMilliseconds(5)),
            ("b", TimeSpan.FromMilliseconds(10)),
            ("c", TimeSpan.FromMilliseconds(10)),
            ("d", TimeSpan.FromMilliseconds(25)));
    }

    [TestMethod]
    public void ScheduledAction_CanChangeThePlant()
    {
        var rig = MidTravel();
        rig.Relays(Open);
        rig.Plant.ScheduleAfter(TimeSpan.FromSeconds(1), p => p.TripDrive());

        rig.AdvanceMs(999);
        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.None);
        rig.AdvanceMs(1);
        rig.Plant.Drive.Trip.Should().Be(SmVectorTrip.External);
    }

    [TestMethod]
    public void IdlePlant_FastForwards_AndTheDriveUptimeKeepsCounting()
    {
        var rig = new PlantRig();

        rig.Advance(TimeSpan.FromMinutes(10));

        rig.Plant.Elapsed.Should().Be(TimeSpan.FromMinutes(10));
        rig.Plant.Drive.SincePowerUp.Should().Be(TimeSpan.FromMinutes(11));
    }

    [TestMethod]
    public void History_DropsTheOldestHalfWhenFull()
    {
        var rig = new PlantRig(new RoofPlantOptions { MaxHistory = 100 });

        for (var i = 0; i < 150; i++)
        {
            rig.Plant.Note($"note {i}");
        }

        rig.Plant.HistoryDropped.Should().BeGreaterThan(0);
        rig.Plant.History.Should().HaveCountLessThanOrEqualTo(100);
        rig.Plant.History[^1].Detail.Should().Be("note 149");
    }

    [TestMethod]
    public void Options_Validate_RejectsImpossibleValues()
    {
        var invalid = new[]
        {
            new RoofPlantOptions { StepSize = TimeSpan.Zero },
            new RoofPlantOptions { StepSize = TimeSpan.FromMilliseconds(11) },
            new RoofPlantOptions { StepSize = TimeSpan.FromMilliseconds(2) },
            new RoofPlantOptions
            {
                StepSize = TimeSpan.FromMilliseconds(2),
                OpenLimit = new Me8108Options { BounceTime = TimeSpan.Zero }
            },
            new RoofPlantOptions { MaxHistory = 99 },
            new RoofPlantOptions { InitialPosition = -0.06 },
            new RoofPlantOptions { InitialPosition = 2.06 },
            new RoofPlantOptions { Mechanics = new RoofMechanicsOptions { TravelMeters = 0 } },
            new RoofPlantOptions { Drive = new SmVectorSettings { OutputInversion = 5 } },
            new RoofPlantOptions { OpenLimit = new Me8108Options { LeverArmMeters = -1 } }
        };

        foreach (var options in invalid)
        {
            options.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RoofPlant(options, new ManualTimeProvider())).Should().Throw<ArgumentOutOfRangeException>();
        }

        // A step over 1 ms is valid once neither switch bounces.
        var noBounce = new Me8108Options { BounceTime = TimeSpan.Zero };
        var slowAction = new Me8108Options { ContactAction = Me8108ContactAction.SlowAction };
        new RoofPlantOptions { StepSize = TimeSpan.FromMilliseconds(10), OpenLimit = noBounce, ClosedLimit = noBounce }
            .Invoking(o => o.Validate()).Should().NotThrow();
        new RoofPlantOptions { StepSize = TimeSpan.FromMilliseconds(10), OpenLimit = slowAction, ClosedLimit = noBounce }
            .Invoking(o => o.Validate()).Should().NotThrow();
    }
}
