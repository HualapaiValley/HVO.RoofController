using System;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.HealthChecks;

[TestClass]
public sealed class RoofControllerAvailabilityCheckTests
{
    private static Task<HealthCheckResult> CheckAsync(RoofStatusResponse snapshot, bool disposed = false)
        => new RoofControllerAvailabilityCheck(
            new FakeRoofControllerService { Snapshot = snapshot, IsServiceDisposed = disposed },
            NullLogger<RoofControllerHealthCheck>.Instance, Options.Create(new RoofControllerOptionsV4()))
            .CheckHealthAsync(new HealthCheckContext());

    private static RoofStatusResponse PlantFault => FakeRoofControllerService.HealthySnapshot() with
    {
        Status = RoofControllerStatus.Error,
        IsFaultLatched = true,
        LatchedFaultReason = RoofControllerStopReason.DriveFault
    };

    [TestMethod]
    public async Task OnlyTheExplicitPlantFaultReasons_AreAvailable_AndOperationalHealthStillFails()
    {
        foreach (var reason in Enum.GetValues<RoofControllerStopReason>())
        {
            var snapshot = PlantFault with { LatchedFaultReason = reason };
            var expected = reason is RoofControllerStopReason.DriveFault or RoofControllerStopReason.DriveNotRunning
                or RoofControllerStopReason.ContradictoryLimitInputs or RoofControllerStopReason.StartLimitReasserted
                or RoofControllerStopReason.DepartureLimitNotReleased ? HealthStatus.Degraded : HealthStatus.Unhealthy;
            (await CheckAsync(snapshot)).Status.Should().Be(expected, "fault {0}", reason);
            var operational = new RoofControllerHealthCheck(new FakeRoofControllerService { Snapshot = snapshot },
                NullLogger<RoofControllerHealthCheck>.Instance, Options.Create(new RoofControllerOptionsV4()));
            (await operational.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
        }

        (await CheckAsync(PlantFault with { LatchedFaultReason = null })).Status.Should().Be(HealthStatus.Unhealthy);
        (await CheckAsync(PlantFault with { LatchedFaultReason = (RoofControllerStopReason)999 })).Status.Should().Be(HealthStatus.Unhealthy);
        (await CheckAsync(PlantFault)).Description.Should().Contain("DriveFault").And.Contain("ClearFault");
    }

    [TestMethod]
    public async Task APlantLatchNeverHidesLifecycleInputOrRelayFailures_OrUnsafeMotion()
    {
        RoofStatusResponse[] failures =
        [
            PlantFault with { IsInitialized = false },
            PlantFault with { IsShuttingDown = true },
            PlantFault with { InputsHealthy = false }, // failed or stale input reads, including an unreachable HAT
            PlantFault with { RelayRegisterReadsHealthy = false }, // failed or stale relay reads
            PlantFault with { RelayRegisterState = RoofRelayRegisterState.Unverified },
            PlantFault with { RelayRegisterState = RoofRelayRegisterState.Unknown },
            PlantFault with { RelayRegisterMask = null },
            PlantFault with { RelayRegisterMask = 1 },
            PlantFault with { IsMoving = true },
            PlantFault with { CommandedMotion = RoofMotionDirection.Opening },
            PlantFault with { IsClearFaultInProgress = true }
        ];
        foreach (var failure in failures)
        {
            (await CheckAsync(failure)).Status.Should().Be(HealthStatus.Unhealthy, "snapshot {0}", failure);
        }
        (await CheckAsync(PlantFault, disposed: true)).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [TestMethod]
    public async Task AnUnclassifiedErrorFails_ButNormalMotionRemainsAvailable()
    {
        var healthy = FakeRoofControllerService.HealthySnapshot();
        (await CheckAsync(healthy with { Status = RoofControllerStatus.Error })).Status.Should().Be(HealthStatus.Unhealthy);
        (await CheckAsync(healthy with { RelayRegisterState = RoofRelayRegisterState.Unknown })).Status.Should().Be(HealthStatus.Unhealthy);
        (await CheckAsync(healthy with
        {
            Status = RoofControllerStatus.Opening, IsMoving = true,
            CommandedMotion = RoofMotionDirection.Opening, RelayRegisterMask = 9
        })).Status.Should().Be(HealthStatus.Healthy);
    }
}
