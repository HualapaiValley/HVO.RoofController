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
/// Destination limits and contradictory limit inputs, driven by direct reads (deterministic).
/// </summary>
[TestClass]
public class RoofControllerLimitEdgeTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateMidTravelAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(30));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat);
    }

    [TestMethod]
    public async Task StopAfterOpenLimitReached_ShouldRemainOpen()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        hat.SetInputs(false, true, false, false); // open limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse("reaching the destination is not a fault");
        hat.RelayMask.Should().Be(0x00);

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Open);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
    }

    [TestMethod]
    public async Task StopAfterClosedLimitReached_ShouldRemainClosed()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;

        svc.Close().IsSuccessful.Should().BeTrue();

        hat.SetInputs(true, false, false, false); // closed limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        hat.RelayMask.Should().Be(0x00);

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task DestinationEdge_ShouldStopImmediately_WithoutDebounce()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;
        svc.UpdateConfiguration(svc.GetConfigurationSnapshot() with { LimitSwitchDebounce = TimeSpan.FromMilliseconds(500) })
            .IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.SimForwardLimitRaw(false); // open limit edge

        svc.IsMoving.Should().BeFalse("the debounce never delays a stop");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task BothLimitsEngagedWhileMoving_ShouldStopLatch_AndRemainErrorAfterStop()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(false, false, false, false);
        svc.ForceStatusRefresh();

        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        hat.RelayMask.Should().Be(0x00);

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Error);

        // The latch outlives the inputs: releasing both limits does not clear it.
        hat.SetInputs(true, true, false, false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task ContradictoryLimitsWhileIdle_ShouldLatch()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;

        hat.SetInputs(false, false, false, false);
        svc.RunSupervisionCycle();

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ContradictoryLimits_ShouldBlockClearFault_UntilResolved()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var refused = svc.ClearFault(100);
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await refused.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();

        hat.SetInputs(true, false, false, false); // wiring repaired: closed
        var cleared = svc.ClearFault(100);
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await cleared.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task OpenWhenAlreadyOpen_ShouldSucceedWithoutEnergizing()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(false, true, false, false); // open limit
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();

        var result = svc.Open();

        result.IsSuccessful.Should().BeTrue();
        result.Value.Should().Be(RoofControllerStatus.Open);
        svc.IsMoving.Should().BeFalse();
        hat.RelayWriteLog.Should().BeEmpty("nothing is energized when the destination limit is already active");
    }
}
