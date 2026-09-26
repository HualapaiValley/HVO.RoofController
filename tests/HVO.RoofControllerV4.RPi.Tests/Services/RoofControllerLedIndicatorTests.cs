using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// HAT indicator LEDs show the logical inputs (LED1 open limit, LED2 closed limit, LED3 drive fault), independent of
/// any latched fault.
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
}
