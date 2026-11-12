using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The drive fault input (IN3) active while idle is the reported last stop (Refs #257): <c>LastStopReason</c> becomes
/// <see cref="RoofControllerStopReason.DriveFault"/>, as a trip during motion records, and stays until a later motion stop.
/// The repeat-Open short-circuit and the lease and repeat refusals still read the last motion stop's own reason.
/// NC limit wiring (the test default): raw HIGH = limit not active; IN3 raw HIGH = fault.
/// </summary>
[TestClass]
public class RoofControllerIdleDriveFaultTests
{
    private const int Pulse = 100;

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateMidTravelAsync(
        Action<RoofControllerOptionsV4>? configure = null)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            configure?.Invoke(opts);
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, time);
    }

    private static async Task<Result<bool>> ClearFaultAsync(SimulatedRoofControllerService svc, ManualTimeProvider time)
    {
        var pulse = svc.ClearFault(Pulse);
        time.Advance(TimeSpan.FromMilliseconds(Pulse));
        return await pulse.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AnIdleTripAtTheLimit_IsTheLastStop_ThroughTheInputClearingAndClearFault_AndARepeatOpenStaysANoOp()
    {
        var (svc, hat, time) = await CreateMidTravelAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(false, true, false, false); // the open limit
        svc.SimForwardLimitRaw(false);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // The drive trips while the roof rests at the open limit.
        hat.SetInputs(false, true, true, false);
        svc.SimFaultRaw(true);
        var tripped = svc.GetCurrentStatusSnapshot();
        tripped.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "an idle trip is the last stop, as a trip during motion is");
        tripped.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        tripped.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "a refused start records no stop");

        // IN3 clears; the latch stays, and so does the last stop, however many supervision cycles pass.
        hat.SetInputs(false, true, false, false);
        svc.SimFaultRaw(false);
        svc.RunSupervisionCycle();
        time.Advance(TimeSpan.FromSeconds(5));
        svc.RunSupervisionCycle();
        var inputCleared = svc.GetCurrentStatusSnapshot();
        inputCleared.IsDriveFaultActive.Should().BeFalse();
        inputCleared.IsFaultLatched.Should().BeTrue();
        inputCleared.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "the input clearing is not another stop");
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "an idle Stop records no stop");

        (await ClearFaultAsync(svc, time)).IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "Clear fault keeps the last stop");
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // A repeat Open at the open limit is the no-op it was: nothing is energized and no stop is recorded.
        hat.ClearRelayWriteLog();
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.IsMoving.Should().BeFalse();
        hat.RelayWriteLog.Should().BeEmpty("the destination limit is already active");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // The next motion stop records its own reason.
        svc.Close().IsSuccessful.Should().BeTrue();
        svc.IsMoving.Should().BeTrue();
        hat.SetInputs(true, true, false, false);
        svc.SimForwardLimitRaw(true);
        hat.SetInputs(true, false, false, false); // the closed limit
        svc.SimReverseLimitRaw(false);
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task AnIdleTripBetweenTheLimits_AfterANormalStop_IsTheLastStop()
    {
        var (svc, hat, _) = await CreateMidTravelAsync();
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);

        hat.SetInputs(true, true, true, false);
        svc.SimFaultRaw(true);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        snapshot.IsOpenLimitActive.Should().BeFalse();
        snapshot.IsClosedLimitActive.Should().BeFalse();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task AnIdleTrip_FoundByAStartsRead_IsTheLastStop()
    {
        // No edge was delivered: the start's own read of the inputs finds IN3 active, refuses and latches.
        var (svc, hat, _) = await CreateMidTravelAsync();
        using var _ = svc;
        hat.SetInputs(true, true, true, false);

        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);

        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        svc.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task AnIdleTripAfterAnotherLatch_IsStillTheLastStop_AndTheFirstLatchIsKept()
    {
        var (svc, hat, time) = await CreateMidTravelAsync(opts => opts.AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(2));
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(2));
        svc.RunSupervisionCycle();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);

        hat.SetInputs(true, true, true, false);
        svc.SimFaultRaw(true);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "the drive tripped after the motion stop");
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning, "the first latched reason is kept");
    }

    [TestMethod]
    public async Task AQueuedIdleTrip_ThatClearsBeforeTheLockIsFree_IsStillTheLastStop()
    {
        var (svc, hat, _) = await CreateMidTravelAsync();
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        var (_, release, holder) = RoofControllerCommandFencingTests.HoldTheLock(svc);

        hat.SetInputs(true, true, true, false);
        await Task.Run(() => svc.SimFaultRaw(true)); // waits for the lock, then is queued
        hat.SetInputs(true, true, false, false); // and clears before the lock is free
        release.Set();
        holder.Join(TimeSpan.FromSeconds(5));

        SpinWait.SpinUntil(() => svc.GetCurrentStatusSnapshot().IsFaultLatched, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the queued edge latches once the lock is free");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        snapshot.IsDriveFaultActive.Should().BeFalse("the fresh read after the edge finds IN3 inactive");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ADriveFaultFoundWithTheDestinationLimit_IsTheLastStop_WhileTheRefusalNamesTheMotionStop(bool renewal)
    {
        // A repeat Open or a lease renewal re-reads the inputs at a passed run-loss deadline and finds the open limit and
        // IN3 both active: the limit stops the motion, then the idle evaluation latches the drive fault. The reported last
        // stop is the drive fault; the refusal still names the motion stop, as before.
        var (svc, hat, time) = await CreateMidTravelAsync(opts =>
        {
            opts.AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(2);
            opts.OperatorLeaseTimeout = TimeSpan.FromSeconds(30);
        });
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        svc.SimAtSpeedRaw(true);
        hat.SetInputs(true, true, false, false);
        svc.SimAtSpeedRaw(false);
        hat.SetInputs(false, true, true, false); // not delivered as edges
        time.Advance(RoofControllerServiceV4.RunLossConfirmationDelay);

        RoofControllerErrorCode? code;
        Exception? error;
        if (renewal)
        {
            var renewed = svc.RenewLease();
            (code, error) = (renewed.ErrorCode(), renewed.Error);
        }
        else
        {
            var repeated = svc.Open();
            (code, error) = (repeated.ErrorCode(), repeated.Error);
        }

        code.Should().Be(RoofControllerErrorCode.FaultLatched);
        error!.Message.Should().Be("Motion had passed a safety deadline (LimitSwitchReached); motion stopped and the fault is latched.");
        svc.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }
}
