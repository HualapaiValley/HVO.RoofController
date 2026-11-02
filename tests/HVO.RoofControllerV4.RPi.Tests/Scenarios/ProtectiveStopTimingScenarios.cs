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

    [TestMethod]
    [CommissioningCheck("C3", "5")]
    public Task ALimitEdge_DuringSlowRoutineIo_StopsThePlantRelaysWithinTheBound_WithoutAFalseLatch()
        => ProtectiveEdgeAsync(driveFault: false);

    [TestMethod]
    [CommissioningCheck("C4", "6")]
    public Task ADriveFaultEdge_DuringSlowRoutineIo_StopsThePlantRelaysWithinTheBound_WithoutAFalseLatch()
        => ProtectiveEdgeAsync(driveFault: true);

    private static async Task ProtectiveEdgeAsync(bool driveFault)
    {
        using var rig = await TimingPlant.StartAsync();
        rig.Controller.Close().IsSuccessful.Should().BeTrue();
        SpinWait.SpinUntil(() => rig.Session.GetStatus().VelocityMetersPerSecond < 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the emulated drive must be moving before the edge");
        PendingLedRetry(rig.Controller);

        using var firstRead = new ManualResetEventSlim();
        var routineReads = 0;
        rig.Registers.BeforeAccess = access =>
        {
            // Only the cycle's routine reads and cosmetic writes are degraded. The protective relay transfers remain
            // healthy, isolating how long their caller waits behind the cycle from an actual failure to stop the HAT.
            if (access.IsRead && access.Register is SmI010Board.DigitalInputRegister or SmI010Board.RelayValueRegister
                && Interlocked.Increment(ref routineReads) <= 2)
            {
                firstRead.Set();
                Thread.Sleep(1400);
            }
            else if (!access.IsRead && access.Register is >= SmI010Board.LedValueRegister and <= SmI010Board.LedModeRegister)
            {
                Thread.Sleep(1400);
            }
        };

        Task? cycle = null;
        Task? edge = null;
        try
        {
            cycle = Task.Run(rig.Controller.RunSupervisionCycle);
            firstRead.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await Task.Delay(1600);
            if (driveFault) rig.Session.Plant.TripDrive();
            else rig.Session.Plant.SetLimitFault(openLimit: false, LimitSwitchFault.StuckActuated);

            var elapsed = Stopwatch.StartNew();
            edge = Task.Run(() =>
            {
                // The emulated plant supplies the documented electrical level; deliver its edge just as the HAT poll
                // loop does, while the direct relay-register read still holds the controller lock.
                var bits = rig.Session.GetStatus().InputBits;
                if (driveFault) rig.Controller.SimFaultRaw((bits & 0x04) != 0);
                else rig.Controller.SimReverseLimitRaw((bits & 0x02) != 0);
            });
            SpinWait.SpinUntil(() => rig.Session.GetStatus().RelayRegister == 0, TimeSpan.FromSeconds(7)).Should().BeTrue();
            elapsed.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + StopMargin);
        }
        finally
        {
            rig.Registers.BeforeAccess = null;
            if (cycle is not null) await cycle.WaitAsync(TimeSpan.FromSeconds(10));
            if (edge is not null) await edge.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForDeferredStopsAsync(rig.Controller);
        }

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
        => (await Task.Run(() => SpinWait.SpinUntil(() => !controller.IsHatIoOverrun && controller.DeferredStopCount == 0,
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
            Controller.Dispose();
            _hat.Dispose();
            Session.Dispose();
        }
    }

    // Delays stay outside the plant lock so its roof, drive and limits continue evolving during a bus transfer.
    private sealed class DelayedRegisters(II2cRegisterClient inner) : II2cRegisterClient
    {
        public Action<HatBusAccess>? BeforeAccess { get; set; }
        public I2cConnectionSettings ConnectionSettings => inner.ConnectionSettings;
        public object SyncRoot => inner.SyncRoot;
        public byte ReadByte(byte register) { BeforeAccess?.Invoke(new(register, 1, true, null)); return inner.ReadByte(register); }
        public ushort ReadUInt16(byte register) { BeforeAccess?.Invoke(new(register, 2, true, null)); return inner.ReadUInt16(register); }
        public uint ReadUInt32(byte register) { BeforeAccess?.Invoke(new(register, 4, true, null)); return inner.ReadUInt32(register); }
        public void ReadBlock(byte register, Span<byte> destination) { BeforeAccess?.Invoke(new(register, destination.Length, true, null)); inner.ReadBlock(register, destination); }
        public void WriteByte(byte register, byte value) { BeforeAccess?.Invoke(new(register, 1, false, value)); inner.WriteByte(register, value); }
        public void WriteUInt16(byte register, ushort value) { BeforeAccess?.Invoke(new(register, 2, false, (byte)value)); inner.WriteUInt16(register, value); }
        public void WriteBlock(byte register, ReadOnlySpan<byte> data) { BeforeAccess?.Invoke(new(register, data.Length, false, data.IsEmpty ? null : data[0])); inner.WriteBlock(register, data); }
        public void Dispose() => inner.Dispose();
    }
}
