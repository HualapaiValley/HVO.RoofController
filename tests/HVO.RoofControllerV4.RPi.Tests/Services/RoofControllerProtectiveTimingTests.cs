using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>Slow routine I/O must yield to an edge; idle protective all-off sequences share the stop bound (#231).</summary>
[TestClass]
[DoNotParallelize]
public class RoofControllerProtectiveTimingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AStopDecision_DoesNotExemptRoutineLedsFromAnOverdueForeignDeadline()
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", flags)!.GetValue(service)!;
        var stop = new Thread(() => service.Stop());
        try
        {
            lock (sync)
            {
                stop.Start();
                stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                // Pause a caller after it decided a stop and after UpdateIndicatorLeds checked for waiting work.
                // An overdue foreign request is now visible at HatCall; the cosmetic transfer still must yield.
                using var section = (IDisposable)typeof(RoofControllerServiceV4).GetMethod("BoundedSection", flags)!
                    .Invoke(service, [null, true, null])!;
                var writes = 0;
                hat.Registers.BeforeLedWrite = (_, _) => writes++;
                typeof(RoofControllerServiceV4).GetMethod("ApplyIndicatorLedModes_NoLock", flags)!.Invoke(service, null);
                writes.Should().Be(0, "only protective relay-off transfers may retain the caller's own deadline");
            }
        }
        finally
        {
            hat.Registers.BeforeLedWrite = null;
            if (stop.ThreadState != System.Threading.ThreadState.Unstarted) stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            WaitForVerifiedOff(service, hat);
        }
    }

    [TestMethod]
    [DataRow("Stop", "limit")]
    [DataRow("Stop", "drive fault")]
    [DataRow("edge", "limit")]
    [DataRow("edge", "drive fault")]
    [DataRow("Stop", "watchdog")]
    [DataRow("Stop", "renewal")]
    [DataRow("Stop", "repeat Close")]
    public async Task AnOverdueForeignRequest_DoesNotSuppressANewProtectiveStop(string queued, string trigger)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var service = SimulatedRoofControllerService.Create(hat, time, configure: options =>
        {
            if (trigger is "renewal" or "repeat Close") options.OperatorLeaseTimeout = TimeSpan.FromSeconds(2);
        });
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        Exception? queuedError = null;
        var first = new Thread(() =>
        {
            try
            {
                if (queued == "Stop") service.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
                else service.SimReverseLimitRaw(false);
            }
            catch (Exception error) { queuedError = error; }
        });
        try
        {
            lock (sync)
            {
                // Keep the queued worker behind this monitor after its caller's arrival deadline has passed.
                first.Start();
                first.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                queuedError.Should().BeNull();
                if (queued == "edge") service.SimReverseLimitRaw(true); // contact chatter before re-assertion
                if (trigger is "renewal" or "repeat Close") time.AdvanceWithoutTimers(TimeSpan.FromSeconds(2));
                hat.ClearRelayWriteLog();
                if (trigger == "limit") service.SimReverseLimitRaw(false);
                else if (trigger == "drive fault") service.SimFaultRaw(true);
                else if (trigger == "watchdog") service.TriggerWatchdog(service.CurrentMotionGeneration);
                else if (trigger == "renewal") service.RenewLease();
                else service.Close();

                // Assert before releasing the monitor: a queued cleanup cannot supply the missing writes/read-back.
                hat.RelayWriteLog.Count.Should().Be(4, "this protective caller must send its own all-off writes");
                hat.RelayMask.Should().Be(0);
                var status = service.GetCurrentStatusSnapshot();
                status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
                status.LastStopReason.Should().Be(trigger switch
                {
                    "limit" => RoofControllerStopReason.LimitSwitchReached,
                    "drive fault" => RoofControllerStopReason.DriveFault,
                    "watchdog" => RoofControllerStopReason.SafetyWatchdogTimeout,
                    _ => RoofControllerStopReason.OperatorLeaseExpired
                });
                status.IsFaultLatched.Should().Be(trigger is "drive fault" or "watchdog");
                status.LatchedFaultReason.Should().NotBe(RoofControllerStopReason.RelayVerificationFailed);
                if (trigger == "drive fault") status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
            }
        }
        finally
        {
            if (first.ThreadState != System.Threading.ThreadState.Unstarted) first.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            WaitForVerifiedOff(service, hat);
        }
    }

    [TestMethod]
    public async Task AStopThatHasReleasedTheMonitor_DoesNotBoundTheNextRoutineRetry()
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        var retried = false;
        var overran = false;
        var verified = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi" && instrument.Name == "roof.controller.commands")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var isStop = false;
            foreach (var tag in tags)
                if (tag.Key == "roof.command" && tag.Value?.ToString() == "stop") isStop = true;
            if (!isStop || retried) return;
            retried = true;
            // Command telemetry runs after Stop releases the monitor but before Stop returns. A new cycle must
            // already have its routine allowance, even while that completed caller is still on the stack.
            hat.Registers.FailRelayReads = true;
            service.ForceRelayStates(false, false, false);
            hat.Registers.FailRelayReads = false;
            hat.Registers.BeforeRelayWrite = (register, _) => { if (register == 2) Thread.Sleep(600); };
            try
            {
                service.RunSupervisionCycle();
                overran = service.IsHatIoOverrun;
                verified = service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified;
            }
            finally { hat.Registers.BeforeRelayWrite = null; }
        });
        listener.Start();
        try
        {
            service.Stop().IsSuccessful.Should().BeTrue();
            retried.Should().BeTrue();
            overran.Should().BeFalse("the completed Stop request must be removed before the monitor is released");
            verified.Should().BeTrue();
        }
        finally
        {
            hat.Registers.FailRelayReads = false;
            hat.Registers.BeforeRelayWrite = null;
            WaitForVerifiedOff(service, hat);
        }
    }

    [TestMethod]
    public async Task AQueuedAllOff_RetainsItsOriginalDeadline_WhenAnotherCycleWinsTheMonitor()
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        hat.Registers.BeforeRelayWrite = (register, _) => { if (register == 2) Thread.Sleep(900); };
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        try
        {
            lock (sync)
            {
                service.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
                SpinWait.SpinUntil(() => !service.IsHatIoOverrun, TimeSpan.FromSeconds(5)).Should().BeTrue();
                // Both the bound's continuation and the lane recovery are waiting for this monitor. Its original
                // deadline has passed; a routine retry must yield now, rather than take a new two-second allowance.
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    service.RunSupervisionCycle();
                    elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(300));
                }
                finally { hat.Registers.BeforeRelayWrite = null; }
            }
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            WaitForVerifiedOff(service, hat);
        }
    }

    [TestMethod]
    [DataRow("limit")]
    [DataRow("drive fault")]
    public async Task UniformlySlowIo_CutsTheEdgeAtItsArrivalBound_LatchesUnverifiedStop_ThenVerifies(string trigger)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        PendingLedRetry(service);
        using var firstRead = new ManualResetEventSlim();
        hat.Registers.BeforeRead = _ => { firstRead.Set(); Thread.Sleep(1400); };
        hat.Registers.BeforeRelayWrite = (_, _) => Thread.Sleep(1400);
        hat.Registers.BeforeLedWrite = (_, _) => Thread.Sleep(1400);
        Task? cycle = null;
        Task? edge = null;
        try
        {
            cycle = Task.Run(service.RunSupervisionCycle);
            firstRead.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await Task.Delay(1600);
            var elapsed = Stopwatch.StartNew();
            edge = Task.Run(() =>
            {
                if (trigger == "limit") service.SimReverseLimitRaw(false);
                else service.SimFaultRaw(true);
            });
            await edge.WaitAsync(TimeSpan.FromSeconds(5));
            var edgeReturnedAfter = elapsed.Elapsed;
            edgeReturnedAfter.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));

            // Every transfer remains slow, including recovery. This characterizes an unverified stop rather than
            // the routine-read/LED contention tests, whose protective writes and verification are healthy and fast.
            (await Task.Run(() => SpinWait.SpinUntil(() => !service.IsHatIoOverrun && service.DeferredStopCount == 0
                && service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0,
                TimeSpan.FromSeconds(30)))).Should().BeTrue("queued all-off work must eventually verify even on the slow bus");
            TestContext.WriteLine("{0}, uniformly 1400 ms I/O: edge returned {1:F0} ms, all-off verified {2:F0} ms after arrival",
                trigger, edgeReturnedAfter.TotalMilliseconds, elapsed.Elapsed.TotalMilliseconds);
            var status = service.GetCurrentStatusSnapshot();
            status.LastStopReason.Should().Be(trigger == "limit" ? RoofControllerStopReason.LimitSwitchReached : RoofControllerStopReason.DriveFault);
            status.IsFaultLatched.Should().BeTrue();
            status.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        }
        finally
        {
            hat.Registers.BeforeRead = null;
            hat.Registers.BeforeRelayWrite = null;
            hat.Registers.BeforeLedWrite = null;
            if (cycle is not null) await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            if (edge is not null) await edge.WaitAsync(TimeSpan.FromSeconds(10));
            WaitForVerifiedOff(service, hat);
        }
    }

    [TestMethod]
    [DataRow("limit")]
    [DataRow("drive fault")]
    [DataRow("Stop")]
    public async Task AFenceBeforeTheCyclePublishesItsSection_IsNotLost(string trigger)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        PendingLedRetry(service);
        hat.Registers.BeforeRead = _ => Thread.Sleep(1400);
        hat.Registers.BeforeLedWrite = (_, _) => Thread.Sleep(1400);
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        var before = service.CommandGeneration;
        var elapsed = new Stopwatch();
        Exception? edgeError = null;
        var edge = new Thread(() =>
        {
            try
            {
                elapsed.Start();
                if (trigger == "Stop") service.Stop();
                else if (trigger == "limit") service.SimReverseLimitRaw(false);
                else service.SimFaultRaw(true);
            }
            catch (Exception error) { edgeError = error; }
        });
        try
        {
            lock (sync)
            {
                // Model the cycle after monitor acquisition but before opening its HAT section. The edge raises its
                // fence and reaches the monitor wait first; a reentrant cycle resumes the interrupted work deterministically.
                edge.Start();
                SpinWait.SpinUntil(() => service.CommandGeneration > before
                    && (edge.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(1))
                    .Should().BeTrue("the protective caller has fenced and is waiting for the cycle's monitor");
                try { service.RunSupervisionCycle(); }
                finally
                {
                    // Protective relay transfers are fast; only the cycle's routine I/O is degraded.
                    hat.Registers.BeforeRead = null;
                    hat.Registers.BeforeLedWrite = null;
                }
            }

            SpinWait.SpinUntil(() => hat.RelayMask == 0, TimeSpan.FromSeconds(8)).Should().BeTrue();
            var offTime = elapsed.Elapsed;
            TestContext.WriteLine("{0} before section: relays off {1:F0} ms after arrival", trigger, offTime.TotalMilliseconds);
            offTime.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            hat.Registers.BeforeRead = null;
            hat.Registers.BeforeLedWrite = null;
            if (edge.ThreadState != System.Threading.ThreadState.Unstarted) edge.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            WaitForVerifiedOff(service, hat);
        }

        edgeError.Should().BeNull();
        var status = service.GetCurrentStatusSnapshot();
        status.IsMoving.Should().BeFalse();
        status.IsFaultLatched.Should().Be(trigger == "drive fault");
        if (trigger == "drive fault") status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task RecoveryAfterTheStopCutAnAccess_DoesNotClaimTheNormal1500MsLimitWasExceeded()
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), logger: logger);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.Registers.BeforeRelayWrite = (register, _) => { if (register == 2) Thread.Sleep(900); };
        try
        {
            service.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            WaitForVerifiedOff(service, hat);
        }

        logger.Entries.Should().Contain(e => e.Message.Contains("exceeded its allowed wait", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e => e.Message.Contains("ran past its 1500 ms limit", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("limit", 200)]
    [DataRow("limit", 1600)]
    [DataRow("drive fault", 200)]
    [DataRow("drive fault", 1600)]
    public async Task AnEdgeDuringSlowSupervision_SkipsPendingLeds_AndStopsWithoutAFalseLatch(string trigger, int edgeDelayMs)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        PendingLedRetry(service);
        using var firstRead = new ManualResetEventSlim();
        // Slow the cycle's two routine reads and the cosmetic writes. The subsequent protective all-off and its
        // verification are healthy: this isolates contention, rather than a stop transfer exceeding its own deadline.
        hat.Registers.BeforeRead = _ =>
        {
            if (hat.RelayMask != 0)
            {
                firstRead.Set();
                Thread.Sleep(1400);
            }
        };
        hat.Registers.BeforeLedWrite = (_, _) => Thread.Sleep(1400);
        Task? cycle = null;
        Task? edge = null;
        try
        {
            cycle = Task.Run(service.RunSupervisionCycle);
            firstRead.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await Task.Delay(edgeDelayMs);
            var elapsed = Stopwatch.StartNew();
            // Do not change the input register: only the edge carries the evidence, as when the poll thread delivers
            // an assertion during a direct read that still reports the preceding level.
            edge = Task.Run(() =>
            {
                if (trigger == "limit") service.SimReverseLimitRaw(false);
                else service.SimFaultRaw(true);
            });
            var off = SpinWait.SpinUntil(() => hat.RelayMask == 0, TimeSpan.FromSeconds(7));
            var offTime = elapsed.Elapsed;
            off.Should().BeTrue();
            TestContext.WriteLine("{0}, edge at {1} ms: relays off {2:F0} ms after edge", trigger, edgeDelayMs, offTime.TotalMilliseconds);
            offTime.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            hat.Registers.BeforeRead = null;
            hat.Registers.BeforeLedWrite = null;
            if (cycle is not null) await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            if (edge is not null) await edge.WaitAsync(TimeSpan.FromSeconds(10));
            WaitForVerifiedOff(service, hat);
        }

        var status = service.GetCurrentStatusSnapshot();
        status.LastStopReason.Should().Be(trigger == "limit" ? RoofControllerStopReason.LimitSwitchReached : RoofControllerStopReason.DriveFault);
        status.IsFaultLatched.Should().Be(trigger == "drive fault");
        if (trigger == "drive fault") status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnEdgeBoundsTheRestOfAnOpenRoutineRetry_FromItsArrival(bool aLaterFenceAlsoArrives)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.Registers.FailRelayReads = true;
        service.ForceRelayStates(false, false, false).Should().BeFalse();
        hat.Registers.FailRelayReads = false;
        using var writing = new ManualResetEventSlim();
        hat.Registers.BeforeRelayWrite = (register, _) =>
        {
            if (register == 2)
            {
                writing.Set();
                Thread.Sleep(900);
            }
        };
        var cycle = Task.Run(service.RunSupervisionCycle);
        writing.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        await Task.Delay(100);
        var elapsed = Stopwatch.StartNew();
        var edge = Task.Run(() => service.SimFaultRaw(true));
        var laterEdge = aLaterFenceAlsoArrives ? Task.Factory.StartNew(() =>
        {
            Thread.Sleep(650); // Both fences precede the next HAT call; the later one must not extend the first.
            service.SimFaultRaw(true);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default) : null;
        try
        {
            await cycle.WaitAsync(TimeSpan.FromSeconds(6));
            elapsed.Stop();
            elapsed.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300),
                "the fence bounds the remaining routine retry, starting at the edge, before the next HAT call notices it");
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            await edge.WaitAsync(TimeSpan.FromSeconds(10));
            if (laterEdge is not null) await laterEdge.WaitAsync(TimeSpan.FromSeconds(10));
            WaitForVerifiedOff(service, hat);
        }

        hat.RelayMask.Should().Be(0);
        service.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    public async Task ARoutineAllOffRetry_WithoutAForeignFence_KeepsEachTransfersOwnLimit()
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Stop().IsSuccessful.Should().BeTrue("a completed request must not arm the subsequent routine retry");
        hat.Registers.FailRelayReads = true;
        service.ForceRelayStates(false, false, false).Should().BeFalse();
        hat.Registers.FailRelayReads = false;
        hat.Registers.BeforeRelayWrite = (register, _) => { if (register == 2) Thread.Sleep(600); };
        try
        {
            service.RunSupervisionCycle();
            service.IsHatIoOverrun.Should().BeFalse("a routine retry is not a new protective stop");
            service.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
            service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
        }
    }

    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("failed reads")]
    public async Task AnIdleProtectiveAllOff_WithSlowWrites_IsBounded_AndEventuallyVerifies(string trigger)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        if (trigger == "mismatch") hat.Registers.WriteByte(1, 1);
        else
        {
            hat.Registers.FailRelayReads = true;
            service.RunSupervisionCycle(); // first failed read keeps the last verified state
            var remaining = 1;
            hat.Registers.BeforeRead = register =>
            {
                if (register == 0 && Interlocked.Decrement(ref remaining) < 0) hat.Registers.FailRelayReads = false;
            };
        }

        hat.Registers.BeforeRelayWrite = (register, _) => { if (register == 2) Thread.Sleep(900); };
        var elapsed = Stopwatch.StartNew();
        try
        {
            service.RunSupervisionCycle();
            elapsed.Stop();
            TestContext.WriteLine("{0}: cycle returned {1:F0} ms after protective all-off decision", trigger, elapsed.Elapsed.TotalMilliseconds);
            elapsed.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            hat.Registers.BeforeRead = null;
            hat.Registers.BeforeRelayWrite = null;
            hat.Registers.FailRelayReads = false;
            WaitForVerifiedOff(service, hat);
        }

        hat.RelayMask.Should().Be(0);
        service.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    private static void WaitForVerifiedOff(RoofControllerServiceV4 service, FakeRoofHat hat)
        => SpinWait.SpinUntil(() => !service.IsHatIoOverrun && service.DeferredStopCount == 0
            && service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0,
            TimeSpan.FromSeconds(10)).Should().BeTrue("lane recovery must complete its queued all-off verification");

    private static void PendingLedRetry(RoofControllerServiceV4 service)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in new[] { "_lastIndicatorLedMask", "_lastAttemptedIndicatorLedMask" })
            typeof(RoofControllerServiceV4).GetField(field, flags)!.SetValue(service, null);
        typeof(RoofControllerServiceV4).GetField("_ledModesApplied", flags)!.SetValue(service, false);
    }
}
