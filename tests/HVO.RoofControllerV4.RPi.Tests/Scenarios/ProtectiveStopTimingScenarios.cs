using System;
using System.Device.I2c;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>Protective stop timing on the emulated HAT, roof and drive with slow routine I/O or idle relay-off writes.</summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class ProtectiveStopTimingScenarios
{
    private static readonly TimeSpan StopMargin = TimeSpan.FromMilliseconds(300);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [CommissioningCheck("C7", "8")]
    public async Task AnIdlePlant_YieldsToAnOverdueStop_WithoutAFalseInputFailure()
    {
        using var rig = await TimingPlant.StartAsync();
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(rig.Controller)!;
        var stop = new Thread(() => rig.Controller.Stop());
        try
        {
            lock (sync)
            {
                stop.Start();
                stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                rig.Controller.RunSupervisionCycle();
                var status = rig.Controller.GetCurrentStatusSnapshot();
                status.ConsecutiveInputReadFailures.Should().Be(0);
                status.LastError.Should().BeNull();
            }
        }
        finally
        {
            if (stop.ThreadState != System.Threading.ThreadState.Unstarted) stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await WaitForDeferredStopsAsync(rig.Controller);
        }

        rig.Controller.GetCurrentStatusSnapshot().ConsecutiveInputReadFailures.Should().Be(0);
        rig.Controller.GetCurrentStatusSnapshot().LastError.Should().BeNull();
        AssertStoppedPlant(rig);
    }

    [TestMethod]
    [CommissioningCheck("C3", "6")]
    public Task ALimitEdge_AfterAnOverdueQueuedStop_VerifiesOffBeforeReleasingTheMonitor()
        => EdgeAfterOverdueStopAsync(driveFault: false);

    [TestMethod]
    [CommissioningCheck("C4", "7")]
    public Task ADriveFaultEdge_AfterAnOverdueQueuedStop_VerifiesOffBeforeReleasingTheMonitor()
        => EdgeAfterOverdueStopAsync(driveFault: true);

    private static async Task EdgeAfterOverdueStopAsync(bool driveFault)
    {
        using var rig = await TimingPlant.StartAsync();
        rig.Controller.Close().IsSuccessful.Should().BeTrue();
        SpinWait.SpinUntil(() => (rig.Session.GetStatus().InputBits & 0x01) == 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the start limit must release before another limit asserts");
        var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(rig.Controller)!;
        Exception? stopError = null;
        var stop = new Thread(() =>
        {
            try { rig.Controller.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified); }
            catch (Exception error) { stopError = error; }
        });
        try
        {
            lock (sync)
            {
                stop.Start();
                stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                stopError.Should().BeNull();
                if (driveFault) rig.Session.Plant.TripDrive();
                else rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);
                var writes = 0;
                rig.Registers.BeforeAccess = access =>
                {
                    if (!access.IsRead && access.Register == SmI010Board.RelayClearRegister) writes++;
                };
                var bits = rig.Session.GetStatus().InputBits;
                if (driveFault) rig.Controller.SimFaultRaw((bits & 0x04) != 0);
                else rig.Controller.SimReverseLimitRaw((bits & 0x02) != 0);

                writes.Should().Be(4, "the fresh edge must send all-off while the overdue worker still waits");
                var status = rig.Controller.GetCurrentStatusSnapshot();
                status.LastStopReason.Should().Be(driveFault ? RoofControllerStopReason.DriveFault : RoofControllerStopReason.LimitSwitchReached);
                status.IsFaultLatched.Should().Be(driveFault);
                if (driveFault) status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
                AssertStoppedPlant(rig);
            }
        }
        finally
        {
            rig.Registers.BeforeAccess = null;
            if (stop.ThreadState != System.Threading.ThreadState.Unstarted) stop.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await WaitForDeferredStopsAsync(rig.Controller);
        }
    }

    [TestMethod]
    [CommissioningCheck("C3", "5")]
    [DataRow(false, 200)]
    [DataRow(false, 1600)]
    [DataRow(true, 0)]
    public Task ALimitEdge_DuringSlowRoutineIo_StopsThePlantRelaysWithinTheBound_WithoutAFalseLatch(bool beforeSection, int edgeDelayMs)
        => ProtectiveEdgeAsync(driveFault: false, beforeSection, edgeDelayMs);

    [TestMethod]
    [CommissioningCheck("C4", "6")]
    [DataRow(false, 200)]
    [DataRow(false, 1600)]
    [DataRow(true, 0)]
    public Task ADriveFaultEdge_DuringSlowRoutineIo_StopsThePlantRelaysWithinTheBound_WithoutAFalseLatch(bool beforeSection, int edgeDelayMs)
        => ProtectiveEdgeAsync(driveFault: true, beforeSection, edgeDelayMs);

    private async Task ProtectiveEdgeAsync(bool driveFault, bool beforeSection, int edgeDelayMs)
    {
        using var rig = await TimingPlant.StartAsync();
        rig.Controller.Close().IsSuccessful.Should().BeTrue();
        SpinWait.SpinUntil(() => rig.Session.GetStatus().VelocityMetersPerSecond < 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the emulated drive must be moving before the edge");
        SpinWait.SpinUntil(() => (rig.Session.GetStatus().InputBits & 0x01) == 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the open start limit must have released before the closed limit can assert");
        PendingLedRetry(rig.Controller);

        using var firstRead = new ManualResetEventSlim();
        var routineReads = 0;
        rig.Registers.AfterRead = access =>
        {
            // Capture each routine register value before delaying its response, as a slow transport may. The plant
            // and poll edge keep evolving while the controller is waiting for that already captured value.
            if ((access.Register == SmI010Board.DigitalInputRegister
                || (!beforeSection && access.Register == SmI010Board.RelayValueRegister))
                && rig.Session.GetStatus().RelayRegister != 0 && Interlocked.Increment(ref routineReads) <= 2)
            {
                firstRead.Set();
                Thread.Sleep(1400);
            }
        };
        rig.Registers.BeforeAccess = access =>
        {
            if (!access.IsRead && access.Register is >= SmI010Board.LedValueRegister and <= SmI010Board.LedModeRegister)
                Thread.Sleep(1400);
        };

        Task? cycle = null;
        Task? edge = null;
        Thread? earlyEdge = null;
        Exception? earlyEdgeError = null;
        var elapsed = new Stopwatch();
        var relayOffTime = TimeSpan.MaxValue;
        var cycleReturnedAfter = TimeSpan.MaxValue;
        var edgeReturnedAfter = TimeSpan.MaxValue;
        try
        {
            // Observe the plant independently: the before-section case runs the cycle synchronously while this test
            // holds its monitor, so observing only after it returns would overstate the actual relay cut time.
            var cut = Task.Run(() => SpinWait.SpinUntil(() => elapsed.IsRunning
                && rig.Session.GetStatus().RelayRegister == 0, TimeSpan.FromSeconds(10))
                    ? elapsed.Elapsed : TimeSpan.MaxValue);
            if (beforeSection)
            {
                var sync = typeof(RoofControllerServiceV4).GetField("_syncLock", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(rig.Controller)!;
                lock (sync)
                {
                    // Resume a cycle after monitor acquisition but before opening its HAT section. The edge has
                    // already fenced and reached the monitor wait, so publishing the section must retain its deadline.
                    if (driveFault) rig.Session.Plant.TripDrive();
                    else rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);
                    var bits = rig.Session.GetStatus().InputBits;
                    var before = rig.Controller.CommandGeneration;
                    earlyEdge = new Thread(() =>
                    {
                        try
                        {
                            elapsed.Start();
                            if (driveFault) rig.Controller.SimFaultRaw((bits & 0x04) != 0);
                            else rig.Controller.SimReverseLimitRaw((bits & 0x02) != 0);
                        }
                        catch (Exception error) { earlyEdgeError = error; }
                        finally { edgeReturnedAfter = elapsed.Elapsed; }
                    }) { IsBackground = true };
                    earlyEdge.Start();
                    SpinWait.SpinUntil(() => rig.Controller.CommandGeneration > before
                        && (earlyEdge.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(1))
                        .Should().BeTrue("the protective edge has fenced and is waiting for the cycle's monitor");
                    try { rig.Controller.RunSupervisionCycle(); }
                    finally
                    {
                        cycleReturnedAfter = elapsed.Elapsed;
                        // Only the cycle's routine work is degraded; the edge's later relay I/O and LEDs are healthy.
                        rig.Registers.BeforeAccess = null;
                        rig.Registers.AfterRead = null;
                    }
                }
            }
            else
            {
                cycle = Task.Run(() =>
                {
                    try { rig.Controller.RunSupervisionCycle(); }
                    finally { cycleReturnedAfter = elapsed.Elapsed; }
                });
                firstRead.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                await Task.Delay(edgeDelayMs);
                if (driveFault) rig.Session.Plant.TripDrive();
                else rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

                elapsed.Start();
                // The emulated plant supplies the documented electrical level; deliver its edge just as the HAT poll
                // loop does, while the direct relay-register read still holds the controller lock.
                var bits = rig.Session.GetStatus().InputBits;
                edge = Task.Run(() =>
                {
                    try
                    {
                        if (driveFault) rig.Controller.SimFaultRaw((bits & 0x04) != 0);
                        else rig.Controller.SimReverseLimitRaw((bits & 0x02) != 0);
                    }
                    finally { edgeReturnedAfter = elapsed.Elapsed; }
                });
            }

            relayOffTime = await cut.WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally
        {
            rig.Registers.BeforeAccess = null;
            rig.Registers.AfterRead = null;
            if (cycle is not null) await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            if (edge is not null) await edge.WaitAsync(TimeSpan.FromSeconds(10));
            if (earlyEdge is not null) earlyEdge.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            await WaitForDeferredStopsAsync(rig.Controller);
        }

        earlyEdgeError.Should().BeNull();
        TestContext.WriteLine("{0}, beforeSection={1}, edge at {5} ms: relays off {2:F0} ms, cycle returned {3:F0} ms, edge returned {4:F0} ms after arrival",
            driveFault ? "drive fault" : "limit", beforeSection, relayOffTime.TotalMilliseconds,
            cycleReturnedAfter.TotalMilliseconds, edgeReturnedAfter.TotalMilliseconds, edgeDelayMs);
        relayOffTime.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + StopMargin);
        cycleReturnedAfter.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + StopMargin,
            "the cycle's remaining I/O, including its LEDs, must honor the pending edge's arrival deadline");
        edgeReturnedAfter.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + StopMargin,
            "the protective edge's wait and all of its I/O share its arrival deadline");
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.LastStopReason.Should().Be(driveFault ? RoofControllerStopReason.DriveFault : RoofControllerStopReason.LimitSwitchReached);
        status.IsFaultLatched.Should().Be(driveFault);
        if (driveFault) status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        AssertStoppedPlant(rig);
    }

    [TestMethod]
    [CommissioningCheck("C7", "7")]
    [DataRow("mismatch")]
    [DataRow("failed reads")]
    public async Task IdleProtectiveAllOff_WithSlowWrites_ReturnsWithinTheBound_AndEventuallyVerifies(string trigger)
    {
        using var rig = await TimingPlant.StartAsync();
        if (trigger == "mismatch")
        {
            // A register fault turns on a direction relay while idle. Without the STOP permit the drive stays stopped.
            rig.Session.Client.WriteByte(SmI010Board.RelaySetRegister, 1);
        }
        else
        {
            var reads = 0;
            rig.Registers.BeforeAccess = access =>
            {
                if (access.IsRead && access.Covers(SmI010Board.RelayValueRegister) && ++reads <= 2)
                {
                    throw new IOException("Injected failure of the idle supervision relay read.");
                }
            };
            rig.Controller.RunSupervisionCycle(); // The first failed read retains the verified idle state.
        }

        var existing = rig.Registers.BeforeAccess;
        rig.Registers.BeforeAccess = access =>
        {
            existing?.Invoke(access);
            if (!access.IsRead && access.Register == SmI010Board.RelayClearRegister) Thread.Sleep(900);
        };

        try
        {
            var elapsed = Stopwatch.StartNew();
            rig.Controller.RunSupervisionCycle();
            elapsed.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + StopMargin);
        }
        finally
        {
            rig.Registers.BeforeAccess = null;
            await WaitForDeferredStopsAsync(rig.Controller);
        }

        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.IsFaultLatched.Should().BeTrue("the bounded idle all-off could not finish all of its verification in time");
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        AssertStoppedPlant(rig);
    }

    private static async Task WaitForDeferredStopsAsync(RoofControllerServiceV4 controller)
        => (await Task.Run(() => SpinWait.SpinUntil(() => !controller.IsHatIoOverrun && controller.DeferredStopCount == 0
            && controller.GetCurrentStatusSnapshot().RelayRegisterState == RoofRelayRegisterState.Verified
            && controller.GetCurrentStatusSnapshot().RelayRegisterMask == 0,
            TimeSpan.FromSeconds(10)))).Should().BeTrue("the relays-off continuation must finish and verify the register");

    private static void AssertStoppedPlant(TimingPlant rig)
    {
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.CommandedMotion.Should().Be(RoofMotionDirection.None);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(0);
        SpinWait.SpinUntil(() => rig.Session.GetStatus().RelayContacts.All(contact => !contact)
            && rig.Session.GetStatus().VelocityMetersPerSecond == 0, TimeSpan.FromSeconds(2)).Should().BeTrue();
        rig.Session.GetStatus().RelayRegister.Should().Be(0);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    private static void PendingLedRetry(RoofControllerServiceV4 controller)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var name in new[] { "_lastIndicatorLedMask", "_lastAttemptedIndicatorLedMask" })
            typeof(RoofControllerServiceV4).GetField(name, flags)!.SetValue(controller, null);
        typeof(RoofControllerServiceV4).GetField("_ledModesApplied", flags)!.SetValue(controller, false);
    }

    private sealed class TimingPlant : IDisposable
    {
        private readonly FourRelayFourInputHat _hat;

        private TimingPlant()
        {
            Session = new HatEmulatorSession(new HatEmulatorSessionOptions
            {
                Plant = new RoofPlantOptions { InitialPosition = 1.0 },
                BusTiming = EmulatedBusTiming.Instant
            });
            Registers = new DelayedRegisters(Session.Client);
            _hat = new FourRelayFourInputHat(Registers, ownsClient: true);
            // The plant and stop bounds run on real time; a manual controller clock leaves motion timers under the
            // scenario's control while it drives supervision and edges explicitly.
            Controller = SimulatedRoofControllerService.Create(_hat, new ManualTimeProvider(), o =>
            {
                o.UseNormallyClosedLimitSwitches = false;
                o.FaultInputActiveHigh = false;
            });
        }

        public HatEmulatorSession Session { get; }
        public DelayedRegisters Registers { get; }
        public SimulatedRoofControllerService Controller { get; }

        public static async Task<TimingPlant> StartAsync()
        {
            var rig = new TimingPlant();
            (await rig.Controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
            return rig;
        }

        public void Dispose()
        {
            Registers.BeforeAccess = null;
            Registers.AfterRead = null;
            Controller.Dispose();
            _hat.Dispose();
            Session.Dispose();
        }
    }

    // Delays stay outside the plant lock so its roof, drive and limits continue evolving during a bus transfer.
    private sealed class DelayedRegisters(II2cRegisterClient inner) : II2cRegisterClient
    {
        public Action<HatBusAccess>? BeforeAccess { get; set; }
        public Action<HatBusAccess>? AfterRead { get; set; }
        public I2cConnectionSettings ConnectionSettings => inner.ConnectionSettings;
        public object SyncRoot => inner.SyncRoot;
        public byte ReadByte(byte register)
        {
            var access = new HatBusAccess(register, 1, true, null);
            BeforeAccess?.Invoke(access);
            var value = inner.ReadByte(register);
            AfterRead?.Invoke(access);
            return value;
        }
        public ushort ReadUInt16(byte register)
        {
            var access = new HatBusAccess(register, 2, true, null);
            BeforeAccess?.Invoke(access);
            var value = inner.ReadUInt16(register);
            AfterRead?.Invoke(access);
            return value;
        }
        public uint ReadUInt32(byte register)
        {
            var access = new HatBusAccess(register, 4, true, null);
            BeforeAccess?.Invoke(access);
            var value = inner.ReadUInt32(register);
            AfterRead?.Invoke(access);
            return value;
        }
        public void ReadBlock(byte register, Span<byte> destination)
        {
            var access = new HatBusAccess(register, destination.Length, true, null);
            BeforeAccess?.Invoke(access);
            inner.ReadBlock(register, destination);
            AfterRead?.Invoke(access);
        }
        public void WriteByte(byte register, byte value) { BeforeAccess?.Invoke(new(register, 1, false, value)); inner.WriteByte(register, value); }
        public void WriteUInt16(byte register, ushort value) { BeforeAccess?.Invoke(new(register, 2, false, (byte)value)); inner.WriteUInt16(register, value); }
        public void WriteBlock(byte register, ReadOnlySpan<byte> data) { BeforeAccess?.Invoke(new(register, data.Length, false, data.IsEmpty ? null : data[0])); inner.WriteBlock(register, data); }
        public void Dispose() => inner.Dispose();
    }
}
