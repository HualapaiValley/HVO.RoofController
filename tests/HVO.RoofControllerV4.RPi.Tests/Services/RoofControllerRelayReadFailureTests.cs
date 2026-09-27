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
/// Failed periodic relay register reads. One failure keeps the last verified state but reports the relay reads as
/// unhealthy; <see cref="RoofControllerServiceV4.MaxConsecutiveRelayReadFailures"/> consecutive failures stop motion and
/// latch <see cref="RoofControllerStopReason.RelayVerificationFailed"/>, or, when idle, re-run the all-off sequence. This
/// threshold is separate from <see cref="RoofControllerOptionsV4.MaxConsecutiveInputReadFailures"/>.
/// </summary>
[TestClass]
public class RoofControllerRelayReadFailureTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var time = new ManualTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            opts.MaxConsecutiveInputReadFailures = 5;
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, time);
    }

    private static void Cycle(SimulatedRoofControllerService svc, ManualTimeProvider time)
    {
        time.Advance(TimeSpan.FromMilliseconds(500));
        svc.RunSupervisionCycle();
    }

    [TestMethod]
    public void Threshold_ShouldBeTwo()
    {
        RoofControllerServiceV4.MaxConsecutiveRelayReadFailures.Should().Be(2);
    }

    [TestMethod]
    public async Task Healthy_AfterInitialize()
    {
        var (svc, _, _) = await CreateAsync();
        using var _ = svc;

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeTrue();
        snapshot.ConsecutiveRelayReadFailures.Should().Be(0);
        snapshot.LastSuccessfulRelayReadUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task OneFailedReadWhileMoving_ShouldKeepMoving_AndReportUnhealthyReads()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);

        svc.IsMoving.Should().BeTrue("one failed read is tolerated");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeFalse();
        snapshot.ConsecutiveRelayReadFailures.Should().Be(1);
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified, "the last verified state is retained");
        snapshot.LastError.Should().Contain("Relay register read failed");
        hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task OneFailedReadWhileMoving_ThenARecoveredRead_ShouldResetTheCount()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        hat.Registers.FailRelayReads = false;
        Cycle(svc, time);

        svc.IsMoving.Should().BeTrue();
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeTrue();
        snapshot.ConsecutiveRelayReadFailures.Should().Be(0);

        // Non-consecutive failures never reach the threshold.
        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        svc.IsMoving.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().ConsecutiveRelayReadFailures.Should().Be(1);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConsecutiveFailedReadsWhileMoving_ShouldStopAndLatch(bool opening)
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();

        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        svc.IsMoving.Should().BeTrue();
        Cycle(svc, time);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified, "the stop's read-back failed too");
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        snapshot.RelayRegisterReadsHealthy.Should().BeFalse();
        snapshot.ConsecutiveRelayReadFailures.Should().BeGreaterThanOrEqualTo(2);
        hat.RelayMask.Should().Be(0x00, "the all-off writes still reach the register");
        (opening ? svc.Open() : svc.Close()).ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task ConsecutiveFailedReadsWhileMoving_ThenRecovery_ShouldReverifyAllOff_ButKeepTheLatch()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        Cycle(svc, time);
        svc.IsMoving.Should().BeFalse();

        hat.Registers.FailRelayReads = false;
        Cycle(svc, time);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        snapshot.RelayRegisterMask.Should().Be(0);
        snapshot.RelayRegisterReadsHealthy.Should().BeTrue();
        snapshot.ConsecutiveRelayReadFailures.Should().Be(0);
        snapshot.IsFaultLatched.Should().BeTrue("only ClearFault resets the latch");
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task ConsecutiveFailedReadsWhileMoving_StopBeforeTheInputThreshold()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        // The whole bus is down: input and relay reads both fail. Inputs tolerate 5 failures, relay reads 2.
        hat.Registers.FailInputReads = true;
        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        Cycle(svc, time);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        svc.GetCurrentStatusSnapshot().ConsecutiveInputReadFailures.Should().Be(2);
    }

    [TestMethod]
    public async Task OneFailedReadWhileIdle_ShouldReportUnhealthyReads_WithoutLatching()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;

        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterReadsHealthy.Should().BeFalse();
        snapshot.IsFaultLatched.Should().BeFalse();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);

        hat.Registers.FailRelayReads = false;
        Cycle(svc, time);
        svc.GetCurrentStatusSnapshot().RelayRegisterReadsHealthy.Should().BeTrue();
    }

    [TestMethod]
    public async Task ConsecutiveFailedReadsWhileIdle_ShouldDriveAllOff_AndLatchWhenItCannotVerify()
    {
        var (svc, hat, time) = await CreateAsync();
        using var _ = svc;
        hat.ClearRelayWriteLog();

        hat.Registers.FailRelayReads = true;
        Cycle(svc, time);
        hat.RelayWriteLog.Should().BeEmpty();
        Cycle(svc, time);

        hat.RelayWriteLog.Should().NotBeEmpty("the all-off sequence runs at the threshold");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.None, "no motion was stopped");
    }

    [TestMethod]
    public async Task ReadsThatAreNotRepeated_ShouldBecomeStale()
    {
        var (svc, _, time) = await CreateAsync();
        using var _ = svc;
        svc.GetCurrentStatusSnapshot().RelayRegisterReadsHealthy.Should().BeTrue();

        // No supervision cycle for longer than the staleness limit (5 s with the default 1 s cadence).
        time.Advance(RoofControllerServiceV4.MinimumReadStaleness + TimeSpan.FromMilliseconds(1));
        svc.GetCurrentStatusSnapshot().RelayRegisterReadsHealthy.Should().BeFalse();

        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().RelayRegisterReadsHealthy.Should().BeTrue();
    }
}
