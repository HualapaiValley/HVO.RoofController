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
/// Relay register read-back. Every relay transition is read back; a mismatch or an unreadable register is reported as
/// <see cref="RoofControllerErrorCode.RelayStateUnverified"/>, shows <see cref="RoofRelayRegisterState.Unverified"/> and
/// <see cref="RoofControllerStatus.Error"/>, and latches <see cref="RoofControllerStopReason.RelayVerificationFailed"/>.
/// Read-back proves the HAT register only, never the relay contacts; the fake models exactly that register.
/// </summary>
[TestClass]
public class RoofControllerRelayVerificationTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();
        hat.ClearMaskHistory();
        return (svc, hat);
    }

    private static void ShouldBeLatchedRelayFailure(SimulatedRoofControllerService svc, RoofRelayRegisterState expectedRegister)
    {
        svc.IsMoving.Should().BeFalse();
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.RelayRegisterState.Should().Be(expectedRegister);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        snapshot.CommandedMotion.Should().Be(RoofMotionDirection.None);
    }

    [TestMethod]
    [DataRow(true, (byte)0x01, DisplayName = "Open relay ignores its set command")]
    [DataRow(false, (byte)0x02, DisplayName = "Close relay ignores its set command")]
    public async Task DirectionRelayNotReadBack_ShouldAbortTheStart_DriveAllOff_AndLatch(bool opening, byte ignoredBit)
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        hat.Registers.IgnoredSetRelayBits = ignoredBit;

        var result = opening ? svc.Open() : svc.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Verified); // the abort's all-off did read back zero
        svc.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        hat.RelayMask.Should().Be(0x00);
        hat.EverBothDirectionBits.Should().BeFalse();

        hat.Registers.IgnoredSetRelayBits = 0;
        (opening ? svc.Open() : svc.Close()).ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task OppositeDirectionStuckOn_ShouldRefuseTheStart_WithoutEverSettingARelay()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        hat.Registers.StuckRelayBits = 0x02; // close relay reads back on

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        hat.RelayWriteLog.Should().NotContain(entry => entry.Register == 0x01, "nothing is energized unless the opposite direction verified off");
        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Unverified);
        svc.GetCurrentStatusSnapshot().RelayRegisterMask.Should().Be(0x02);
    }

    [TestMethod]
    public async Task StopPermitNotReadBack_ShouldNeverEnergizeTheDirection()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        hat.Registers.IgnoredSetRelayBits = 0x08; // STOP permit (RLY4) ignores its set command

        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        hat.RelayWriteLog.Should().NotContain(((byte)0x01, (byte)0x02), "the direction is never set without a verified STOP permit");
        hat.RelayMask.Should().Be(0x00);
        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    public async Task RelayWriteFailure_OnStart_ShouldAbortAndLatch()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        hat.Registers.FailRelayWrites = true; // the STOP permit write throws

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        hat.RelayMask.Should().Be(0x00);
        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Verified); // nothing was set, and all-off reads back zero
    }

    [TestMethod]
    public async Task StopWithARelayStuckOn_ShouldReportUnverified_AndKeepTheStopCause()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.Registers.StuckRelayBits = 0x01;

        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Unverified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop, "LastStopReason keeps the cause; the latch records the relay failure");
        svc.GetCurrentStatusSnapshot().RelayRegisterMask.Should().Be(0x01);
        hat.Registers.RelayMask.Should().Be(0x00, "the register write itself went through; only the read-back disagrees");
    }

    [TestMethod]
    public async Task StopWithAnUnreadableRegister_ShouldReportUnverified()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();
        hat.Registers.FailRelayReads = true;

        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Unverified);
        svc.GetCurrentStatusSnapshot().RelayRegisterMask.Should().BeNull();
    }

    [TestMethod]
    public async Task IdleStopWithARelayStuckOn_ShouldReportUnverified_WithoutChangingLastStopReason()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        hat.Registers.StuckRelayBits = 0x08;

        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Unverified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.None);
    }

    [TestMethod]
    public async Task SupervisionMismatchWhileMoving_RelayDroppedOut_ShouldStopAndLatch()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.SetRelaysMask(0).IsSuccessful.Should().BeTrue(); // something else cleared the register
        svc.RunSupervisionCycle();

        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Verified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task SupervisionMismatchWhileMoving_ExtraBitStuck_ShouldStopAndReportUnverified()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();

        hat.Registers.StuckRelayBits = 0x04; // clear-fault relay reads back on while closing
        svc.RunSupervisionCycle();

        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Unverified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task SupervisionMismatchWhileIdle_ShouldDriveAllOff_AndLatch()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;

        hat.SetRelay(1, true).IsSuccessful.Should().BeTrue(); // open relay energized behind the controller's back
        svc.RunSupervisionCycle();

        hat.RelayMask.Should().Be(0x00);
        ShouldBeLatchedRelayFailure(svc, RoofRelayRegisterState.Verified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.None, "no motion was commanded");
    }

    [TestMethod]
    public async Task UnverifiedRegister_IsRetriedBySupervision_ButTheLatchRemains()
    {
        var (svc, hat) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.Registers.StuckRelayBits = 0x01;
        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified, "still stuck");

        hat.Registers.StuckRelayBits = 0x00;
        svc.RunSupervisionCycle();

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        snapshot.RelayRegisterMask.Should().Be(0x00);
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed, "re-verification never clears a latch");
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task RelayFailure_ShouldTakePrecedenceOverAnEarlierLatch()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, true, false); // drive fault at startup
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);

        hat.Registers.StuckRelayBits = 0x02;
        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed,
            "relay state is unknown, which outranks the earlier fault");
    }

    [TestMethod]
    public async Task Initialize_WithTheRegisterNotVerifiedOff_ShouldFail_AndLatch()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        hat.Registers.StuckRelayBits = 0x08;
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());

        (await svc.Initialize(CancellationToken.None)).ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
    }
}
