using System;
using System.Diagnostics;
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
            SpinWait.SpinUntil(() => !service.IsHatIoOverrun && service.DeferredStopCount == 0,
                TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        logger.Entries.Should().Contain(e => e.Message.Contains("exceeded its allowed wait", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e => e.Message.Contains("ran past its 1500 ms limit", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("limit")]
    [DataRow("drive fault")]
    public async Task AnEdgeDuringSlowSupervision_SkipsPendingLeds_AndStopsWithoutAFalseLatch(string trigger)
    {
        using var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Close().IsSuccessful.Should().BeTrue();
        PendingLedRetry(service);
        using var firstRead = new ManualResetEventSlim();
        var reads = 0;
        // Slow the cycle's two routine reads and the cosmetic writes. The subsequent protective all-off and its
        // verification are healthy: this isolates contention, rather than a stop transfer exceeding its own deadline.
        hat.Registers.BeforeRead = _ =>
        {
            if (Interlocked.Increment(ref reads) <= 2)
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
            await Task.Delay(1600);
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
            TestContext.WriteLine("{0}: relays off {1:F0} ms after edge", trigger, offTime.TotalMilliseconds);
            offTime.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            hat.Registers.BeforeRead = null;
            hat.Registers.BeforeLedWrite = null;
            if (cycle is not null) await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            if (edge is not null) await edge.WaitAsync(TimeSpan.FromSeconds(10));
            SpinWait.SpinUntil(() => service.DeferredStopCount == 0 && !service.IsHatIoOverrun, TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        var status = service.GetCurrentStatusSnapshot();
        status.LastStopReason.Should().Be(trigger == "limit" ? RoofControllerStopReason.LimitSwitchReached : RoofControllerStopReason.DriveFault);
        status.IsFaultLatched.Should().Be(trigger == "drive fault");
        if (trigger == "drive fault") status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    public async Task AnEdgeBoundsTheRestOfAnOpenRoutineRetry_FromItsArrival()
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
            SpinWait.SpinUntil(() => !service.IsHatIoOverrun && service.DeferredStopCount == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
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
            SpinWait.SpinUntil(() => !service.IsHatIoOverrun && service.DeferredStopCount == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        hat.RelayMask.Should().Be(0);
        service.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    private static void PendingLedRetry(RoofControllerServiceV4 service)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in new[] { "_lastIndicatorLedMask", "_lastAttemptedIndicatorLedMask" })
            typeof(RoofControllerServiceV4).GetField(field, flags)!.SetValue(service, null);
        typeof(RoofControllerServiceV4).GetField("_ledModesApplied", flags)!.SetValue(service, false);
    }
}
