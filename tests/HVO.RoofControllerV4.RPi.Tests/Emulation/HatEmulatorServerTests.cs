using System;
using System.Device.I2c;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Iot.Devices.Abstractions;
using HVO.RoofControllerV4.Common.Emulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The HAT emulator's TCP server against a raw protocol client: the Hello handshake, accesses, refusals and the link
/// controls (outage, response delay, disconnect).
/// </summary>
[TestClass]
public sealed class HatEmulatorServerTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Hello_IsAnsweredWithTheHatsBusAndAddress_ThenEachAccessRunsAgainstTheTarget()
    {
        await using var rig = ServerRig.Start(busId: 3);
        using var link = await RawLink.ConnectAsync(rig.Server);

        var reply = await link.RequestAsync(HatEmulatorProtocol.CreateHello(7));
        reply.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        reply.Sequence.Should().Be(7u);
        HatEmulatorProtocol.ReadHelloReply(reply).Should().Be((3, 0x0E));

        var revision = await link.RequestAsync(Read(8, SmI010Board.RevisionRegister, 2));
        revision.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        revision.Sequence.Should().Be(8u);
        revision.Payload.ToArray().Should().Equal(Direct(rig, c => { var b = new byte[2]; c.ReadBlock(SmI010Board.RevisionRegister, b); return b; }));

        var write = await link.RequestAsync(Write(9, SmI010Board.RelayValueRegister, 0x08));
        write.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        write.Payload.IsEmpty.Should().BeTrue();
        rig.Plant.Hat.RelayRegister.Should().Be(0x08);

        rig.Server.Requests.Should().Be(2, "the Hello is not an access");
        rig.Server.AcceptedConnections.Should().Be(1);
        rig.Server.OpenConnections.Should().Be(1);
    }

    [TestMethod]
    [DataRow("version", "Protocol version 2 is not supported; the emulator speaks version 1.")]
    [DataRow("magic", "Hello does not carry the HVOH magic.")]
    [DataRow("read", "Expected Hello first, received Read.")]
    public async Task AFirstFrameThatIsNotAValidHello_IsRefused_AndTheConnectionCloses(string variant, string reason)
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        var first = variant switch
        {
            "version" => HatEmulatorProtocol.CreateHello(1, version: 2),
            "magic" => new HatEmulatorFrame(HatEmulatorFrameKind.Hello, 1, 0, 0, "HVOX\0\u0001"u8.ToArray()),
            _ => Read(1, 0, 1)
        };

        var refusal = await link.RequestAsync(first);

        refusal.Kind.Should().Be(HatEmulatorFrameKind.ProtocolError);
        refusal.Sequence.Should().Be(1u);
        refusal.PayloadText.Should().Be(reason);
        (await link.ReceiveAsync()).Should().BeNull("the server closes a connection it refused");
        rig.Server.Requests.Should().Be(0);
        rig.Target.ReadCount.Should().Be(0);
    }

    [TestMethod]
    public async Task AReadCarryingAPayload_IsRefused_AndTheConnectionCloses_WithoutTouchingTheHat()
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();

        var refusal = await link.RequestAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Read, 2, 0, 1, new byte[] { 1 }));

        refusal.Kind.Should().Be(HatEmulatorFrameKind.ProtocolError);
        refusal.PayloadText.Should().Be("A Read carries no payload.");
        (await link.ReceiveAsync()).Should().BeNull();
        rig.Server.Requests.Should().Be(0);
        rig.Target.ReadCount.Should().Be(0);
    }

    [TestMethod]
    [DataRow(0x00, 2, 1, "A Write of 2 registers carries 1 bytes.")]
    [DataRow(0xFF, 2, 2, "The access runs past register 0xFF.")]
    public async Task AMalformedWrite_IsRefused_AndTheConnectionCloses_WithoutTouchingTheHat(int register, int count, int bytes, string reason)
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();

        var refusal = await link.RequestAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Write, 2, (byte)register, (ushort)count, new byte[bytes]));

        refusal.Kind.Should().Be(HatEmulatorFrameKind.ProtocolError);
        refusal.PayloadText.Should().Be(reason);
        (await link.ReceiveAsync()).Should().BeNull();
        rig.Target.WriteCount.Should().Be(0);
    }

    [TestMethod]
    public async Task AGarbledFrame_IsRefused_AndTheConnectionCloses()
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();

        // Only the length prefix: bytes the server never reads would turn its close into a reset.
        await link.SendRawAsync([0x00, 0x03]);

        var refusal = await link.ReceiveAsync();
        refusal.Should().NotBeNull();
        refusal!.Value.Kind.Should().Be(HatEmulatorFrameKind.ProtocolError);
        refusal.Value.PayloadText.Should().Be("Frame body length 3 is outside 8-2048.");
        (await link.ReceiveAsync()).Should().BeNull();
    }

    [TestMethod]
    public async Task ABusFailure_IsAnIoError_AndTheConnectionStaysUp()
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();
        rig.Target.FailNextInputReads = 1;

        var failure = await link.RequestAsync(Read(2, SmI010Board.DigitalInputRegister, 1));
        failure.Kind.Should().Be(HatEmulatorFrameKind.IoError);
        failure.Sequence.Should().Be(2u);
        failure.PayloadText.Should().Be("Injected I2C read failure at register 3.");

        rig.Plant.SetHatPower(false);
        var unpowered = await link.RequestAsync(Write(3, SmI010Board.RelayValueRegister, 0x08));
        unpowered.Kind.Should().Be(HatEmulatorFrameKind.IoError);
        unpowered.PayloadText.Should().Be("SM-I-010 at 0x0E does not respond (HAT unpowered).");

        rig.Plant.SetHatPower(true);
        var answer = await link.RequestAsync(Read(4, SmI010Board.DigitalInputRegister, 1));
        answer.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        answer.Payload.ToArray().Should().Equal(Direct(rig, c => [c.ReadByte(SmI010Board.DigitalInputRegister)]));
        rig.Server.Requests.Should().Be(3);
        rig.Server.OpenConnections.Should().Be(1);
    }

    [TestMethod]
    public async Task Outage_ClosesOpenConnections_AndClosesNewOnes_UntilItEnds()
    {
        await using var rig = ServerRig.Start();
        using var before = await RawLink.ConnectAsync(rig.Server);
        await before.HelloAsync();

        rig.Server.Outage = true;

        rig.Server.Outage.Should().BeTrue();
        (await before.ReceiveOrClosedAsync()).Should().BeNull("the outage closes open connections");
        using (var during = await RawLink.ConnectAsync(rig.Server))
        {
            (await during.ReceiveOrClosedAsync()).Should().BeNull("a connection during the outage is closed as soon as it is accepted");
        }

        rig.Server.Outage = false;
        using var after = await RawLink.ConnectAsync(rig.Server);
        await after.HelloAsync();
        (await after.RequestAsync(Read(2, 0, 1))).Kind.Should().Be(HatEmulatorFrameKind.Ok);
        rig.Server.AcceptedConnections.Should().Be(3);
    }

    [TestMethod]
    public async Task Outage_DuringARequest_SendsNoResponse()
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();
        rig.Server.ResponseDelay = TimeSpan.FromMilliseconds(300);

        await link.SendAsync(Read(2, 0, 1));
        await WaitUntilAsync(() => rig.Server.Requests == 1);
        rig.Server.Outage = true;

        (await link.ReceiveOrClosedAsync()).Should().BeNull();
        rig.Server.Requests.Should().Be(1, "the access ran; only its response was lost");
    }

    [TestMethod]
    public async Task DisconnectAll_ClosesEveryConnection_ButNewOnesAreAccepted()
    {
        await using var rig = ServerRig.Start();
        using var first = await RawLink.ConnectAsync(rig.Server);
        using var second = await RawLink.ConnectAsync(rig.Server);
        await first.HelloAsync();
        await second.HelloAsync();
        rig.Server.OpenConnections.Should().Be(2);

        rig.Server.DisconnectAll();

        (await first.ReceiveOrClosedAsync()).Should().BeNull();
        (await second.ReceiveOrClosedAsync()).Should().BeNull();
        await WaitUntilAsync(() => rig.Server.OpenConnections == 0);
        using var third = await RawLink.ConnectAsync(rig.Server);
        await third.HelloAsync();
        (await third.RequestAsync(Read(2, 0, 1))).Kind.Should().Be(HatEmulatorFrameKind.Ok);
    }

    [TestMethod]
    public async Task ResponseDelay_HoldsEachResponse_AndMustNotBeNegative()
    {
        await using var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();
        rig.Server.ResponseDelay = TimeSpan.FromMilliseconds(250);

        var watch = Stopwatch.StartNew();
        (await link.RequestAsync(Read(2, 0, 1))).Kind.Should().Be(HatEmulatorFrameKind.Ok);

        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        rig.Server.ResponseDelay.Should().Be(TimeSpan.FromMilliseconds(250));
        FluentActions.Invoking(() => rig.Server.ResponseDelay = TimeSpan.FromTicks(-1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task TheTargetIsReadForEveryAccess_SoASessionResetTakesEffectAtOnce()
    {
        var time = new ManualTimeProvider();
        using var session = new HatEmulatorSession(new HatEmulatorSessionOptions { BusTiming = EmulatedBusTiming.Instant }, time);
        await using var server = new HatEmulatorServer(session.CurrentClient, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var link = await RawLink.ConnectAsync(server);
        await link.HelloAsync();

        (await link.RequestAsync(Write(2, SmI010Board.RelayValueRegister, 0x08))).Kind.Should().Be(HatEmulatorFrameKind.Ok);
        session.Plant.Hat.RelayRegister.Should().Be(0x08);

        session.Reset();

        var relays = await link.RequestAsync(Read(3, SmI010Board.RelayValueRegister, 1));
        relays.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        relays.Payload.ToArray().Should().Equal([0x00], "the new plant's HAT is at power-on");
        session.Generation.Should().Be(1);
        session.Client.ReadCount.Should().Be(1);
    }

    [TestMethod]
    public async Task Start_Twice_Throws_AndDispose_IsIdempotent()
    {
        var rig = ServerRig.Start();
        using var link = await RawLink.ConnectAsync(rig.Server);
        await link.HelloAsync();

        FluentActions.Invoking(rig.Server.Start).Should().Throw<InvalidOperationException>().WithMessage("The server is already started.");

        await rig.DisposeAsync();
        await rig.Server.DisposeAsync();
        (await link.ReceiveOrClosedAsync()).Should().BeNull("disposing the server closes its connections");
    }

    [TestMethod]
    public async Task AConnectionThatSendsNoHello_IsClosedAfterTheHelloTimeout_AndOneThatDid_StaysOpen()
    {
        await using var rig = ServerRig.Start();
        rig.Server.HelloTimeout.Should().Be(TimeSpan.FromSeconds(5));
        rig.Server.HelloTimeout = TimeSpan.FromMilliseconds(200);
        using var greeted = await RawLink.ConnectAsync(rig.Server);
        await greeted.HelloAsync();
        using var silent = await RawLink.ConnectAsync(rig.Server);

        (await silent.ReceiveOrClosedAsync()).Should().BeNull("a connection that sends no Hello is closed");
        await WaitUntilAsync(() => rig.Server.OpenConnections == 1);
        (await greeted.RequestAsync(Read(2, 0, 1))).Kind.Should().Be(HatEmulatorFrameKind.Ok, "the Hello timeout ends with the Hello");

        FluentActions.Invoking(() => rig.Server.HelloTimeout = TimeSpan.Zero).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => rig.Server.HelloTimeout = TimeSpan.FromDays(30)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task ARequestWhoseConnectionClosedWhileItWaitedItsTurn_IsNotRun()
    {
        var plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
        using var target = new GatedTarget(new EmulatedHatRegisterClient(plant, timing: EmulatedBusTiming.Instant));
        await using var server = new HatEmulatorServer(() => target, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var first = await RawLink.ConnectAsync(server);
        await first.HelloAsync();
        var second = await RawLink.ConnectAsync(server);
        await second.HelloAsync();

        // The first connection's read holds the access gate; the second sends a write, then gives up and closes.
        await first.SendAsync(Read(2, SmI010Board.RelayValueRegister, 1));
        (await target.Held.Task.WaitAsync(ReceiveTimeout)).Should().BeTrue();
        await second.SendAsync(Write(2, SmI010Board.RelayValueRegister, 0x08));
        second.Dispose();
        target.Release();

        (await first.ReceiveAsync())!.Value.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        await WaitUntilAsync(() => server.OpenConnections == 1);
        server.Requests.Should().Be(1, "the closed connection's write was not run");
        plant.Hat.RelayRegister.Should().Be(0, "a write the client gave up on must not land later");
        (await first.RequestAsync(Write(3, SmI010Board.RelayValueRegister, 0x01))).Kind.Should().Be(HatEmulatorFrameKind.Ok);
        plant.Hat.RelayRegister.Should().Be(0x01);
    }

    [TestMethod]
    public async Task ConnectionsDroppedAsTheyAreAccepted_AndDisposal_LeaveNoConnectionsOrErrors()
    {
        var rig = ServerRig.Start();
        using var stop = new CancellationTokenSource();
        var dropper = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.Server.DisconnectAll();
                await Task.Yield();
            }
        });

        for (var i = 0; i < 50; i++)
        {
            using var link = await RawLink.ConnectAsync(rig.Server);
        }

        await WaitUntilAsync(() => rig.Server.AcceptedConnections == 50);
        await stop.CancelAsync();
        await dropper;
        await rig.DisposeAsync().AsTask().WaitAsync(ReceiveTimeout);

        rig.Server.OpenConnections.Should().Be(0);
    }

    [TestMethod]
    public void Constructor_RefusesNullArguments()
    {
        FluentActions.Invoking(() => new HatEmulatorServer(null!, new IPEndPoint(IPAddress.Loopback, 0))).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new HatEmulatorServer(() => null!, null!)).Should().Throw<ArgumentNullException>();
    }

    private static HatEmulatorFrame Read(uint sequence, byte register, ushort count)
        => new(HatEmulatorFrameKind.Read, sequence, register, count, ReadOnlyMemory<byte>.Empty);

    private static HatEmulatorFrame Write(uint sequence, byte register, byte value)
        => new(HatEmulatorFrameKind.Write, sequence, register, 1, new[] { value });

    /// <summary>What the target returns for an access made in process, without counting it.</summary>
    private static byte[] Direct(ServerRig rig, Func<EmulatedHatRegisterClient, byte[]> access)
    {
        using var client = new EmulatedHatRegisterClient(rig.Plant, timing: EmulatedBusTiming.Instant);
        return access(client);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.Should().BeLessThan(ReceiveTimeout, "the condition should hold soon");
            await Task.Delay(10);
        }
    }

    private sealed class ServerRig : IAsyncDisposable
    {
        private ServerRig(int busId)
        {
            Plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
            Target = new EmulatedHatRegisterClient(Plant, busId, EmulatedBusTiming.Instant);
            Server = new HatEmulatorServer(() => Target, new IPEndPoint(IPAddress.Loopback, 0));
            Server.Start();
        }

        public RoofPlant Plant { get; }

        public EmulatedHatRegisterClient Target { get; }

        public HatEmulatorServer Server { get; }

        public static ServerRig Start(int busId = 1) => new(busId);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Target.Dispose();
        }
    }

    /// <summary>A target whose next read waits until <see cref="Release"/>, to hold the server's access gate.</summary>
    private sealed class GatedTarget(EmulatedHatRegisterClient inner) : II2cRegisterClient
    {
        private readonly ManualResetEventSlim _released = new();
        private int _held;

        public TaskCompletionSource<bool> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public I2cConnectionSettings ConnectionSettings => inner.ConnectionSettings;

        public object SyncRoot => inner.SyncRoot;

        public void Release() => _released.Set();

        public byte ReadByte(byte register) => inner.ReadByte(register);

        public ushort ReadUInt16(byte register) => inner.ReadUInt16(register);

        public uint ReadUInt32(byte register) => inner.ReadUInt32(register);

        public void ReadBlock(byte register, Span<byte> destination)
        {
            if (Interlocked.Exchange(ref _held, 1) == 0)
            {
                Held.TrySetResult(true);
                _released.Wait(ReceiveTimeout).Should().BeTrue("the test releases the held read");
            }

            inner.ReadBlock(register, destination);
        }

        public void WriteByte(byte register, byte value) => inner.WriteByte(register, value);

        public void WriteUInt16(byte register, ushort value) => inner.WriteUInt16(register, value);

        public void WriteBlock(byte register, ReadOnlySpan<byte> data) => inner.WriteBlock(register, data);

        public void Dispose()
        {
            inner.Dispose();
            _released.Dispose();
        }
    }

    /// <summary>A protocol client with no reconnect or checks of its own.</summary>
    private sealed class RawLink : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private RawLink(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawLink> ConnectAsync(HatEmulatorServer server)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port);
            return new RawLink(client);
        }

        public async Task SendAsync(HatEmulatorFrame frame)
        {
            using var timeout = new CancellationTokenSource(ReceiveTimeout);
            await HatEmulatorProtocol.WriteFrameAsync(_stream, frame, timeout.Token);
        }

        public async Task SendRawAsync(byte[] bytes)
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task<HatEmulatorFrame?> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(ReceiveTimeout);
            return await HatEmulatorProtocol.ReadFrameAsync(_stream, timeout.Token);
        }

        /// <summary>The next frame, or null when the server closed or reset the connection.</summary>
        public async Task<HatEmulatorFrame?> ReceiveOrClosedAsync()
        {
            try
            {
                return await ReceiveAsync();
            }
            catch (System.IO.IOException)
            {
                return null;
            }
        }

        public async Task<HatEmulatorFrame> RequestAsync(HatEmulatorFrame request)
        {
            await SendAsync(request);
            var response = await ReceiveAsync();
            response.Should().NotBeNull("the server answers every request");
            return response!.Value;
        }

        public async Task HelloAsync()
        {
            var reply = await RequestAsync(HatEmulatorProtocol.CreateHello(1));
            reply.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        }

        public void Dispose() => _client.Dispose();
    }
}
