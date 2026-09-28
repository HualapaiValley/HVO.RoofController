using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// The figures the hardware overview, the commissioning guide and the README quote from the emulated plant (assumed
/// mechanics: 2 m of travel at 0.1 m/s, hard stops 60 mm past each operating point). A model change that moves one
/// fails here, so the documents are updated with it.
/// </summary>
[TestClass]
public class PlantDocumentedFiguresTests
{
    private static readonly TimeSpan Travel = TimeSpan.FromSeconds(60);

    [TestMethod]
    [DataRow(SmVectorStopMethod.Coast, 0, 0d, 0.010)]
    [DataRow(SmVectorStopMethod.Ramp, 300, 0d, 0.015)]
    [DataRow(SmVectorStopMethod.CoastWithDcBrake, 500, 10d, 0.003)]
    [DataRow(SmVectorStopMethod.CoastWithDcBrake, 500, 0d, 0.010)]
    public async Task StopDistancePastTheOperatingPoint_ByStopMethod(
        SmVectorStopMethod method, int milliseconds, double dcBrakeVoltagePercent, double expectedMeters)
    {
        // With P174 = 0.0 (factory) the P175 brake period passes without braking current: the roof coasts.
        var drive = method switch
        {
            SmVectorStopMethod.Ramp => new SmVectorSettings { StopMethod = method, DecelerationTime = TimeSpan.FromMilliseconds(milliseconds) },
            SmVectorStopMethod.CoastWithDcBrake => new SmVectorSettings
            {
                StopMethod = method,
                DcBrakeTime = TimeSpan.FromMilliseconds(milliseconds),
                DcBrakeVoltagePercent = dcBrakeVoltagePercent
            },
            _ => new SmVectorSettings { StopMethod = method }
        };
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Drive = drive });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Plant.MaximumOpenOvertravel.Should().BeApproximately(expectedMeters, 0.0005);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CoastStopReversal_DropsIN4_AndStartsTheOtherWay_WhileTheRoofStillCoasts()
    {
        // With the coast stop TB-14 drops as soon as the output shuts off, so the start interlock does not hold a
        // reversal: the drive starts the other way before the coasting roof has stopped (a known limitation).
        using var h = await PlantHarness.StartAsync();
        h.Open().IsSuccessful.Should().BeTrue();
        h.RunFor(TimeSpan.FromSeconds(5));
        var commanded = h.Elapsed;

        h.Close().IsSuccessful.Should().BeTrue();
        h.RunUntil(() => h.Plant.Drive.OutputDirection == -1, TimeSpan.FromSeconds(1)).Should().BeTrue();

        var openReleased = h.CoilOffAt(1, commanded);
        (h.EventAt("IN4 LOW", commanded) - openReleased).TotalMilliseconds.Should().BeApproximately(8, 0.5);
        var reverse = h.EventAt("drive Running: run reverse", commanded) - openReleased;
        reverse.TotalMilliseconds.Should().BeApproximately(167, 0.5);

        var mechanics = h.Plant.Options.Mechanics;
        var coast = TimeSpan.FromSeconds(mechanics.SpeedAtBaseFrequency / mechanics.CoastDeceleration);
        reverse.Should().BeLessThan(coast, "the roof coasts for 0.2 s from full speed");
    }

    [TestMethod]
    [DataRow(2, 0.954)]
    [DataRow(20, 2.826)]
    public async Task StartLimitRelease_AfterTheCommand_ByAccelerationTime(int accelerationSeconds, double expectedSeconds)
    {
        // The command's relay transactions take about 90 ms of bus time before the drive runs.
        var plant = new RoofPlantOptions { Drive = new SmVectorSettings { AccelerationTime = TimeSpan.FromSeconds(accelerationSeconds) } };
        using var h = await PlantHarness.StartAsync(plant);
        var commanded = h.Elapsed;

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntil(() => !h.Plant.ClosedLimit.ContactNoClosed, TimeSpan.FromSeconds(10)).Should().BeTrue();

        var released = h.EventAt("IN2 LOW", commanded);
        (released - commanded).TotalSeconds.Should().BeApproximately(expectedSeconds, 0.005);
    }

    [TestMethod]
    public async Task WrongWayMove_FromTheClosedLimit_ReachesTheHardStop_AfterTheCommand()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Wiring = WiringFault.SwappedMotorLeads });
        var commanded = h.Elapsed;

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        var hardStop = h.Violations.First(v => v.Kind == PlantViolationKind.HardStopContact);
        (hardStop.At - commanded).TotalSeconds.Should().BeApproximately(1.50, 0.005);
    }
}
