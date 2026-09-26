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
/// ClearFault: a bounded RLY3 pulse that is serialized without queuing, preempted by Stop, released in its own finally,
/// and that clears the latch only when IN3 is inactive, the limits are not contradictory, the inputs are readable and
/// the relay register is verified all-off. Pulses run on a <see cref="ManualTimeProvider"/>.
/// </summary>
[TestClass]
public class RoofControllerClearFaultTests
{
    private const int Pulse = 100;
    private const byte ClearFaultBit = 0x04;

    /// <summary>Initializes with IN3 active (DriveFault latched at startup), then releases IN3.</summary>
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateLatchedAsync(bool releaseFault = true)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, true, false);
        var time = new ManualTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, time);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        if (releaseFault)
        {
            hat.SetInputs(true, true, false, false);
        }

        hat.ClearRelayWriteLog();
        return (svc, hat, time);
    }

    private static async Task<Result<bool>> CompletePulseAsync(Task<Result<bool>> pulse, ManualTimeProvider time, int pulseMs = Pulse)
    {
        time.Advance(TimeSpan.FromMilliseconds(pulseMs));
        return await pulse.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(RoofControllerLimits.MinClearFaultPulseMilliseconds - 1)]
    [DataRow(RoofControllerLimits.MaxClearFaultPulseMilliseconds + 1)]
    public async Task PulseOutOfBounds_ShouldBeRejectedAsInvalidRequest_WithoutTouchingRelays(int pulseMs)
    {
        var (svc, hat, _) = await CreateLatchedAsync();
        using var _ = svc;

        (await svc.ClearFault(pulseMs)).ErrorCode().Should().Be(RoofControllerErrorCode.InvalidRequest);

        hat.RelayWriteLog.Should().BeEmpty();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(RoofControllerLimits.MinClearFaultPulseMilliseconds)]
    [DataRow(RoofControllerLimits.MaxClearFaultPulseMilliseconds)]
    public async Task PulseAtTheBounds_ShouldBeAccepted(int pulseMs)
    {
        var (svc, _, time) = await CreateLatchedAsync();
        using var _ = svc;

        (await CompletePulseAsync(svc.ClearFault(pulseMs), time, pulseMs)).IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task SuccessfulClear_ShouldPulseOnlyRly3_ClearTheLatch_AndKeepLastStopReason()
    {
        var (svc, hat, time) = await CreateLatchedAsync();
        using var _ = svc;
        var lastStopReason = svc.LastStopReason;

        var pulse = svc.ClearFault(Pulse);
        hat.RelayMask.Should().Be(ClearFaultBit, "only the clear-fault relay is asserted during the pulse");
        svc.GetCurrentStatusSnapshot().IsClearFaultInProgress.Should().BeTrue();

        (await CompletePulseAsync(pulse, time)).IsSuccessful.Should().BeTrue();

        hat.RelayMask.Should().Be(0x00);
        hat.MaskHistory.Should().OnlyContain(mask => mask == 0x00 || mask == ClearFaultBit);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeFalse();
        snapshot.LatchedFaultReason.Should().BeNull();
        snapshot.LastError.Should().BeNull();
        snapshot.IsClearFaultInProgress.Should().BeFalse();
        snapshot.Status.Should().Be(RoofControllerStatus.Stopped);
        svc.LastStopReason.Should().Be(lastStopReason, "ClearFault does not rewrite motion history");
        svc.Open().IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task ClearWithNothingLatched_ShouldSucceed()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        (await CompletePulseAsync(svc.ClearFault(Pulse), time)).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task FaultInputStillActive_ShouldReturnInterlockActive_AndKeepTheLatch()
    {
        var (svc, hat, time) = await CreateLatchedAsync(releaseFault: false);
        using var _ = svc;

        (await CompletePulseAsync(svc.ClearFault(Pulse), time)).ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);

        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ConcurrentClear_ShouldBeRefusedAsOperationInProgress_NotQueued()
    {
        var (svc, _, time) = await CreateLatchedAsync();
        using var _ = svc;

        var first = svc.ClearFault(Pulse);
        var second = await svc.ClearFault(Pulse).WaitAsync(TimeSpan.FromSeconds(5));

        second.ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        (await CompletePulseAsync(first, time)).IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task Stop_ShouldPreemptThePulse_AndReleaseRly3()
    {
        var (svc, hat, _) = await CreateLatchedAsync();
        using var _ = svc;

        var pulse = svc.ClearFault(RoofControllerLimits.MaxClearFaultPulseMilliseconds);
        hat.RelayMask.Should().Be(ClearFaultBit);

        svc.Stop().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00, "Stop drives every relay off, including RLY3");

        var result = await pulse.WaitAsync(TimeSpan.FromSeconds(5)); // no time advance: the stop ended it
        result.ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        hat.RelayMask.Should().Be(0x00);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue("a preempted pulse leaves the latch unchanged");
        snapshot.IsClearFaultInProgress.Should().BeFalse();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    public async Task CallerCancellation_ShouldEndThePulse_AndReleaseRly3()
    {
        var (svc, hat, _) = await CreateLatchedAsync();
        using var _ = svc;
        using var cts = new CancellationTokenSource();

        var pulse = svc.ClearFault(RoofControllerLimits.MaxClearFaultPulseMilliseconds, cts.Token);
        hat.RelayMask.Should().Be(ClearFaultBit);
        cts.Cancel();

        (await pulse.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
    }

    [TestMethod]
    public async Task MotionAndConfigurationDuringThePulse_ShouldBeRefusedAsOperationInProgress()
    {
        var (svc, hat, time) = await CreateLatchedAsync();
        using var _ = svc;

        var pulse = svc.ClearFault(Pulse);

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        svc.UpdateConfiguration(svc.GetConfigurationSnapshot()).ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        hat.RelayMask.Should().Be(ClearFaultBit);

        (await CompletePulseAsync(pulse, time)).IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task ClearWhileMoving_ShouldBeRefusedAsOperationInProgress()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();

        (await svc.ClearFault(Pulse).WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);

        svc.IsMoving.Should().BeTrue();
        hat.RelayMask.Should().Be(0x09, "RLY3 is never asserted while moving");
    }

    [TestMethod]
    public async Task AllOffNotVerifiedBeforeThePulse_ShouldReturnRelayStateUnverified_AndNeverAssertRly3()
    {
        var (svc, hat, _) = await CreateLatchedAsync();
        using var _ = svc;
        hat.Registers.StuckRelayBits = 0x01;

        var result = await svc.ClearFault(Pulse).WaitAsync(TimeSpan.FromSeconds(5));

        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        hat.RelayWriteLog.Should().NotContain(((byte)0x01, (byte)0x03), "RLY3 is never set when all-off is not verified");
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed, "relay verification failure takes precedence");
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
    }

    [TestMethod]
    public async Task Rly3AssertNotVerified_ShouldReturnRelayStateUnverified_AndLeaveTheLatch()
    {
        var (svc, hat, _) = await CreateLatchedAsync();
        using var _ = svc;
        hat.Registers.IgnoredSetRelayBits = ClearFaultBit;

        var result = await svc.ClearFault(Pulse).WaitAsync(TimeSpan.FromSeconds(5));

        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        hat.RelayMask.Should().Be(0x00);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault, "the latch is unchanged");
    }

    [TestMethod]
    public async Task Rly3ReleaseNotVerified_ShouldLatchRelayVerificationFailed()
    {
        var (svc, hat, time) = await CreateLatchedAsync();
        using var _ = svc;

        var pulse = svc.ClearFault(Pulse);
        hat.Registers.StuckRelayBits = ClearFaultBit; // RLY3 reads back on after release

        var result = await CompletePulseAsync(pulse, time);

        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task InputReadFailureAfterThePulse_ShouldReturnHardwareUnavailable_AndLeaveTheLatch()
    {
        var (svc, hat, time) = await CreateLatchedAsync();
        using var _ = svc;

        var pulse = svc.ClearFault(Pulse);
        hat.Registers.FailNextInputReads = 1;

        (await CompletePulseAsync(pulse, time)).ErrorCode().Should().Be(RoofControllerErrorCode.HardwareUnavailable);

        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RelayVerificationFailedLatch_ShouldClear_OnceTheRegisterReverifies()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.Registers.StuckRelayBits = 0x01;
        svc.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        svc.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop, "the stop reason keeps the cause");

        hat.Registers.StuckRelayBits = 0x00;
        svc.RunSupervisionCycle(); // retries and verifies all-off
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        snapshot.Status.Should().Be(RoofControllerStatus.Error, "the latch stays until ClearFault");

        (await CompletePulseAsync(svc.ClearFault(Pulse), time)).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
    }

    [TestMethod]
    public async Task EveryLatchingReason_ShouldBeClearedOnlyByClearFault()
    {
        // StartLimitReasserted is used as the representative motion-time latch; the watchdog, IN3, read-failure,
        // relay and contradictory-limit latches have their own tests.
        var hat = new FakeRoofHat();
        hat.SetInputs(true, false, false, false); // closed
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time, opts => opts.LimitSwitchDebounce = TimeSpan.Zero);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, false);
        svc.RunSupervisionCycle();
        hat.SetInputs(true, false, false, false);
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);

        svc.Stop().IsSuccessful.Should().BeTrue();
        svc.RunSupervisionCycle();
        time.Advance(TimeSpan.FromMinutes(5));
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue("neither Stop, time nor supervision clears a latch");

        (await CompletePulseAsync(svc.ClearFault(Pulse), time)).IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
    }
}
