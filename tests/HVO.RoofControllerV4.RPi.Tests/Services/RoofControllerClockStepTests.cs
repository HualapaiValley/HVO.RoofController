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
/// Controller time is monotonic: deadlines and read staleness follow the elapsed time, not the wall clock, so a wall-clock
/// step neither keeps an expired lease alive nor stops motion early. Published timestamps are still wall-clock UTC.
/// </summary>
[TestClass]
public class RoofControllerClockStepTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, SteppingWallClockTimeProvider Clock)> CreateAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var clock = new SteppingWallClockTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, clock, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(120);
            opts.OperatorLeaseTimeout = Lease;
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, clock);
    }

    [TestMethod]
    public async Task Lease_ShouldExpireOnTime_AfterTheWallClockStepsBack()
    {
        var (svc, hat, clock) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.StepWallClock(TimeSpan.FromSeconds(-60));
        svc.GetCurrentStatusSnapshot().LastTransitionUtc.Should().Be(clock.GetUtcNow() - TimeSpan.FromSeconds(1),
            "published timestamps are wall-clock UTC with their real age");

        clock.Advance(Lease - TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeFalse("the lease runs on elapsed time, so stepping the wall clock back does not extend it");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task Lease_ShouldNotExpireEarly_WhenTheWallClockStepsForward()
    {
        var (svc, hat, clock) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.StepWallClock(TimeSpan.FromHours(1));
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeTrue("neither the lease nor the watchdog has run out in elapsed time");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LeaseSecondsRemaining.Should().Be(2);
        snapshot.WatchdogSecondsRemaining.Should().Be(119);
        snapshot.IsFaultLatched.Should().BeFalse();
        hat.RelayMask.Should().Be(0x09);

        clock.Advance(Lease - TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
    }

    [TestMethod]
    public async Task ReadStaleness_ShouldIgnoreWallClockSteps()
    {
        var (svc, _, clock) = await CreateAsync();
        using var _ = svc;
        svc.RunSupervisionCycle();
        var version = svc.GetCurrentStatusSnapshot().StatusVersion;

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.StepWallClock(TimeSpan.FromHours(1));
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeTrue("the last read is 1 s old however far the wall clock moved");
        snapshot.InputsHealthy.Should().BeTrue();
        snapshot.LastSuccessfulRelayReadUtc.Should().Be(clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        snapshot.LastSuccessfulInputReadUtc.Should().Be(clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        snapshot.SnapshotUtc.Should().Be(clock.GetUtcNow());
        snapshot.StatusVersion.Should().Be(version, "a wall-clock step alone is not a status change");

        clock.StepWallClock(TimeSpan.FromHours(-2));
        clock.Advance(RoofControllerServiceV4.MinimumReadStaleness - TimeSpan.FromSeconds(1));
        snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeTrue();
        snapshot.InputsHealthy.Should().BeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeFalse("the reads are stale after the staleness limit of elapsed time");
        snapshot.InputsHealthy.Should().BeFalse();
    }
}
