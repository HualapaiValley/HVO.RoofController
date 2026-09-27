using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.HostedServices;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.HostedServices;

[TestClass]
public sealed class RoofControllerServiceV4HostTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldInitialize_AndRequestVerifiedShutdownOnCancellation()
    {
        var service = new FakeRoofControllerService();
        using var host = new TestableHost(service);
        using var cts = new CancellationTokenSource();

        var executeTask = host.ExecuteForTestAsync(cts.Token);
        await WaitUntilAsync(() => service.IsInitialized, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        service.InitializationCallCount.Should().Be(1);
        service.ShutdownCallCount.Should().Be(1, "the host requests the controller's verified shutdown stop");
        service.IsServiceDisposed.Should().BeFalse("the container owns the singleton; the host never disposes it");
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldRetryInitializationUntilSuccessful()
    {
        var service = new FakeRoofControllerService();
        service.EnqueueInitialization(Result<bool>.Failure(new RoofControllerException(RoofControllerErrorCode.HardwareUnavailable, "init failed")));
        service.EnqueueInitialization(Result<bool>.Success(true));

        using var host = new TestableHost(service);
        using var cts = new CancellationTokenSource();

        var executeTask = host.ExecuteForTestAsync(cts.Token);
        await WaitUntilAsync(() => service.InitializationCallCount >= 2 && service.IsInitialized, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        service.InitializationCallCount.Should().Be(2);
        service.ShutdownCallCount.Should().Be(1);
    }

    [TestMethod]
    public async Task StopAsync_ShouldRequestShutdown_EvenWhenExecuteNeverStarted()
    {
        var service = new FakeRoofControllerService();
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(service, logger);

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        service.ShutdownCallCount.Should().Be(1);
        logger.Contains(LogLevel.Information, "shutdown stop completed").Should().BeTrue();
        logger.MessagesAt(LogLevel.Critical).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ApplicationStopping_ShouldRequestShutdownBeforeStopAsync_AndStopAsyncShouldNotRepeatAVerifiedStop()
    {
        var service = new FakeRoofControllerService();
        using var lifetime = new FakeHostApplicationLifetime();
        using var host = new TestableHost(service, lifetime: lifetime);

        lifetime.TriggerStopping();
        await WaitUntilAsync(() => service.ShutdownCallCount >= 1, TimeSpan.FromSeconds(5));

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        service.ShutdownCallCount.Should().Be(1, "a verified shutdown stop is not repeated");
    }

    [TestMethod]
    public async Task ShutdownFailure_ShouldBeLoggedCritical_AndRetriedByLaterTriggers()
    {
        var service = new FakeRoofControllerService
        {
            ShutdownBehavior = _ => Task.FromResult(Result<RoofStatusResponse>.Failure(
                new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "register read back 0x09")))
        };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var lifetime = new FakeHostApplicationLifetime();
        using var host = new TestableHost(service, logger, lifetime);

        lifetime.TriggerStopping();
        await WaitUntilAsync(() => logger.MessagesAt(LogLevel.Critical).Any(), TimeSpan.FromSeconds(5));
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        service.ShutdownCallCount.Should().Be(2, "an unverified stop is retried by the next trigger");
        logger.MessagesAt(LogLevel.Critical).Should().HaveCount(2)
            .And.OnlyContain(m => m.Contains("FAILED") && m.Contains(nameof(RoofControllerErrorCode.RelayStateUnverified)));
    }

    [TestMethod]
    public async Task ShutdownThatThrows_ShouldBeLoggedCritical_AndNotEscapeStopAsync()
    {
        var service = new FakeRoofControllerService
        {
            ShutdownBehavior = _ => throw new InvalidOperationException("I2C bus fault")
        };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(service, logger);

        var stop = () => host.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical && e.Exception is InvalidOperationException);
    }

    [TestMethod]
    public async Task ShutdownThatHangs_ShouldBeBounded_AndLoggedCritical()
    {
        var never = new TaskCompletionSource<Result<RoofStatusResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeRoofControllerService { ShutdownBehavior = _ => never.Task };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(service, logger);

        var verified = await host.ShutdownControllerAsync("test").WaitAsync(RoofControllerServiceV4Host.ShutdownTimeout + TimeSpan.FromSeconds(10));

        verified.Should().BeFalse();
        logger.Contains(LogLevel.Critical, "did not complete").Should().BeTrue();
    }

    [TestMethod]
    public async Task RealController_HostStop_ShouldDeEnergizeRelays()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var controller = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        controller.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x09);

        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(controller, logger);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        hat.RelayMask.Should().Be(0x00);
        controller.IsShuttingDown.Should().BeTrue();
        controller.LastStopReason.Should().Be(RoofControllerStopReason.HostShutdown);
        controller.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
        logger.MessagesAt(LogLevel.Critical).Should().BeEmpty();
    }

    [TestMethod]
    public async Task RealController_UnverifiableShutdownStop_ShouldBeLoggedCritical()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var controller = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        controller.Close().IsSuccessful.Should().BeTrue();

        hat.Registers.StuckRelayBits = 0x02; // the close bit will not clear in the register

        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(controller, logger);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Contains(LogLevel.Critical, "shutdown stop FAILED").Should().BeTrue();
        controller.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        controller.Status.Should().Be(RoofControllerStatus.Error);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class TestableHost : RoofControllerServiceV4Host
    {
        public TestableHost(
            IRoofControllerServiceV4 roofControllerService,
            ILogger<RoofControllerServiceV4Host>? logger = null,
            IHostApplicationLifetime? lifetime = null)
            : base(logger ?? new CapturingLogger<RoofControllerServiceV4Host>(),
                Options.Create(new RoofControllerHostOptionsV4 { RestartOnFailureWaitTime = 0 }),
                roofControllerService,
                lifetime)
        {
        }

        public Task ExecuteForTestAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void TriggerStopping() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
