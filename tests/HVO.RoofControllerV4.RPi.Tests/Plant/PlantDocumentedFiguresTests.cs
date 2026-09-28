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
    [DataRow(SmVectorStopMethod.Coast, 0, 0.010)]
    [DataRow(SmVectorStopMethod.Ramp, 300, 0.015)]
    [DataRow(SmVectorStopMethod.CoastWithDcBrake, 500, 0.003)]
    public async Task StopDistancePastTheOperatingPoint_ByStopMethod(SmVectorStopMethod method, int milliseconds, double expectedMeters)
    {
        var drive = method switch
        {
            SmVectorStopMethod.Ramp => new SmVectorSettings { StopMethod = method, DecelerationTime = TimeSpan.FromMilliseconds(milliseconds) },
            SmVectorStopMethod.CoastWithDcBrake => new SmVectorSettings { StopMethod = method, DcBrakeTime = TimeSpan.FromMilliseconds(milliseconds) },
            _ => new SmVectorSettings { StopMethod = method }
        };
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Drive = drive });

        h.Open();
        h.RunUntilStopped(Travel).Should().BeTrue();

        h.Plant.MaximumOpenOvertravel.Should().BeApproximately(expectedMeters, 0.003);
        h.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(2, 0.87)]
    [DataRow(20, 2.74)]
    public async Task StartLimitRelease_AfterTheCommand_ByAccelerationTime(int accelerationSeconds, double expectedSeconds)
    {
        var plant = new RoofPlantOptions { Drive = new SmVectorSettings { AccelerationTime = TimeSpan.FromSeconds(accelerationSeconds) } };
        using var h = await PlantHarness.StartAsync(plant);
        var commanded = h.Plant.Elapsed;

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntil(() => !h.Plant.ClosedLimit.ContactNoClosed, TimeSpan.FromSeconds(10)).Should().BeTrue();

        (h.Plant.Elapsed - commanded).TotalSeconds.Should().BeApproximately(expectedSeconds, 0.05);
    }

    [TestMethod]
    public async Task WrongWayMove_FromTheClosedLimit_ReachesTheHardStop_AfterTheCommand()
    {
        using var h = await PlantHarness.StartAsync(new RoofPlantOptions { Wiring = WiringFault.SwappedMotorLeads });
        var commanded = h.Plant.Elapsed;

        h.Open().IsSuccessful.Should().BeTrue();
        h.RunUntilStopped(Travel).Should().BeTrue();

        var hardStop = h.Violations.First(v => v.Kind == PlantViolationKind.HardStopContact);
        (hardStop.At - commanded).TotalSeconds.Should().BeApproximately(1.41, 0.05);
    }
}
