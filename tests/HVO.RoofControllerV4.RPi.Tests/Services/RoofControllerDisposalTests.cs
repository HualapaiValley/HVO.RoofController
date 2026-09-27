using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
public sealed class RoofControllerDisposalTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateInitializedAsync(bool backgroundSupervision = false)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var service = SimulatedRoofControllerService.Create(hat, backgroundSupervision ? null : new ManualTimeProvider(), backgroundSupervision: backgroundSupervision);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (service, hat);
    }

    [TestMethod]
    public async Task DisposeAsync_ShouldStopMotion_AndRejectFurtherCommandsAsShuttingDown()
    {
        var (service, hat) = await CreateInitializedAsync(backgroundSupervision: true);
        service.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x09);

        await service.DisposeAsync();

        service.IsServiceDisposed.Should().BeTrue();
        service.IsShuttingDown.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00, "disposal drives every relay off");
        service.LastStopReason.Should().Be(RoofControllerStopReason.SystemDisposal);

        var result = service.Open();
        result.ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        result.Error!.InnerException.Should().BeOfType<ObjectDisposedException>();
        service.Close().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        service.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        service.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        (await service.ClearFault(100)).ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        service.UpdateConfiguration(service.GetConfigurationSnapshot()).ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        (await service.Initialize(CancellationToken.None)).ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
    }

    [TestMethod]
    public void Dispose_BeforeInitialize_ShouldMatchAsyncDisposeBehavior()
    {
        var hat = new FakeRoofHat();
        var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());

        service.Dispose();
        service.Dispose(); // idempotent

        service.IsServiceDisposed.Should().BeTrue();
        var result = service.Close();
        result.ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        result.Error!.InnerException.Should().BeOfType<ObjectDisposedException>();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ShutdownAsync_ShouldBeIdempotent_AndDisposeAfterShutdownShouldBeClean()
    {
        var (service, hat) = await CreateInitializedAsync();
        service.Close().IsSuccessful.Should().BeTrue();

        var first = await service.ShutdownAsync(CancellationToken.None);
        var second = await service.ShutdownAsync(CancellationToken.None);

        first.IsSuccessful.Should().BeTrue();
        second.IsSuccessful.Should().BeTrue();
        first.Value.IsShuttingDown.Should().BeTrue();
        first.Value.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        service.LastStopReason.Should().Be(RoofControllerStopReason.HostShutdown);
        hat.RelayMask.Should().Be(0x00);
        service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        service.Stop().IsSuccessful.Should().BeTrue("Stop remains available until disposal");

        await service.DisposeAsync();
        (await service.ShutdownAsync(CancellationToken.None)).IsSuccessful.Should().BeTrue("a verified shutdown stays verified after disposal");
    }

    [TestMethod]
    public async Task ShutdownAsync_WithUnverifiableRegister_ShouldFailWithRelayStateUnverified()
    {
        var (service, hat) = await CreateInitializedAsync();
        using var _ = service;
        service.Open().IsSuccessful.Should().BeTrue();
        hat.Registers.StuckRelayBits = 0x01;

        // A cancelled wait returns at once; the bounded retry is covered by RoofControllerShutdownRetryTests.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await service.ShutdownAsync(cts.Token);

        result.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        var snapshot = service.GetCurrentStatusSnapshot();
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.HostShutdown, "the stop reason keeps the cause");
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
    }

    [TestMethod]
    public async Task DisposeRacingOpen_ShouldNeverLeaveRelaysEnergized_AndShouldBeBounded()
    {
        // QA-15: whichever side wins, the register ends all-off and no command is admitted after disposal.
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var (service, hat) = await CreateInitializedAsync(backgroundSupervision: iteration % 2 == 0);
            using var start = new ManualResetEventSlim(false);

            var opener = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 20; i++)
                {
                    var result = i % 2 == 0 ? service.Open() : service.Close();
                    if (!result.IsSuccessful)
                    {
                        result.ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
                    }
                }
            });

            var disposer = Task.Run(async () =>
            {
                start.Wait();
                await service.DisposeAsync();
            });

            start.Set();
            await Task.WhenAll(opener, disposer).WaitAsync(TimeSpan.FromSeconds(10));

            service.IsServiceDisposed.Should().BeTrue();
            hat.RelayMask.Should().Be(0x00, $"iteration {iteration}: relays must be off after disposal whatever the interleaving");
            hat.EverBothDirectionBits.Should().BeFalse();
            service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        }
    }

    [TestMethod]
    public async Task DisposeFromStatusChangedHandler_ShouldNotDeadlock()
    {
        var (service, hat) = await CreateInitializedAsync(backgroundSupervision: true);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, args) =>
        {
            if (args.Status.Status == RoofControllerStatus.Opening)
            {
                service.Dispose();
                disposed.TrySetResult();
            }
        };

        service.Open().IsSuccessful.Should().BeTrue();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.IsServiceDisposed.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ClearFaultPulse_InterruptedByDisposal_ShouldReleaseTheRelay()
    {
        var (service, hat) = await CreateInitializedAsync(); // manual time: the pulse never elapses on its own

        var clear = service.ClearFault(RoofControllerLimits.MaxClearFaultPulseMilliseconds);
        hat.RelayMask.Should().Be(0x04, "the clear-fault relay is asserted during the pulse");

        await service.DisposeAsync();
        var result = await clear.WaitAsync(TimeSpan.FromSeconds(5));

        result.ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task Disposal_ShouldReleaseTimers()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();
        time.ActiveTimerCount.Should().Be(1);

        await service.DisposeAsync();

        time.ActiveTimerCount.Should().Be(0);
        Enumerable.Range(0, 3).ToList().ForEach(_ => time.Advance(TimeSpan.FromMinutes(1)));
        hat.RelayMask.Should().Be(0x00);
    }
}
