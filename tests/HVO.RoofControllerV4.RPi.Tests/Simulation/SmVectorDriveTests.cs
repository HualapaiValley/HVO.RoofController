using System;
using System.Linq;
using FluentAssertions;
using HVO.RoofControllerV4.Simulation.Drive;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Simulation;

/// <summary>
/// The SMVector model on its own, stepped 1 ms at a time. Defaults: P104/P105 2 s to 60 Hz (30 Hz/s), coast stop,
/// P140 = Fault, P142 = Run, 4 ms input response, 20 ms minimum Clear Fault pulse.
/// </summary>
[TestClass]
public class SmVectorDriveTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);
    private static readonly SmVectorInputs Idle = new(true, false, false, false);
    private static readonly SmVectorInputs Forward = new(true, true, false, false);
    private static readonly SmVectorInputs Reverse = new(true, false, true, false);
    private static readonly SmVectorInputs ForwardWithoutPermit = new(false, true, false, false);
    private static readonly SmVectorInputs ClearFault = new(true, false, false, true);

    private static SmVectorDrive Create(
        SmVectorSettings? settings = null,
        SmVectorAssumptions? assumptions = null,
        bool powered = true,
        TimeSpan? uptime = null)
        => new(settings ?? new SmVectorSettings(), assumptions ?? new SmVectorAssumptions(), powered, uptime ?? TimeSpan.FromMinutes(1));

    private static void Step(SmVectorDrive drive, SmVectorInputs inputs, int milliseconds, bool stalled = false)
    {
        for (var i = 0; i < milliseconds; i++)
        {
            drive.Step(Ms, inputs, stalled);
        }
    }

    private static SmVectorDrive AtSpeed(SmVectorSettings? settings = null, SmVectorAssumptions? assumptions = null)
    {
        var drive = Create(settings, assumptions);
        Step(drive, Forward, 2100 + (int)drive.Settings.StartDcBrakeTime.TotalMilliseconds);
        drive.OutputFrequencyHz.Should().Be(60);
        return drive;
    }

    [TestMethod]
    public void RunForward_StartsAfterTheInputResponse_AndRampsAt30HzPerSecond()
    {
        var drive = Create();

        Step(drive, Forward, 3);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.Tb14Sinking.Should().BeFalse();

        Step(drive, Forward, 1);
        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.OutputDirection.Should().Be(1);
        drive.IsDriving.Should().BeTrue();
        drive.Tb14Sinking.Should().BeTrue("P142 = Run");
        drive.OutputFrequencyHz.Should().BeApproximately(0.03, 1e-9);

        Step(drive, Forward, 999);
        drive.OutputFrequencyHz.Should().BeApproximately(30, 1e-6);

        Step(drive, Forward, 1100);
        drive.OutputFrequencyHz.Should().Be(60, "the output stops at the P103 reference");
    }

    [TestMethod]
    public void RunReverse_RunsWithNegativeDirection()
    {
        var drive = Create();
        Step(drive, Reverse, 10);

        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.OutputDirection.Should().Be(-1);
    }

    [TestMethod]
    public void ZeroAccelerationTime_ReachesTheReferenceInOneStep()
    {
        var drive = Create(new SmVectorSettings { AccelerationTime = TimeSpan.Zero });
        Step(drive, Forward, 4);

        drive.OutputFrequencyHz.Should().Be(60);
    }

    [TestMethod]
    public void CoastStop_TurnsTheOutputOffWhenTheRunInputIsRemoved()
    {
        var drive = AtSpeed();

        Step(drive, Idle, 3);
        drive.Mode.Should().Be(SmVectorMode.Running);

        Step(drive, Idle, 1);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.OutputFrequencyHz.Should().Be(0);
        drive.OutputDirection.Should().Be(0);
        drive.IsDriving.Should().BeFalse();
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RampStop_DeceleratesAt30HzPerSecond_WithTheRunOutputPerTheAssumption(bool runOutputDuringDeceleration)
    {
        var drive = AtSpeed(
            new SmVectorSettings { StopMethod = SmVectorStopMethod.Ramp },
            new SmVectorAssumptions { RunOutputDuringDeceleration = runOutputDuringDeceleration });

        Step(drive, Idle, 4);
        drive.Mode.Should().Be(SmVectorMode.Decelerating);
        drive.IsDriving.Should().BeTrue();
        drive.Tb14Sinking.Should().Be(runOutputDuringDeceleration);

        Step(drive, Idle, 999);
        drive.OutputFrequencyHz.Should().BeApproximately(30, 1e-6);
        drive.OutputDirection.Should().Be(1);

        Step(drive, Idle, 1001);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.OutputFrequencyHz.Should().Be(0);
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CoastWithDcBrake_BrakesForP175_ThenStops(bool runOutputDuringDcBrake)
    {
        var drive = AtSpeed(
            new SmVectorSettings { StopMethod = SmVectorStopMethod.CoastWithDcBrake, DcBrakeTime = TimeSpan.FromMilliseconds(500) },
            new SmVectorAssumptions { RunOutputDuringDcBrake = runOutputDuringDcBrake });

        Step(drive, Idle, 4);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);
        drive.IsDriving.Should().BeFalse("the DC brake does not drive the motor");
        drive.IsDcBraking.Should().BeTrue();
        drive.OutputFrequencyHz.Should().Be(0);
        drive.Tb14Sinking.Should().Be(runOutputDuringDcBrake, "P142 = Run follows the assumption during the brake");

        Step(drive, Idle, 499);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);

        Step(drive, Idle, 1);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    public void CoastWithContinuousDcBrake_BrakesUntilARun()
    {
        var drive = AtSpeed(new SmVectorSettings { StopMethod = SmVectorStopMethod.CoastWithDcBrake, DcBrakeTime = SmVectorSettings.ContinuousDcBrake });

        Step(drive, Idle, 4);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);
        drive.IsSettled.Should().BeTrue("nothing changes until an input does");
        drive.AdvanceIdle(TimeSpan.FromHours(1));
        Step(drive, Idle, 1000);
        drive.Mode.Should().Be(SmVectorMode.DcBraking, "P175 = 999.9 brakes until a run or a fault (SV01J p.34)");
        drive.Tb14Sinking.Should().BeTrue();

        Step(drive, Forward, 4);
        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void ContinuousDcBrake_EndsOnAFault()
    {
        var drive = AtSpeed(new SmVectorSettings { StopMethod = SmVectorStopMethod.CoastWithDcBrake, DcBrakeTime = SmVectorSettings.ContinuousDcBrake });
        Step(drive, Idle, 10);

        drive.InjectTrip();

        drive.Mode.Should().Be(SmVectorMode.Faulted);
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    public void CoastWithDcBrake_WithoutABrakeTime_StopsAtOnce()
    {
        var drive = AtSpeed(new SmVectorSettings { StopMethod = SmVectorStopMethod.CoastWithDcBrake });

        Step(drive, Idle, 4);

        drive.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void RampWithDcBrake_RampsToZero_ThenBrakes()
    {
        var drive = AtSpeed(new SmVectorSettings
        {
            StopMethod = SmVectorStopMethod.RampWithDcBrake,
            DecelerationTime = TimeSpan.FromMilliseconds(100),
            DcBrakeTime = TimeSpan.FromMilliseconds(200)
        });

        Step(drive, Idle, 4);
        drive.Mode.Should().Be(SmVectorMode.Decelerating);

        Step(drive, Idle, 100);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);

        Step(drive, Idle, 200);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void RunInputReturningDuringTheDcBrake_RestartsTheDrive()
    {
        var drive = AtSpeed(new SmVectorSettings { StopMethod = SmVectorStopMethod.CoastWithDcBrake, DcBrakeTime = TimeSpan.FromSeconds(1) });
        Step(drive, Idle, 10);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);

        Step(drive, Forward, 4);

        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Tb1Stop_StopsTheDrive_AndReleasingItRestartsOnlyPerTheAssumption(bool restartWhenReleased)
    {
        var drive = AtSpeed(assumptions: new SmVectorAssumptions { RestartWhenStopReleasedWithRunHeld = restartWhenReleased });

        Step(drive, ForwardWithoutPermit, 4);
        drive.Mode.Should().Be(SmVectorMode.Stopped);

        Step(drive, Forward, 10);
        drive.Mode.Should().Be(restartWhenReleased ? SmVectorMode.Running : SmVectorMode.Stopped);

        // A new run edge always starts it.
        Step(drive, Idle, 10);
        Step(drive, Forward, 10);
        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void RunWithoutThePermit_DoesNotStart()
    {
        var drive = Create();

        Step(drive, ForwardWithoutPermit, 100);

        drive.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(3)]
    public void StartControlSource_OtherThanTheTerminalStrip_IgnoresTheRunInputs(int p100)
    {
        // P100 = 0 also disables TB-1 as a STOP input (SV01J p.25), but nothing can start the modelled drive.
        var drive = Create(new SmVectorSettings { StartControlSource = p100 });

        Step(drive, Forward, 100);
        Step(drive, ForwardWithoutPermit, 100);
        Step(drive, Reverse, 100);

        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public void StartControlSource_WithTheTerminalStrip_RunsAndHonoursTheStopPermit(int p100)
    {
        var drive = Create(new SmVectorSettings { StartControlSource = p100 });

        Step(drive, ForwardWithoutPermit, 100);
        drive.Mode.Should().Be(SmVectorMode.Stopped, "TB-1 is an active STOP input for P100 != 0");

        Step(drive, Idle, 10);
        Step(drive, Reverse, 10);
        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.OutputDirection.Should().Be(-1);
    }

    [TestMethod]
    public void ForwardOnlyRotation_IgnoresRunReverse_AndStillStopsOnBothInputs()
    {
        // P112 = 0: "If any input is set to 10, 12 or 14, P112 must be set to 1 for Reverse action to function" (SV01J p.30).
        var settings = new SmVectorSettings { ReverseEnabled = false };
        var idle = Create(settings);
        Step(idle, Reverse, 100);
        idle.Mode.Should().Be(SmVectorMode.Stopped);

        var running = AtSpeed(settings);
        Step(running, Reverse, 4);
        running.Mode.Should().Be(SmVectorMode.Stopped, "a change from forward to reverse removes the only valid run input");

        var both = AtSpeed(settings);
        Step(both, new SmVectorInputs(true, true, true, false), 4);
        both.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void BothRunInputs_DoNotRun_AndStopARunningDrive()
    {
        var both = new SmVectorInputs(true, true, true, false);
        var idle = Create();
        Step(idle, both, 100);
        idle.Mode.Should().Be(SmVectorMode.Stopped);

        var running = AtSpeed();
        Step(running, both, 4);
        running.Mode.Should().Be(SmVectorMode.Stopped);
    }

    [TestMethod]
    public void DirectionChange_RampsThroughZeroAtTheDecelerationRate_ThenReverses()
    {
        var drive = AtSpeed();

        Step(drive, Reverse, 4 + 999);
        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.OutputDirection.Should().Be(1, "the output still turns forward while it slows");
        drive.OutputFrequencyHz.Should().BeApproximately(30, 1e-6);

        Step(drive, Reverse, 1001);
        drive.OutputDirection.Should().Be(-1);

        Step(drive, Reverse, 2100);
        drive.OutputDirection.Should().Be(-1);
        drive.OutputFrequencyHz.Should().Be(60);
    }

    [TestMethod]
    [DataRow(1995, true)]
    [DataRow(1996, false)]
    public void StartWithinTwoSecondsOfPowerUp_TripsFUF(int uptimeMilliseconds, bool trips)
    {
        // The start is seen 4 ms (the input response) after it is applied.
        var drive = Create(uptime: TimeSpan.FromMilliseconds(uptimeMilliseconds));

        Step(drive, Forward, 4);

        if (trips)
        {
            drive.Mode.Should().Be(SmVectorMode.Faulted);
            drive.Trip.Should().Be(SmVectorTrip.StartTooSoonAfterPowerUp);
            drive.RelayOutputClosed.Should().BeFalse("P140 = Fault opens on a trip");
        }
        else
        {
            drive.Mode.Should().Be(SmVectorMode.Running);
            drive.Trip.Should().Be(SmVectorTrip.None);
        }
    }

    [TestMethod]
    public void PowerCycle_RestartsTheStartLockout()
    {
        var drive = Create();
        drive.SetPower(false);
        drive.SetPower(true);
        drive.SincePowerUp.Should().Be(TimeSpan.Zero);

        Step(drive, Forward, 4);

        drive.Trip.Should().Be(SmVectorTrip.StartTooSoonAfterPowerUp);
    }

    [TestMethod]
    public void StartMethod2_HasTheSameLockout()
    {
        var drive = Create(new SmVectorSettings { StartMethod = 2 }, uptime: TimeSpan.Zero);

        Step(drive, Forward, 4);

        drive.Trip.Should().Be(SmVectorTrip.StartTooSoonAfterPowerUp);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StartMethod2_BrakesForP175_BeforeTheMotorStarts(bool runOutputDuringDcBrake)
    {
        var drive = Create(
            new SmVectorSettings { StartMethod = 2, DcBrakeTime = TimeSpan.FromMilliseconds(500) },
            new SmVectorAssumptions { RunOutputDuringDcBrake = runOutputDuringDcBrake });

        Step(drive, Forward, 4);
        drive.Mode.Should().Be(SmVectorMode.StartDcBraking);
        drive.IsDriving.Should().BeFalse();
        drive.IsDcBraking.Should().BeTrue();
        drive.OutputFrequencyHz.Should().Be(0);
        drive.Tb14Sinking.Should().Be(runOutputDuringDcBrake);

        Step(drive, Forward, 499);
        drive.Mode.Should().Be(SmVectorMode.StartDcBraking);

        Step(drive, Forward, 1);
        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.OutputDirection.Should().Be(1);
        drive.Tb14Sinking.Should().BeTrue();
    }

    [TestMethod]
    public void StartMethod2_WithP175Continuous_BrakesFor15Seconds()
    {
        // "If P110 = 2, 4...6 and P175 = 999.9, brake voltage will be applied for 15s" (SV01J p.34).
        var drive = Create(new SmVectorSettings { StartMethod = 2, DcBrakeTime = SmVectorSettings.ContinuousDcBrake });

        Step(drive, Forward, 4 + 14_999);
        drive.Mode.Should().Be(SmVectorMode.StartDcBraking);

        Step(drive, Forward, 1);
        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void StartMethod2_WithoutABrakeTime_StartsAtOnce()
    {
        var drive = Create(new SmVectorSettings { StartMethod = 2 });

        Step(drive, Forward, 4);

        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartMethod2_RunRemovedDuringTheBrake_StopsWithoutTheStopMethod(bool removeByStop)
    {
        var drive = Create(new SmVectorSettings
        {
            StartMethod = 2,
            StopMethod = SmVectorStopMethod.CoastWithDcBrake,
            DcBrakeTime = TimeSpan.FromMilliseconds(500)
        });
        var changes = new System.Collections.Generic.List<string>();
        drive.Changed += changes.Add;
        Step(drive, Forward, 100);

        Step(drive, removeByStop ? ForwardWithoutPermit : Idle, 4);

        drive.Mode.Should().Be(SmVectorMode.Stopped, "the motor never started, so there is nothing to brake after");
        changes.Last().Should().EndWith("during the DC brake before start");
    }

    [TestMethod]
    public void StartMethod2_ADirectionChangeWhileRunning_DoesNotBrake()
    {
        var drive = AtSpeed(new SmVectorSettings { StartMethod = 2, DcBrakeTime = TimeSpan.FromMilliseconds(500) });

        Step(drive, Reverse, 4);

        drive.Mode.Should().Be(SmVectorMode.Running, "the output is already on; it ramps through 0 Hz");
    }

    [TestMethod]
    public void StartMethod2_ARunDuringTheStopBrake_BrakesAgainBeforeStarting()
    {
        var drive = AtSpeed(new SmVectorSettings
        {
            StartMethod = 2,
            StopMethod = SmVectorStopMethod.CoastWithDcBrake,
            DcBrakeTime = TimeSpan.FromMilliseconds(500)
        });
        Step(drive, Idle, 100);
        drive.Mode.Should().Be(SmVectorMode.DcBraking);

        Step(drive, Forward, 4);
        drive.Mode.Should().Be(SmVectorMode.StartDcBraking);

        Step(drive, Forward, 500);
        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    [DataRow(19, false)]
    [DataRow(20, true)]
    public void ClearFault_NeedsTheMinimumPulse(int pulseMilliseconds, bool resets)
    {
        var drive = Create();
        drive.InjectTrip();

        Step(drive, ClearFault, pulseMilliseconds);
        Step(drive, Idle, 10);

        drive.Trip.Should().Be(resets ? SmVectorTrip.None : SmVectorTrip.External);
        drive.Mode.Should().Be(resets ? SmVectorMode.Stopped : SmVectorMode.Faulted);
        drive.RelayOutputClosed.Should().Be(resets);
    }

    [TestMethod]
    public void ClearFault_ActsOncePerClosure()
    {
        var drive = Create();
        drive.InjectTrip();
        Step(drive, ClearFault, 30);
        drive.Trip.Should().Be(SmVectorTrip.None);

        drive.InjectTrip();
        Step(drive, ClearFault, 100);
        drive.Trip.Should().Be(SmVectorTrip.External, "a fault while Clear Fault is held stays latched");

        Step(drive, Idle, 10);
        Step(drive, ClearFault, 30);
        drive.Trip.Should().Be(SmVectorTrip.None);
    }

    [TestMethod]
    public void ClearFault_WithoutAFault_ChangesNothing()
    {
        var drive = Create();

        Step(drive, ClearFault, 50);

        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.Trip.Should().Be(SmVectorTrip.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResetWithTheRunInputHeld_RestartsOnlyPerTheAssumption(bool restartAfterReset)
    {
        var drive = AtSpeed(assumptions: new SmVectorAssumptions { RestartAfterResetWithRunHeld = restartAfterReset });
        drive.InjectTrip();
        drive.Mode.Should().Be(SmVectorMode.Faulted);
        drive.OutputFrequencyHz.Should().Be(0);

        Step(drive, new SmVectorInputs(true, true, false, true), 30);

        drive.Trip.Should().Be(SmVectorTrip.None);
        drive.Mode.Should().Be(restartAfterReset ? SmVectorMode.Running : SmVectorMode.Stopped);
    }

    [TestMethod]
    public void RunInputRemovedWhileFaulted_RearmsTheStart()
    {
        var drive = AtSpeed();
        drive.InjectTrip();
        Step(drive, Idle, 10);
        Step(drive, ClearFault, 30);

        Step(drive, Forward, 10);

        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void StalledMotor_TripsMotorOverloadAfterTheStallTime()
    {
        var drive = AtSpeed();

        Step(drive, Forward, 2999, stalled: true);
        drive.Mode.Should().Be(SmVectorMode.Running);

        Step(drive, Forward, 1, stalled: true);
        drive.Mode.Should().Be(SmVectorMode.Faulted);
        drive.Trip.Should().Be(SmVectorTrip.MotorOverload);
        drive.IsDriving.Should().BeFalse();
    }

    [TestMethod]
    public void StallThatClears_RestartsTheStallTimer()
    {
        var drive = AtSpeed();

        Step(drive, Forward, 2000, stalled: true);
        Step(drive, Forward, 1);
        Step(drive, Forward, 2000, stalled: true);

        drive.Mode.Should().Be(SmVectorMode.Running);
    }

    [TestMethod]
    public void PowerLoss_TurnsEverythingOff_AndClearsTheFault()
    {
        var drive = AtSpeed();
        drive.InjectTrip();

        drive.SetPower(false);

        drive.Mode.Should().Be(SmVectorMode.Unpowered);
        drive.Trip.Should().Be(SmVectorTrip.None);
        drive.Tb11Energized.Should().BeFalse();
        drive.RelayOutputClosed.Should().BeFalse();
        drive.Tb14Sinking.Should().BeFalse();

        Step(drive, Forward, 100);
        drive.Mode.Should().Be(SmVectorMode.Unpowered);
        drive.SincePowerUp.Should().Be(TimeSpan.Zero);

        drive.SetPower(true);
        drive.Mode.Should().Be(SmVectorMode.Stopped);
        drive.RelayOutputClosed.Should().BeTrue();
    }

    [TestMethod]
    public void UnpoweredOutputs_AreOff_EvenWhenInverted()
    {
        var drive = Create(new SmVectorSettings { OutputInversion = 3 }, powered: false);

        drive.RelayOutputClosed.Should().BeFalse();
        drive.Tb14Sinking.Should().BeFalse();
    }

    [TestMethod]
    public void UnpoweredConstruction_HasNoUptime()
    {
        var drive = Create(powered: false);

        drive.Mode.Should().Be(SmVectorMode.Unpowered);
        drive.Powered.Should().BeFalse();
        drive.SincePowerUp.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void RelayOutput_FollowsTheFaultFunctions()
    {
        var fault = Create();
        var inverse = Create(new SmVectorSettings { RelayOutput = SmVectorOutputFunction.InverseFault });
        var none = Create(new SmVectorSettings { RelayOutput = SmVectorOutputFunction.None });

        fault.RelayOutputClosed.Should().BeTrue();
        inverse.RelayOutputClosed.Should().BeFalse();
        none.RelayOutputClosed.Should().BeFalse();

        fault.InjectTrip();
        inverse.InjectTrip();
        none.InjectTrip();

        fault.RelayOutputClosed.Should().BeFalse();
        inverse.RelayOutputClosed.Should().BeTrue();
        none.RelayOutputClosed.Should().BeFalse();
    }

    [TestMethod]
    public void AtSpeedOutput_ClosesOnlyAtTheReference()
    {
        var drive = Create(new SmVectorSettings { Tb14Output = SmVectorOutputFunction.AtSpeed });

        Step(drive, Forward, 1000);
        drive.Mode.Should().Be(SmVectorMode.Running);
        drive.Tb14Sinking.Should().BeFalse();

        Step(drive, Forward, 1100);
        drive.Tb14Sinking.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(0, true, false)]
    [DataRow(1, false, false)]
    [DataRow(2, true, true)]
    [DataRow(3, false, true)]
    public void OutputInversion_InvertsTheSelectedOutputs(int inversion, bool relayClosed, bool tb14Sinking)
    {
        var drive = Create(new SmVectorSettings { OutputInversion = inversion });

        drive.RelayOutputClosed.Should().Be(relayClosed);
        drive.Tb14Sinking.Should().Be(tb14Sinking);
    }

    [TestMethod]
    public void InjectTrip_None_Throws()
    {
        var drive = Create();

        drive.Invoking(d => d.InjectTrip(SmVectorTrip.None)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void InjectTrip_WhileUnpowered_DoesNothing()
    {
        var drive = Create(powered: false);

        drive.InjectTrip();

        drive.Trip.Should().Be(SmVectorTrip.None);
        drive.Mode.Should().Be(SmVectorMode.Unpowered);
    }

    [TestMethod]
    public void Changed_ReportsModeChanges()
    {
        var drive = Create();
        var changes = new System.Collections.Generic.List<string>();
        drive.Changed += changes.Add;

        Step(drive, Forward, 10);
        Step(drive, Idle, 10);

        changes.Should().Equal("drive Running: run forward", "drive Stopped: coast stop: run input removed");
    }

    [TestMethod]
    public void Settings_Validate_RejectsValuesOutsideTheManualsRanges()
    {
        var invalid = new[]
        {
            new SmVectorSettings { MaxFrequencyHz = 0 },
            new SmVectorSettings { BaseFrequencyHz = -1 },
            new SmVectorSettings { AccelerationTime = TimeSpan.FromSeconds(-1) },
            new SmVectorSettings { DecelerationTime = TimeSpan.FromSeconds(3601) },
            new SmVectorSettings { StartMethod = 1 },
            new SmVectorSettings { DcBrakeTime = TimeSpan.FromSeconds(-1) },
            new SmVectorSettings { DcBrakeTime = TimeSpan.FromSeconds(1000) },
            new SmVectorSettings { StartControlSource = -1 },
            new SmVectorSettings { StartControlSource = 7 },
            new SmVectorSettings { OutputInversion = 4 }
        };

        foreach (var settings in invalid)
        {
            settings.Invoking(s => s.Validate()).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => Create(settings)).Should().Throw<ArgumentOutOfRangeException>();
        }

        new SmVectorSettings().Invoking(s => s.Validate()).Should().NotThrow();
        new SmVectorSettings { DcBrakeTime = SmVectorSettings.ContinuousDcBrake, StartControlSource = 6 }
            .Invoking(s => s.Validate()).Should().NotThrow();
    }

    [TestMethod]
    public void Defaults_AreTheWiringDocValues()
    {
        var settings = new SmVectorSettings();

        settings.StartControlSource.Should().Be(1, "P100 = 1");
        settings.ReverseEnabled.Should().BeTrue("P112 = 1");
        settings.StartMethod.Should().Be(0, "P110 = 0");
        settings.DcBrakeTime.Should().Be(TimeSpan.Zero, "P175 factory default");
        settings.RelayOutput.Should().Be(SmVectorOutputFunction.Fault, "P140 = 3");
        settings.Tb14Output.Should().Be(SmVectorOutputFunction.Run, "P142 = 1");
        settings.OutputInversion.Should().Be(0, "P144 = 0");
        settings.StopMethod.Should().Be(SmVectorStopMethod.Coast, "P111 factory default");
        SmVectorSettings.PowerUpStartLockout.Should().Be(TimeSpan.FromSeconds(2));
    }
}
