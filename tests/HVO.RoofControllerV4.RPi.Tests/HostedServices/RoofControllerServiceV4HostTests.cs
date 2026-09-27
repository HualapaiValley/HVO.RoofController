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
        using var host = new TestableHost(service, logger) { ShutdownWaitTimeout = TimeSpan.FromMilliseconds(100) };

        var verified = await host.ShutdownControllerAsync("test").WaitAsync(TimeSpan.FromSeconds(10));

        verified.Should().BeFalse();
        logger.Contains(LogLevel.Critical, "did not complete").Should().BeTrue();
    }

    [TestMethod]
    public void ShutdownTimeouts_ShouldFitInsideTheHostShutdownTimeout()
    {
        // Program.cs sets HostOptions.ShutdownTimeout to 20 s (there is no shared constant). Each trigger calls again
        // after an unverified result, so up to three sequential calls can run (ApplicationStopping, StopAsync's
        // "host stop" and the end of ExecuteAsync), each for the full wait plus the abandon grace. All three must fit
        // inside the host's budget.
        var hostShutdownTimeout = TimeSpan.FromSeconds(20);
        var perCall = RoofControllerServiceV4Host.ShutdownTimeout + RoofControllerServiceV4Host.ShutdownAbandonGrace;

        TimeSpan.FromTicks(perCall.Ticks * 3).Should().BeLessThan(hostShutdownTimeout);
    }

    [TestMethod]
    public async Task ShutdownThatBlocksInSynchronousIo_ShouldBeBounded_AndLoggedCritical()
    {
        // The controller's first stop attempt is synchronous HAT I/O under its lock; a wedged bus blocks the calling thread.
        using var wedged = new ManualResetEventSlim(false);
        var service = new FakeRoofControllerService
        {
            ShutdownBehavior = _ =>
            {
                wedged.Wait();
                return Task.FromResult(Result<RoofStatusResponse>.Success(FakeRoofControllerService.HealthySnapshot()));
            }
        };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var lifetime = new FakeHostApplicationLifetime();
        using var host = new TestableHost(service, logger, lifetime) { ShutdownWaitTimeout = TimeSpan.FromMilliseconds(100) };

        try
        {
            var trigger = Task.Run(lifetime.TriggerStopping);
            await trigger.WaitAsync(TimeSpan.FromSeconds(2));   // the ApplicationStopping callback does not block
            var verified = await host.ShutdownControllerAsync("test").WaitAsync(TimeSpan.FromSeconds(10));

            verified.Should().BeFalse();
            logger.Contains(LogLevel.Critical, "did not complete").Should().BeTrue();
            logger.Contains(LogLevel.Critical, "abandoned").Should().BeTrue();
            service.ShutdownCallCount.Should().Be(1, "the second trigger shared the blocked call");
        }
        finally
        {
            wedged.Set();
        }
    }

    [TestMethod]
    public async Task TriggerAfterAnAbandonedShutdownCall_ShouldNotCallAgain_AndBeLoggedCritical()
    {
        using var wedged = new ManualResetEventSlim(false);
        var service = new FakeRoofControllerService
        {
            ShutdownBehavior = _ =>
            {
                wedged.Wait();
                return Task.FromResult(Result<RoofStatusResponse>.Success(FakeRoofControllerService.HealthySnapshot()));
            }
        };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(service, logger) { ShutdownWaitTimeout = TimeSpan.FromMilliseconds(100) };

        try
        {
            (await host.ShutdownControllerAsync("first").WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            logger.Contains(LogLevel.Critical, "abandoned").Should().BeTrue();

            var second = host.ShutdownControllerAsync("second");
            second.IsCompleted.Should().BeTrue("a trigger never waits behind a call that is still blocked");
            (await second).Should().BeFalse();
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

            service.ShutdownCallCount.Should().Be(1, "no further call is queued behind the blocked one");
            logger.Entries.Count(e => e.Level == LogLevel.Critical && e.Message.Contains("still blocked in HAT I/O")).Should().Be(2);
        }
        finally
        {
            wedged.Set();
        }
    }

    [TestMethod]
    public async Task ConcurrentTriggers_ShouldShareOneShutdownCall()
    {
        var pending = new TaskCompletionSource<Result<RoofStatusResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeRoofControllerService { ShutdownBehavior = _ => pending.Task };
        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var lifetime = new FakeHostApplicationLifetime();
        using var host = new TestableHost(service, logger, lifetime);

        lifetime.TriggerStopping();
        var stop = host.StopAsync(CancellationToken.None);
        var direct = host.ShutdownControllerAsync("test");
        await WaitUntilAsync(() => service.ShutdownCallCount >= 1, TimeSpan.FromSeconds(5));

        pending.SetResult(Result<RoofStatusResponse>.Success(FakeRoofControllerService.HealthySnapshot()));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        (await direct.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        service.ShutdownCallCount.Should().Be(1);
        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("shutdown stop completed")).Should().Be(1);
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
        using var host = new TestableHost(controller, logger) { ShutdownWaitTimeout = TimeSpan.FromMilliseconds(200) };
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Contains(LogLevel.Critical, "shutdown stop FAILED").Should().BeTrue();
        logger.Contains(LogLevel.Critical, "still being retried").Should().BeTrue();
        controller.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        controller.Status.Should().Be(RoofControllerStatus.Error);
    }

    [TestMethod]
    public async Task RealController_FailingShutdownWrite_ThenBusRecovery_ShouldCompleteVerified()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var controller = SimulatedRoofControllerService.Create(hat, time);
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        controller.Open().IsSuccessful.Should().BeTrue();
        hat.Registers.FailRelayWrites = true;

        var logger = new CapturingLogger<RoofControllerServiceV4Host>();
        using var host = new TestableHost(controller, logger);
        var stop = host.StopAsync(CancellationToken.None);

        // The first stop attempt fails; the controller schedules its all-off retry. The bus then recovers.
        await WaitUntilAsync(() => controller.GetCurrentStatusSnapshot().LastError?.Contains("retrying") == true, TimeSpan.FromSeconds(5));
        time.ActiveTimerCount.Should().Be(1, "only the retry timer remains once motion stopped");
        hat.RelayMask.Should().Be(0x09);
        hat.Registers.FailRelayWrites = false;
        time.Advance(RoofControllerServiceV4.ShutdownStopRetryInterval);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        hat.RelayMask.Should().Be(0x00);
        logger.Contains(LogLevel.Information, "verified all-off").Should().BeTrue();
        logger.MessagesAt(LogLevel.Critical).Should().BeEmpty();
        controller.Open().ErrorCode().Should().Be(RoofControllerErrorCode.ShuttingDown);
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
