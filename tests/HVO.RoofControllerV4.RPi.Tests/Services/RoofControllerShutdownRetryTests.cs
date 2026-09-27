using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The bounded all-off retry after an unverified shutdown stop. Supervision stops at shutdown, so this retry is the only
/// path that re-drives the relays off before the process exits. Motion stays rejected throughout, and the relay
/// verification fault stays latched even when a retry verifies.
/// </summary>
[TestClass]
public sealed class RoofControllerShutdownRetryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateOpeningAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x09);
        return (service, hat, time);
    }

    /// <summary>
    /// Waits until the retry has scheduled its next attempt (its delay timer exists), then advances to it. Returns false
    /// when the shutdown finished instead.
    /// </summary>
    private static bool AdvanceToNextRetry(ManualTimeProvider time, Task shutdown)
    {
        SpinWait.SpinUntil(() => time.ActiveTimerCount > 0 || shutdown.IsCompleted, Bound).Should().BeTrue("the retry schedules its next attempt");
        if (shutdown.IsCompleted)
        {
            return false;
        }

        time.Advance(RoofControllerServiceV4.ShutdownStopRetryInterval);
        return true;
    }

    /// <summary>Waits until the attempt started by the last advance has finished (it scheduled the next one, or ended).</summary>
    private static void WaitForAttempt(ManualTimeProvider time, Task shutdown)
        => SpinWait.SpinUntil(() => time.ActiveTimerCount > 0 || shutdown.IsCompleted, Bound).Should().BeTrue();

    [TestMethod]
    public async Task FailingShutdownWrite_ThenBusRecovery_ShouldRetryAndVerifyAllOff()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        using var _ = service;

        hat.Registers.FailRelayWrites = true;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        shutdown.IsCompleted.Should().BeFalse("the stop could not verify, so the shutdown waits for the retry");
        hat.RelayMask.Should().Be(0x09, "the failed writes left the motor relays set");
        var during = service.GetCurrentStatusSnapshot();
        during.IsShuttingDown.Should().BeTrue();
        during.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        during.LastError.Should().Contain("retrying");
        service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown, "motion stays rejected during the retry");
        service.Close().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);

        hat.Registers.FailRelayWrites = false;
        AdvanceToNextRetry(time, shutdown).Should().BeTrue();
        var result = await shutdown.WaitAsync(Bound);

        result.IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        result.Value.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        result.Value.IsFaultLatched.Should().BeTrue("the relay verification fault stays latched");
        result.Value.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        result.Value.LastError.Should().Contain("after 1 retries");
        service.LastStopReason.Should().Be(RoofControllerStopReason.HostShutdown);
        service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        time.ActiveTimerCount.Should().Be(0, "the retry ended");
    }

    [TestMethod]
    public async Task Retry_ShouldKeepTrying_UntilTheBusRecovers()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        using var _ = service;

        hat.Registers.FailRelayWrites = true;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            AdvanceToNextRetry(time, shutdown).Should().BeTrue();
            WaitForAttempt(time, shutdown);
            shutdown.IsCompleted.Should().BeFalse($"attempt {attempt + 1} failed and the window has not passed");
        }

        hat.Registers.FailRelayWrites = false;
        AdvanceToNextRetry(time, shutdown).Should().BeTrue();
        var result = await shutdown.WaitAsync(Bound);

        result.IsSuccessful.Should().BeTrue();
        result.Value.LastError.Should().Contain("after 4 retries");
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task Retry_ShouldGiveUpAfterTheWindow_AndReportTheUnresolvedState()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        using var _ = service;

        hat.Registers.StuckRelayBits = 0x01;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        var attempts = 0;
        while (AdvanceToNextRetry(time, shutdown))
        {
            attempts++;
            attempts.Should().BeLessThan(100, "the retry is bounded");
        }

        var expected = (int)(RoofControllerServiceV4.ShutdownStopRetryWindow / RoofControllerServiceV4.ShutdownStopRetryInterval);
        attempts.Should().Be(expected);
        var result = await shutdown.WaitAsync(Bound);
        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        var snapshot = service.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        snapshot.LastError.Should().Contain($"after {expected} retries").And.Contain("independent hardware stop");
        time.ActiveTimerCount.Should().Be(0, "nothing retries after the window");
        service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
    }

    [TestMethod]
    public async Task CancelledCaller_ShouldReturnUnverified_WhileTheRetryContinues_AndALaterCallShouldJoinIt()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        using var _ = service;
        hat.Registers.FailRelayWrites = true;

        using var cts = new CancellationTokenSource();
        var first = service.ShutdownAsync(cts.Token);
        await cts.CancelAsync();
        var firstResult = await first.WaitAsync(Bound);

        firstResult.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        firstResult.Error!.Message.Should().Contain("still being retried in the background");
        time.ActiveTimerCount.Should().Be(1, "the retry keeps its own schedule");

        hat.ClearRelayWriteLog();
        var second = service.ShutdownAsync(CancellationToken.None);
        hat.RelayWriteLog.Should().BeEmpty("a call during a retry joins it instead of starting another stop");

        hat.Registers.FailRelayWrites = false;
        AdvanceToNextRetry(time, second).Should().BeTrue();
        (await second.WaitAsync(Bound)).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task OperatorStop_DuringTheRetry_ShouldEndItOnceTheRegisterVerifies()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        using var _ = service;
        hat.Registers.FailRelayWrites = true;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        hat.Registers.FailRelayWrites = false;
        service.Stop().IsSuccessful.Should().BeTrue("Stop stays available until disposal");
        hat.RelayMask.Should().Be(0x00);
        hat.ClearRelayWriteLog();

        AdvanceToNextRetry(time, shutdown).Should().BeTrue();
        (await shutdown.WaitAsync(Bound)).IsSuccessful.Should().BeTrue();
        hat.RelayWriteLog.Should().BeEmpty("the register was already verified, so the retry had nothing to do");
    }

    [TestMethod]
    public async Task Dispose_ShouldEndTheRetry_AfterItsOwnAllOffAttempt()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        hat.Registers.FailRelayWrites = true;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        hat.Registers.FailRelayWrites = false;
        await service.DisposeAsync().AsTask().WaitAsync(Bound);

        hat.RelayMask.Should().Be(0x00, "disposal made its own all-off attempt");
        var result = await shutdown.WaitAsync(Bound);
        result.IsSuccessful.Should().BeTrue("the retry saw the register verified by disposal");
        time.ActiveTimerCount.Should().Be(0);
    }

    [TestMethod]
    public async Task Dispose_WhileTheBusIsStillDown_ShouldEndTheRetryPromptly_AndReportUnverified()
    {
        var (service, hat, time) = await CreateOpeningAsync();
        hat.Registers.FailRelayWrites = true;
        var shutdown = service.ShutdownAsync(CancellationToken.None);

        await service.DisposeAsync().AsTask().WaitAsync(Bound);

        var result = await shutdown.WaitAsync(Bound);
        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        time.ActiveTimerCount.Should().Be(0);
        (await service.ShutdownAsync(CancellationToken.None)).ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
    }
}
