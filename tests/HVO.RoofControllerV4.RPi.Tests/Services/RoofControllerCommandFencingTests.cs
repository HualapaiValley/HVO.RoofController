using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The command generation and its relay fence (#220, §2 U1/P1/T1, R8-3): a Stop or a protective stop that comes after a
/// start's checks wins at the relay; Stop's wait for the controller lock is bounded (W3, AA5); refusals, repeats and
/// no-ops never move the generation.
/// </summary>
[TestClass]
public class RoofControllerCommandFencingTests
{
    private const byte RelaySetRegister = 1;
    private const byte RelayClearRegister = 2;

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateAsync(Action<RoofControllerOptionsV4>? configure = null)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel, healthy (NC limits: HIGH = not active)
        var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), configure);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (service, hat);
    }

    [TestMethod]
    public async Task StopBetweenTheChecksAndTheRelay_Wins_NothingIsEnergized_NoFaultLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task<HVO.Core.Results.Result<RoofControllerStatus>>? stop = null;
        service.BeforeEnergizeHook = () =>
        {
            // Another thread's Stop: it moves the generation and raises the fence at once, then waits for the lock.
            var admitted = service.CommandGeneration;
            stop = Task.Run(() => service.Stop());
            SpinWait.SpinUntil(() => service.CommandGeneration > admitted, TimeSpan.FromSeconds(5)).Should().BeTrue();
        };
        hat.ClearRelayWriteLog();

        var result = service.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.Superseded);
        (await stop!).IsSuccessful.Should().BeTrue("the Stop got the lock once the refused start let it go");
        hat.RelayMask.Should().Be(0x00);
        hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister, "no relay was ever switched on");
        var status = service.GetCurrentStatusSnapshot();
        status.IsFaultLatched.Should().BeFalse("a Stop overtaking a start is not a fault");
        status.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task DriveFaultTripBetweenTheChecksAndTheRelay_FencesTheStart_AndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? trip = null;
        service.BeforeEnergizeHook = () =>
        {
            hat.SetInputs(true, true, true, false);
            trip = Task.Run(() => service.SimFaultRaw(true)); // waits for the lock the start holds
            Thread.Sleep(50);
        };

        var result = service.Close();
        await trip!;

        result.ErrorCode().Should().Be(RoofControllerErrorCode.Superseded, "the drive fault's fence goes up before its edge waits for the lock the start holds");
        hat.RelayMask.Should().Be(0x00);
        var status = service.GetCurrentStatusSnapshot();
        status.IsMoving.Should().BeFalse();
        status.IsFaultLatched.Should().BeTrue();
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task ADriveFaultEdge_WhileAStartHoldsTheLockPastTheWait_IsNeverDropped_FencesTheStart_AndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? trip = null;
        service.BeforeEnergizeHook = () =>
        {
            service.BeforeEnergizeHook = null;
            hat.SetInputs(true, true, true, false);
            trip = Task.Run(() => service.SimFaultRaw(true));

            // The start holds the lock longer than an edge waits for it (a HAT access that hangs).
            Thread.Sleep(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(500));
        };
        hat.ClearRelayWriteLog();

        var result = service.Close();
        await trip!;

        result.ErrorCode().Should().Be(RoofControllerErrorCode.Superseded, "the fault's fence went up before the edge waited for the lock");
        hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister, "no relay was switched on");
        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().IsFaultLatched, TimeSpan.FromSeconds(5)).Should().BeTrue("the edge's latch was queued, not dropped");
        var status = service.GetCurrentStatusSnapshot();
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        status.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ALimitEdge_WhileAStartHoldsTheLockPastTheWait_IsAppliedOnceTheLockIsFree()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? edge = null;
        service.BeforeEnergizeHook = () =>
        {
            service.BeforeEnergizeHook = null;
            hat.SetInputs(true, false, false, false); // the closed limit (NC: LOW = active)
            edge = Task.Run(() => service.SimReverseLimitRaw(false));
            Thread.Sleep(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(500));
        };

        // A limit asserting raises the fence before its edge waits (rereview S-F3): the start is refused at its relay.
        service.Close().ErrorCode().Should().Be(RoofControllerErrorCode.Superseded);
        await edge!;

        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().Status == RoofControllerStatus.Closed, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the edge was applied as soon as the lock was free");
        service.GetCurrentStatusSnapshot().IsMoving.Should().BeFalse();
        hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister && write.Value == 2, "the close relay never went on");
        hat.RelayMask.Should().Be(0x00);
    }

    /// <summary>Holds the controller's lock on a thread of its own until released (a HAT access that blocks, say).</summary>
    private static (ManualResetEventSlim Held, ManualResetEventSlim Release, Thread Holder) HoldTheLock(RoofControllerServiceV4 service)
    {
        var syncLock = typeof(RoofControllerServiceV4).GetField("_syncLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (syncLock)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        }) { IsBackground = true };
        holder.Start();
        held.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        return (held, release, holder);
    }

    [TestMethod]
    public async Task ALimitTripThatClearsBeforeTheLockIsFree_IsStillProcessed_WithItsMotion()
    {
        // PR #230 rereview S-F3 (Sol's repro): the edge's own evidence is kept, so a start limit that reasserts and clears
        // again while a blocked access holds the lock still stops the roof and latches.
        var (service, hat) = await CreateAsync();
        using var _ = service;
        service.Close().IsSuccessful.Should().BeTrue(); // from mid-travel: any assertion of the open limit is a reassertion
        var (_, release, holder) = HoldTheLock(service);

        hat.SetInputs(false, true, false, false); // the open limit (NC: LOW = active)
        await Task.Run(() => service.SimForwardLimitRaw(false)); // waits for the lock, then is queued
        hat.SetInputs(true, true, false, false); // and clears before the lock is free
        release.Set();
        holder.Join(TimeSpan.FromSeconds(5));

        SpinWait.SpinUntil(() => !service.GetCurrentStatusSnapshot().IsMoving, TimeSpan.FromSeconds(5)).Should().BeTrue("the trip stops the roof though the limit has cleared");
        var status = service.GetCurrentStatusSnapshot();
        status.LastStopReason.Should().Be(RoofControllerStopReason.StartLimitReasserted);
        status.IsFaultLatched.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task AShutdownBetweenTheChecksAndTheRelay_FencesAtOnce_AndNoDirectionRelayEnergizes()
    {
        // PR #230 rereview F11 and §15 U1: shutdown's intent and fence go up before it waits for the lock a start holds.
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task<HVO.Core.Results.Result<RoofStatusResponse>>? shutdown = null;
        var fenced = false;
        service.BeforeEnergizeHook = () =>
        {
            service.BeforeEnergizeHook = null;
            var admitted = service.CommandGeneration;
            shutdown = Task.Run(() => service.ShutdownAsync(CancellationToken.None));
            fenced = SpinWait.SpinUntil(() => service.CommandGeneration > admitted, TimeSpan.FromSeconds(1));
        };
        hat.ClearRelayWriteLog();

        var close = service.Close();
        (await shutdown!.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccessful.Should().BeTrue();

        fenced.Should().BeTrue("the shutdown raised the fence while the start still held the lock");
        close.IsSuccessful.Should().BeFalse("the start is refused at its relay");
        hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister, "no relay was switched on after the shutdown was asked for");
        hat.RelayMask.Should().Be(0x00);
        service.IsShuttingDown.Should().BeTrue();
    }

    [TestMethod]
    public async Task WatchdogBetweenTheChecksAndTheRelay_StopsAndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? watchdog = null;
        service.BeforeEnergizeHook = () =>
        {
            var generation = service.CurrentMotionGeneration;
            watchdog = Task.Run(() => service.TriggerWatchdog(generation)); // the start has armed this generation
            Thread.Sleep(50);
        };

        service.Close().IsSuccessful.Should().BeTrue();
        await watchdog!;

        hat.RelayMask.Should().Be(0x00);
        service.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
    }

    [TestMethod]
    public void TheHatAccessLimit_IsShorterThanStopsWaitForTheLock()
        => RoofControllerServiceV4.DefaultHatIoLimit.Should().BeLessThan(RoofControllerServiceV4.StopLockWait,
            "a Stop waiting behind a hanging access gets the lock once that access has failed at its limit");

    [TestMethod]
    public async Task AHangingRelayWrite_FailsAtItsLimit_ReleasesTheLock_Latches_AndAStopAndATripActAtOnce()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var service = SimulatedRoofControllerService.Create(hat, time);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        using var release = new ManualResetEventSlim(false);
        hat.Registers.BeforeRelayWrite = (register, value) =>
        {
            if (register == RelaySetRegister && value == 2)
            {
                // The close relay's write hangs in the transfer (§17 AA5).
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        try
        {
            var watch = Stopwatch.StartNew();
            var close = await Task.Run(() => service.Close()).WaitAsync(service.HatIoLimit + TimeSpan.FromSeconds(2));
            watch.Elapsed.Should().BeLessThan(service.HatIoLimit + TimeSpan.FromSeconds(1), "the write failed at its limit, and the lock with it");
            close.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
            var status = service.GetCurrentStatusSnapshot();
            status.IsFaultLatched.Should().BeTrue();
            status.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
            status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified, "the write's outcome is unknown until it returns");

            // A following Stop acts at once, and cannot report the relays off while the write is out.
            watch.Restart();
            service.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));

            // So does a drive fault.
            hat.SetInputs(true, true, true, false);
            watch.Restart();
            service.SimFaultRaw(true);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));

            // The latch cannot be cleared while the write is out.
            (await service.ClearFault()).ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            release.Set();
        }

        // The write lands late: the controller writes every relay off at once and verifies it; the latch stays.
        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0x00,
            TimeSpan.FromSeconds(5)).Should().BeTrue("the relays are written off and verified once the late write has returned");
        service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
        service.IsMoving.Should().BeFalse();

        hat.SetInputs(true, true, false, false);
        service.SimFaultRaw(false);
        var clear = service.ClearFault(100);
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await clear.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeTrue("with the write returned and the inputs healthy, a person may clear the latch");
    }

    [TestMethod]
    public async Task AStopWhoseOwnRelayWriteHangs_AnswersAtTheLimit_AndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        service.Close().IsSuccessful.Should().BeTrue();
        using var release = new ManualResetEventSlim(false);
        var armed = 1;
        hat.Registers.BeforeRelayWrite = (register, _) =>
        {
            if (register == RelayClearRegister && Interlocked.Exchange(ref armed, 0) == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30)); // Stop's first relays-off write hangs
            }
        };

        try
        {
            var watch = Stopwatch.StartNew();
            var stop = await Task.Run(() => service.Stop()).WaitAsync(service.HatIoLimit + TimeSpan.FromSeconds(2));
            watch.Elapsed.Should().BeLessThan(service.HatIoLimit + TimeSpan.FromSeconds(1));
            stop.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
            service.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            release.Set();
        }

        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0x00,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [TestMethod]
    public async Task SeveralSlowRelayOffWrites_StillAnswerAStopWithinItsOneBound()
    {
        // PR #230 rereview S-F4: one deadline for the whole Stop, its wait for the lock and all of its relay I/O.
        var (service, hat) = await CreateAsync();
        using var _ = service;
        service.Close().IsSuccessful.Should().BeTrue();
        hat.Registers.BeforeRelayWrite = (register, _) =>
        {
            if (register == RelayClearRegister)
            {
                Thread.Sleep(700); // every relays-off write is slow, none hangs
            }
        };

        var watch = Stopwatch.StartNew();
        var stop = service.Stop();
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300), "the Stop answers within its one bound");
        stop.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified, "its writes had not all been verified by then");
        hat.Registers.BeforeRelayWrite = null;
        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0x00,
            TimeSpan.FromSeconds(10)).Should().BeTrue("the relays-off work goes on after the answer and verifies");
        service.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task ASlowWriteThenAHangingWrite_StillAnswerAStopWithinItsOneBound_AndLatch()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        service.Close().IsSuccessful.Should().BeTrue();
        using var release = new ManualResetEventSlim(false);
        var writes = 0;
        hat.Registers.BeforeRelayWrite = (register, _) =>
        {
            if (register != RelayClearRegister)
            {
                return;
            }

            if (Interlocked.Increment(ref writes) == 1)
            {
                Thread.Sleep(900);
            }
            else
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        try
        {
            var watch = Stopwatch.StartNew();
            var stop = await Task.Run(() => service.Stop()).WaitAsync(TimeSpan.FromSeconds(5));
            watch.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(300));
            stop.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
            service.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        }
        finally
        {
            hat.Registers.BeforeRelayWrite = null;
            release.Set();
        }

        SpinWait.SpinUntil(() => service.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified && hat.RelayMask == 0x00,
            TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    [TestMethod]
    public async Task Generation_MovesWithStartsAndStops_NeverWithRefusalsRepeatsOrNoOps()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;

        var before = service.CommandGeneration;
        service.Close().IsSuccessful.Should().BeTrue();
        var started = service.CommandGeneration;
        started.Should().BeGreaterThan(before, "an admitted start moves it");

        service.Close().IsSuccessful.Should().BeTrue();
        service.CommandGeneration.Should().Be(started, "a repeat does not");

        service.Stop().IsSuccessful.Should().BeTrue();
        var stopped = service.CommandGeneration;
        stopped.Should().BeGreaterThan(started, "a Stop does");

        hat.SetInputs(true, false, false, false); // the closed limit (NC: LOW = active)
        service.ForceStatusRefresh();
        service.Close().IsSuccessful.Should().BeTrue();
        service.CommandGeneration.Should().Be(stopped, "a no-op at the destination does not");

        hat.SetInputs(true, true, true, false); // the drive fault
        service.Open().IsSuccessful.Should().BeFalse();
        var latched = service.CommandGeneration;
        service.Open().IsSuccessful.Should().BeFalse();
        service.CommandGeneration.Should().Be(latched, "a refusal does not");
    }
}
