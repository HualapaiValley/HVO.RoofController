using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>The relay re-read owed by a Stop whose deadline passes after the relays already verified off.</summary>
[TestClass]
public class RoofControllerStopReverificationTests
{
    [TestMethod]
    public async Task AStopWhoseSecondLedWriteIsRefusedWithoutAnOverrun_QueuesAnotherRelayReadBack()
    {
        // F29: a lane overrun would queue its own recovery and hide a missing Stop re-read. Instead, let the first LED
        // transfer finish, then suspend its caller in the cosmetic failure log until the Stop's deadline passes. The
        // second transfer must be refused before reaching the lane, so only Stop itself can queue this re-read.
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var logger = new CallbackLogger();
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), logger: logger);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.Registers.FailLedValueWrites = true;
        hat.SetInputs(true, false, false, false);
        service.ForceStatusRefresh(); // Leave a cosmetic LED retry pending, with healthy and verified-off relays.
        hat.Registers.FailLedValueWrites = false;
        hat.Registers.FailLedModeWrites = true;

        using var reread = new ManualResetEventSlim();
        var reads = 0;
        hat.Registers.BeforeRead = register =>
        {
            if (register == 0 && Interlocked.Increment(ref reads) == 2)
            {
                reread.Set();
            }
        };

        var pausedAfterFirstLed = false;
        var refusedSecondLed = false;
        var overrunWhenRefused = false;
        long? observedDeadline = null;
        logger.Callback = (message, error) =>
        {
            if (!pausedAfterFirstLed && (message.StartsWith("Failed to set the indicator LED modes", StringComparison.Ordinal)
                || message.StartsWith("Exception while setting the indicator LED modes", StringComparison.Ordinal)))
            {
                pausedAfterFirstLed = true;
                hat.Registers.FailLedModeWrites = false;
                observedDeadline = (long?)typeof(RoofControllerServiceV4)
                    .GetField("t_ioDeadline", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
                if (observedDeadline is { } deadline)
                {
                    while (Stopwatch.GetTimestamp() <= deadline)
                    {
                        Thread.Sleep(1);
                    }
                }
            }

            if (message.StartsWith("Exception while setting indicator LED mask", StringComparison.Ordinal)
                && error is HatIoOverrunException)
            {
                refusedSecondLed = true;
                overrunWhenRefused = service.IsHatIoOverrun;
            }
        };

        try
        {
            var stop = service.Stop();

            pausedAfterFirstLed.Should().BeTrue("the first LED access completed before the caller crossed the deadline");
            observedDeadline.Should().NotBeNull();
            refusedSecondLed.Should().BeTrue("the second LED access was not sent after the deadline");
            overrunWhenRefused.Should().BeFalse("no lane recovery may supply the missing re-read for Stop");
            stop.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
            reread.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("Stop must queue another relay read-back even without an I/O overrun");
            SpinWait.SpinUntil(() => service.DeferredStopCount == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();

            service.IsHatIoOverrun.Should().BeFalse();
            var status = service.GetCurrentStatusSnapshot();
            status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
            status.IsFaultLatched.Should().BeFalse("only cosmetic LED work failed; the relays read back off");
            hat.RelayMask.Should().Be(0);
        }
        finally
        {
            logger.Callback = null;
            hat.Registers.FailLedModeWrites = false;
            hat.Registers.BeforeRead = null;
        }
    }

    private sealed class CallbackLogger : ILogger<RoofControllerServiceV4>
    {
        public Action<string, Exception?>? Callback { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Callback?.Invoke(formatter(state, exception), exception);
    }
}
