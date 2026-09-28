using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The HAT emulator over TCP answers exactly as the in-process emulated client does: two identical plants on identical
/// clocks, one read through <see cref="EmulatedHatRegisterClient"/> directly and one through
/// <see cref="SocketI2cRegisterClient"/> and <see cref="HatEmulatorServer"/>, see the same accesses and must return the
/// same bytes or fail with the same exception, and end with the same plant history.
/// </summary>
[TestClass]
public sealed class HatEmulatorParityTests
{
    [TestMethod]
    public async Task RegisterAccesses_ReturnTheSameBytesAndFailures_AndLeaveTheSameHistory()
    {
        await using var rig = await ParityRig.StartAsync();

        // Every register, one at a time and in blocks.
        for (var register = 0; register < 256; register++)
        {
            rig.Same($"read 0x{register:X2}", c => [c.ReadByte((byte)register)]);
        }

        rig.Same("read all", c => Read(c, 0, 256));
        rig.Same("read 0x78 x2", c => BitConverter.GetBytes(c.ReadUInt16(SmI010Board.RevisionRegister)));
        rig.Same("read 0x78 x4", c => BitConverter.GetBytes(c.ReadUInt32(SmI010Board.RevisionRegister)));
        rig.Same("read 0x00 x9", c => Read(c, 0, 9));

        // The STOP permit, then open: the roof leaves the closed limit and the inputs follow it.
        rig.Same("relays 0x08", c => Write(c, SmI010Board.RelayValueRegister, 0x08));
        rig.Same("relay set 0x01", c => Write(c, SmI010Board.RelaySetRegister, 0x01));
        for (var step = 0; step < 40; step++)
        {
            rig.Advance(TimeSpan.FromMilliseconds(50));
            rig.Same($"inputs while opening {step}", c => [c.ReadByte(SmI010Board.DigitalInputRegister)]);
            rig.Same($"relays while opening {step}", c => [c.ReadByte(SmI010Board.RelayValueRegister)]);
        }

        rig.Same("relay clear 0x01", c => Write(c, SmI010Board.RelayClearRegister, 0x01));
        rig.Advance(TimeSpan.FromSeconds(1));
        rig.Same("inputs after the stop", c => Read(c, 0, 4));

        // LEDs: value, set, clear and modes.
        rig.Same("led modes", c => Write(c, SmI010Board.LedModeRegister, 0x0F));
        rig.Same("led value", c => Write(c, SmI010Board.LedValueRegister, 0x05));
        rig.Same("led set", c => Write(c, SmI010Board.LedSetRegister, 0x02));
        rig.Same("led clear", c => Write(c, SmI010Board.LedClearRegister, 0x01));
        rig.Same("leds", c => Read(c, SmI010Board.LedValueRegister, 4));
        rig.Same("wide write", c => { c.WriteUInt16(0x40, 0xBEEF); return Read(c, 0x40, 2); });

        // Injected bus failures.
        rig.Both(b => b.FailNextReads = 2);
        rig.Same("failed read 1", c => [c.ReadByte(SmI010Board.DigitalInputRegister)]);
        rig.Same("failed read 2", c => [c.ReadByte(SmI010Board.RelayValueRegister)]);
        rig.Same("read after the failures", c => [c.ReadByte(SmI010Board.DigitalInputRegister)]);
        rig.Both(b => b.FailWrites = true);
        rig.Same("failed write", c => Write(c, SmI010Board.RelayValueRegister, 0x00));
        rig.Both(b => b.FailWrites = false);
        rig.Both(b => b.FailInputReads = true);
        rig.Same("failed input read", c => Read(c, 0, 4));
        rig.Same("relay read during the input failure", c => [c.ReadByte(SmI010Board.RelayValueRegister)]);
        rig.Both(b => b.FailInputReads = false);
        rig.Both(b => b.FailWhen = access => !access.IsRead && access.Register == SmI010Board.RelayValueRegister && access.Value == 0x00);
        rig.Same("targeted write failure", c => Write(c, SmI010Board.RelayValueRegister, 0x00));
        rig.Same("untargeted write", c => Write(c, SmI010Board.LedValueRegister, 0x00));
        rig.Both(b => b.FailWhen = null);

        // Accesses the HAT cannot take.
        rig.Same("read past 0xFF", c => Read(c, 0xFF, 2));
        rig.Same("write past 0xFF", c => { c.WriteBlock(0xF8, new byte[9]); return []; });
        rig.Same("empty read", c => Read(c, 5, 0));
        rig.Same("empty write", c => { c.WriteBlock(5, ReadOnlySpan<byte>.Empty); return []; });

        // The HAT loses power, then comes back at its power-on state.
        rig.BothPlants(p => p.SetHatPower(false));
        rig.Same("read while unpowered", c => [c.ReadByte(SmI010Board.DigitalInputRegister)]);
        rig.Same("write while unpowered", c => Write(c, SmI010Board.RelayValueRegister, 0x08));
        rig.BothPlants(p => p.SetHatPower(true));
        rig.Advance(TimeSpan.FromMilliseconds(100));
        rig.Same("registers after power returns", c => Read(c, 0, 9));

        rig.Remote.ConnectCount.Should().Be(1, "an I/O error keeps the connection");
        rig.AssertSamePlants();
    }

