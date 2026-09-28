using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// HAT indicator LEDs show the logical inputs (LED1 open limit, LED2 closed limit, LED3 drive fault), independent of
/// any latched fault. LED1-LED3 are put under manual control; LED4 is left automatic so the HAT shows IN4.
/// </summary>
[TestClass]
public class RoofControllerLedIndicatorTests
{
    [TestMethod]
    public async Task LedMask_ShouldReflectOpenClosedAndFaultStates()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // NC mid-travel, no fault
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.LedMask.Should().Be(0x00);

        (bool In1, bool In2, bool In3, byte Leds)[] steps =
        {
            (false, true, false, 0x01), // open limit
            (true, false, false, 0x02), // closed limit
            (true, true, true, 0x04),   // fault (this latches DriveFault; LEDs keep showing the inputs)
            (false, true, true, 0x05),  // open + fault
            (true, false, true, 0x06),  // closed + fault
            (false, false, true, 0x07), // both limits + fault (wiring error)
            (true, true, false, 0x00),  // all clear again: LEDs follow the inputs, not the latch
        };

        foreach (var (in1, in2, in3, leds) in steps)
        {
            hat.SetInputs(in1, in2, in3, false);
            svc.ForceStatusRefresh();
            hat.LedMask.Should().Be(leds, $"inputs IN1={in1} IN2={in2} IN3={in3}");
        }

        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue("the latch outlives the input that caused it");
    }

    [TestMethod]
    public async Task LedsAreOff_WhileLimitSwitchesAreIgnored()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(false, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.IgnorePhysicalLimitSwitches = true);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.ForceStatusRefresh();

        hat.LedMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task LedModes_AreSetAtInitialization_Led1To3ManualAndLed4Automatic()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes);
        RoofControllerServiceV4.IndicatorLedModes.Should().Be(0x07);
    }

    [TestMethod]
    public async Task LedModes_AreReappliedWithTheNextLedChange_AfterAHatReset()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.Registers.LedModeMask = 0x00; // the HAT reset: every LED follows its input again
        hat.SetInputs(false, true, false, false);
        svc.ForceStatusRefresh();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes);
        hat.LedMask.Should().Be(0x01);
    }

    [TestMethod]
    public async Task LedModes_AreReappliedByIdleSupervision_AfterAHatResetWithNoLedChange()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.Registers.LedModeMask = 0x00; // a supply dip between two reads: nothing failed, nothing changed
        svc.RunSupervisionCycle();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes);
        hat.LedMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task LedModes_AreLeftAlone_WhileTheyReadBackCorrectly()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.Registers.FailLedModeWrites = true;

        svc.RunSupervisionCycle();
        svc.RunSupervisionCycle();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes, "no write was needed, so the failing write was never tried");
        svc.GetCurrentStatusSnapshot().LastError.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public async Task LedModeWriteFailure_IsRetriedWithTheNextUpdate_EvenWhenTheLedsAreUnchanged()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        hat.Registers.FailLedModeWrites = true;
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.ForceStatusRefresh();
        hat.LedModeMask.Should().Be(0x00);

        hat.Registers.FailLedModeWrites = false;
        svc.ForceStatusRefresh();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes);
        hat.LedMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LedWriteFailure_IsNotRetriedWithEveryPollDuringAMove_OnlyOnceIdle(bool ledValueWritesFailToo)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        hat.Registers.FailLedModeWrites = true;
        hat.Registers.FailLedValueWrites = ledValueWritesFailToo;
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Close().IsSuccessful.Should().BeTrue();
        var attempts = hat.Registers.LedWriteAttempts;

        svc.ForceStatusRefresh();
        svc.ForceStatusRefresh();
        svc.ForceStatusRefresh();
        hat.Registers.LedWriteAttempts.Should().Be(attempts, "the LEDs did not change, so the moving roof's polls add no bus traffic");

        hat.Registers.FailLedModeWrites = false;
        hat.Registers.FailLedValueWrites = false;
        svc.Stop().IsSuccessful.Should().BeTrue();
        svc.ForceStatusRefresh();

        hat.LedModeMask.Should().Be(RoofControllerServiceV4.IndicatorLedModes, "the retry resumes once idle");
    }

    [TestMethod]
    public async Task LedModeWriteFailure_IsNotFatal()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        hat.Registers.FailLedModeWrites = true;
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.SetInputs(false, true, false, false);
        svc.ForceStatusRefresh();

        hat.LedMask.Should().Be(0x01, "the LED values are still written");
        svc.Close().IsSuccessful.Should().BeTrue("a cosmetic LED failure never blocks motion");
        svc.IsMoving.Should().BeTrue();
    }
}
