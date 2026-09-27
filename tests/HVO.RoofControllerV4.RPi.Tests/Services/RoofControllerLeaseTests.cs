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
/// Optional operator lease (dead-man): motion stops unless the operator renews it (RenewLease or a repeat of the same
/// Open/Close command). The lease is enforced by supervision; the watchdog stays the absolute cap. A renewal, repeat,
/// reversal or Stop that arrives after a deadline passed, but before the next supervision cycle, stops motion for that
/// deadline first: a renewal or repeat is then refused, and so is a reversal unless only the lease expired.
/// </summary>
[TestClass]
public class RoofControllerLeaseTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateAsync(TimeSpan? lease)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.OperatorLeaseTimeout = lease;
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, time);
    }

    [TestMethod]
    public async Task ExpiredLease_ShouldStopOnTheNextSupervisionCycle_WithoutLatching()
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().LeaseSecondsRemaining.Should().Be(3);

        time.Advance(Lease - TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeFalse("an expired lease is an operator stop, not a fault");
        snapshot.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        snapshot.LeaseSecondsRemaining.Should().BeNull();
        hat.RelayMask.Should().Be(0x00);
        svc.Open().IsSuccessful.Should().BeTrue("motion may be commanded again");
    }

    [TestMethod]
    public async Task RenewLease_ShouldExtendTheLease()
    {
        var (svc, _, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(2));
        var renewed = svc.RenewLease();
        renewed.IsSuccessful.Should().BeTrue();
        renewed.Value.LeaseSecondsRemaining.Should().Be(3);

        time.Advance(TimeSpan.FromSeconds(2)); // 4 s after start, 2 s after renewal
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(1));
        svc.RunSupervisionCycle();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RepeatOfTheSameCommand_ShouldRenewTheLease(bool opening)
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();

        time.Advance(TimeSpan.FromSeconds(2));
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();
        hat.RelayWriteLog.Should().BeEmpty("a repeat only renews the lease");

        time.Advance(TimeSpan.FromSeconds(2));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();
    }

    [TestMethod]
    public async Task RenewLease_ShouldNeverExtendTheWatchdog()
    {
        var (svc, _, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            svc.RenewLease().IsSuccessful.Should().BeTrue();
        }

        // Renewed at 8 s, so the lease runs to 11 s; the absolute 10 s cap fires first.
        time.Advance(TimeSpan.FromSeconds(2));
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);
    }

    [TestMethod]
    public async Task RenewLease_AfterExpiry_ShouldStopAndReturnLeaseNotActive()
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.Advance(Lease); // expired, but no supervision cycle has run yet
        svc.IsMoving.Should().BeTrue();

        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);

        svc.IsMoving.Should().BeFalse("an expired lease is never revived");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RepeatOfTheSameCommand_AfterExpiry_ShouldStopAndReturnLeaseNotActive(bool opening)
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();

        time.Advance(Lease); // expired, but no supervision cycle has run yet
        svc.IsMoving.Should().BeTrue();

        var repeat = opening ? svc.Open() : svc.Close();

        repeat.ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);
        svc.IsMoving.Should().BeFalse("a repeated command never revives an expired lease");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);

        time.Advance(TimeSpan.FromSeconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeFalse("the next supervision cycle does not restart motion");
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RepeatOfTheSameCommand_JustBeforeExpiry_ShouldRenewTheLease()
    {
        var (svc, _, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.Advance(Lease - TimeSpan.FromMilliseconds(1));
        svc.Open().IsSuccessful.Should().BeTrue();

        svc.IsMoving.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().LeaseSecondsRemaining.Should().Be(3);
    }

    [TestMethod]
    public async Task RepeatOfTheSameCommand_AfterAMissedWatchdog_ShouldStopAndLatch()
    {
        var (svc, hat, time) = await CreateAsync(lease: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.SuppressTimerCallbacks = true; // the watchdog callback is lost
        time.Advance(TimeSpan.FromSeconds(10));
        svc.IsMoving.Should().BeTrue();

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RepeatOfTheSameCommand_AfterTheAtSpeedDeadline_ShouldStopAndLatch()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(2);
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Close().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(2)); // IN4 never asserted
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RepeatOfTheSameCommand_AfterExpiry_WhenTheStopCannotVerify_ShouldReturnRelayStateUnverified()
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.Advance(Lease);
        hat.Registers.StuckRelayBits = 0x01;

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
    }

    [TestMethod]
    public async Task Reversal_AfterAMissedWatchdog_ShouldStopLatchAndBeRejected()
    {
        var (svc, hat, time) = await CreateAsync(lease: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.SuppressTimerCallbacks = true; // the watchdog callback is lost
        time.Advance(TimeSpan.FromSeconds(10));
        svc.IsMoving.Should().BeTrue();

        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00, "the reversal must not start the opposite motion");
    }

    [TestMethod]
    public async Task Reversal_AfterTheAtSpeedDeadline_ShouldStopLatchAndBeRejected()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(2);
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Close().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(2)); // IN4 never asserted
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task Reversal_AfterOnlyTheLeaseExpired_ShouldStopAndStartTheOppositeMotion()
    {
        var (svc, hat, time) = await CreateAsync(Lease);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.Advance(Lease);
        svc.IsMoving.Should().BeTrue("no supervision cycle has run yet");

        svc.Close().IsSuccessful.Should().BeTrue("an expired lease is not a fault; the reversal is a start from idle");

        svc.IsMoving.Should().BeTrue();
        hat.RelayMask.Should().Be(0x0A);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired, "the stop is recorded with the lease's reason");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeFalse();
        snapshot.CommandedMotion.Should().Be(RoofMotionDirection.Closing);
        snapshot.LeaseSecondsRemaining.Should().Be(3, "the new motion has a fresh lease");
    }

    [TestMethod]
    public async Task Stop_AfterAMissedWatchdog_ShouldRecordAndLatchTheWatchdogTimeout()
    {
        var (svc, hat, time) = await CreateAsync(lease: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.SuppressTimerCallbacks = true; // the watchdog callback is lost
        time.Advance(TimeSpan.FromSeconds(10));

        svc.Stop().IsSuccessful.Should().BeTrue("the relay register verified all-off");

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RenewLease_AfterAMissedWatchdog_ShouldStopAndLatch()
    {
        var (svc, hat, time) = await CreateAsync(TimeSpan.FromSeconds(8));
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        time.SuppressTimerCallbacks = true; // the watchdog callback is lost

        time.Advance(TimeSpan.FromSeconds(6));
        svc.RenewLease().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(4)); // 10 s: the watchdog cap has passed, the renewed lease has not

        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RenewLease_WhileIdle_ShouldReturnLeaseNotActive()
    {
        var (svc, _, _) = await CreateAsync(Lease);
        using var _ = svc;

        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);
    }

    [TestMethod]
    public async Task RenewLease_WithoutALeaseConfigured_ShouldReturnLeaseNotActive_AndNotStop()
    {
        var (svc, hat, time) = await CreateAsync(lease: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().LeaseSecondsRemaining.Should().BeNull();

        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);

        svc.IsMoving.Should().BeTrue("renewing a non-existent lease does not affect motion");
        time.Advance(TimeSpan.FromSeconds(5));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue("without a lease only the watchdog and limits stop the roof");
        hat.RelayMask.Should().Be(0x09);
    }
}