    [TestMethod]
    public async Task TheHatLibrary_SeesTheSameResults_ThroughEitherClient()
    {
        await using var rig = await ParityRig.StartAsync();
        using var direct = new FourRelayFourInputHat(rig.Direct, ownsClient: false);
        using var remote = new FourRelayFourInputHat(rig.Remote, ownsClient: false);

        remote.IsHardwareBacked.Should().Be(direct.IsHardwareBacked).And.BeTrue();
        remote.HardwareRevision.Should().Be(direct.HardwareRevision);
        remote.SoftwareRevision.Should().Be(direct.SoftwareRevision);

        void Same<T>(string step, Func<FourRelayFourInputHat, Result<T>> call)
            => Describe(call(remote)).Should().Be(Describe(call(direct)), step);

        Same("inputs at rest", h => h.GetAllDigitalInputs());
        Same("relays off", h => h.SetRelaysMask(0));
        Same("stop permit", h => h.SetRelay(4, true));
        Same("open", h => h.SetRelay(1, true));
        Same("relay mask", h => h.GetRelaysMask());
        for (var step = 0; step < 30; step++)
        {
            rig.Advance(TimeSpan.FromMilliseconds(100));
            Same($"inputs {step}", h => h.GetAllDigitalInputs());
            Same($"input mask {step}", h => h.GetDigitalInputsMask());
        }

        Same("stop", h => h.SetRelay(1, false));
        Same("led modes", h => h.SetLedModesMask(0x0F));
        Same("led mode mask", h => h.GetLedModesMask());
        Same("leds", h => h.SetLedsMask(0x09));
        Same("all leds", h => h.GetAllLeds());

        rig.Both(b => b.FailNextWrites = 1);
        Same("failed relay write", h => h.SetRelay(4, false));
        rig.Both(b => b.FailInputReads = true);
        Same("failed input read", h => h.GetAllDigitalInputs());
        rig.Both(b => b.FailInputReads = false);
        rig.BothPlants(p => p.SetHatPower(false));
        Same("unpowered relay read", h => h.GetRelaysMask());
        rig.BothPlants(p => p.SetHatPower(true));
        Same("relay read after power returns", h => h.GetRelaysMask());

        rig.AssertSamePlants();
    }

    private static byte[] Read(II2cRegisterClient client, byte register, int count)
    {
        var buffer = new byte[count];
        client.ReadBlock(register, buffer);
        return buffer;
    }

    private static byte[] Write(II2cRegisterClient client, byte register, byte value)
    {
        client.WriteByte(register, value);
        return [];
    }

    private static string Describe<T>(Result<T> result)
        => result.IsSuccessful ? $"ok {Format(result.Value)}" : $"failed {result.Error?.GetType().Name}: {result.Error?.Message}";

    private static string? Format(object? value)
        => value is System.Collections.IEnumerable items and not string
            ? "[" + string.Join(", ", items.Cast<object?>()) + "]"
            : value?.ToString();

    private sealed class ParityRig : IAsyncDisposable
    {
        private readonly ManualTimeProvider _directTime = new();
        private readonly ManualTimeProvider _remoteTime = new();

        private ParityRig()
        {
            DirectPlant = new RoofPlant(new RoofPlantOptions(), _directTime);
            RemotePlant = new RoofPlant(new RoofPlantOptions(), _remoteTime);
            Direct = new EmulatedHatRegisterClient(DirectPlant, timing: EmulatedBusTiming.Instant);
            Served = new EmulatedHatRegisterClient(RemotePlant, timing: EmulatedBusTiming.Instant);
            Server = new HatEmulatorServer(() => Served, new IPEndPoint(IPAddress.Loopback, 0));
            Server.Start();
            Remote = new SocketI2cRegisterClient(
                new HatEmulatorOptions { Enabled = true, Host = "127.0.0.1", Port = Server.LocalEndPoint.Port, RequestTimeout = TimeSpan.FromSeconds(10) },
                busId: 1,
                address: 0x0E,
                new CapturingLogger<SocketI2cRegisterClient>());
        }

        public RoofPlant DirectPlant { get; }

        public RoofPlant RemotePlant { get; }

        /// <summary>The in-process client.</summary>
        public EmulatedHatRegisterClient Direct { get; }

        /// <summary>The client the server answers with.</summary>
        public EmulatedHatRegisterClient Served { get; }

        public HatEmulatorServer Server { get; }

        /// <summary>The controller's client, through the server.</summary>
        public SocketI2cRegisterClient Remote { get; }

        public static Task<ParityRig> StartAsync() => Task.FromResult(new ParityRig());

        public void Advance(TimeSpan duration)
        {
            _directTime.Advance(duration);
            _remoteTime.Advance(duration);
        }

        public void Both(Action<EmulatedHatRegisterClient> configure)
        {
            configure(Direct);
            configure(Served);
        }

        public void BothPlants(Action<RoofPlant> action)
        {
            action(DirectPlant);
            action(RemotePlant);
        }

        /// <summary>Runs the access through both clients and compares the bytes, or the exception type and message.</summary>
        public void Same(string step, Func<II2cRegisterClient, byte[]> access)
            => Outcome(() => access(Remote)).Should().Be(Outcome(() => access(Direct)), step);

        public void AssertSamePlants()
        {
            RemotePlant.History.Select(e => (e.At, e.Kind, e.Detail)).Should().Equal(DirectPlant.History.Select(e => (e.At, e.Kind, e.Detail)));
            RemotePlant.Violations.Select(v => (v.At, v.Kind, v.Detail)).Should().Equal(DirectPlant.Violations.Select(v => (v.At, v.Kind, v.Detail)));
            RemotePlant.Position.Should().Be(DirectPlant.Position);
            RemotePlant.History.Should().NotBeEmpty();
            (Served.ReadCount, Served.WriteCount, Served.InjectedFailures).Should().Be((Direct.ReadCount, Direct.WriteCount, Direct.InjectedFailures));
        }

        public async ValueTask DisposeAsync()
        {
            Remote.Dispose();
            await Server.DisposeAsync();
            Direct.Dispose();
            Served.Dispose();
        }

        private static string Outcome(Func<byte[]> access)
        {
            try
            {
                return "ok " + Convert.ToHexString(access());
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
