using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Emulation;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The controller's register client for the HAT emulator: framing, the Hello check, timeouts, disconnects and
/// reconnects, against a scripted stand-in emulator and against the real emulator server.
/// </summary>
[TestClass]
public sealed class SocketI2cRegisterClientTests
{
    /// <summary>
    /// The request timeout of the tests whose subject is a timeout. The accesses in those tests that must succeed share it,
    /// so it leaves a loaded runner's thread pool time to run the stand-in emulator's answers.
    /// </summary>
    private static readonly TimeSpan TimeoutUnderTest = TimeSpan.FromSeconds(1);

    private static HatEmulatorOptions Options(int port, TimeSpan? requestTimeout = null, TimeSpan? connectTimeout = null) => new()
    {
        Enabled = true,
        Host = "127.0.0.1",
        Port = port,
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5)
    };

    private static (SocketI2cRegisterClient Client, CapturingLogger<SocketI2cRegisterClient> Log) Connect(
        int port, TimeSpan? requestTimeout = null, int address = 0x0E, TimeSpan? connectTimeout = null)
    {
        var log = new CapturingLogger<SocketI2cRegisterClient>();
        return (new SocketI2cRegisterClient(Options(port, requestTimeout, connectTimeout), busId: 1, address, log), log);
    }

    [TestMethod]
    public async Task Construction_DoesNotConnect()
    {
        await using var emulator = new ScriptedHatEmulator(c => c.HoldAsync());
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            await Task.Delay(50);

            emulator.Accepted.Should().Be(0);
            client.IsConnected.Should().BeFalse();
            client.ConnectCount.Should().Be(0);
            client.Endpoint.Should().Be($"127.0.0.1:{emulator.Port}");
            client.ConnectionSettings.BusId.Should().Be(1);
            client.ConnectionSettings.DeviceAddress.Should().Be(0x0E);
        }
    }

    [TestMethod]
    public void Construction_RefusesInvalidOptions()
    {
        var options = Options(0);

        FluentActions.Invoking(() => new SocketI2cRegisterClient(options, 1, 0x0E, new CapturingLogger<SocketI2cRegisterClient>()))
            .Should().Throw<ArgumentException>().WithMessage("HatEmulator:Port must be between 1 and 65535.*");
    }

    [TestMethod]
    [DataRow("emulator:5291")]
    [DataRow("192.0.2.10:5291")]
    [DataRow("http://emulator")]
    [DataRow("roof pi")]
    [DataRow("emulator/")]
    public void AHostWithASchemeAPortOrSpaces_IsRefused(string host)
    {
        var options = Options(HatEmulatorOptions.DefaultPort);
        options.Host = host;

        options.Validate().Should().Equal("HatEmulator:Host must be a host name or an IP address, without a scheme or a port (the port is HatEmulator:Port).");
        FluentActions.Invoking(() => new SocketI2cRegisterClient(options, 1, 0x0E, new CapturingLogger<SocketI2cRegisterClient>()))
            .Should().Throw<ArgumentException>().WithMessage("HatEmulator:Host must be a host name or an IP address*");
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("192.0.2.10")]
    [DataRow("::1")]
    [DataRow("localhost")]
    [DataRow("hat-emulator")]
    [DataRow("test-pi.local")]
    public void AHostNameOrAddress_IsAccepted(string host)
    {
        var options = Options(HatEmulatorOptions.DefaultPort);
        options.Host = host;

        options.Validate().Should().BeEmpty();
    }

    [TestMethod]
    public async Task FirstAccess_SendsTheHelloFirst_ThenEachAccessWithTheNextSequenceNumber()
    {
        var registers = new byte[256];
        registers[3] = 0x42;
        await using var emulator = new ScriptedHatEmulator(c => ScriptedHatEmulator.ServeRegistersAsync(c, registers));
        var (client, log) = Connect(emulator.Port);
        using (client)
        {
            client.ReadByte(3).Should().Be(0x42);
            client.WriteBlock(0, new byte[] { 0x09, 0x0A });
            client.ReadByte(1).Should().Be(0x0A);

            var received = emulator.Received;
            received.Select(f => f.Kind).Should().Equal(HatEmulatorFrameKind.Hello, HatEmulatorFrameKind.Read, HatEmulatorFrameKind.Write, HatEmulatorFrameKind.Read);
            received.Select(f => f.Sequence).Should().Equal(1u, 2u, 3u, 4u);
            HatEmulatorProtocol.CheckHello(received[0]).Should().BeNull();
            received[1].Register.Should().Be(3);
            received[1].Count.Should().Be(1);
            received[2].Payload.ToArray().Should().Equal(0x09, 0x0A);
            registers[0].Should().Be(0x09);
            emulator.Accepted.Should().Be(1);
            client.IsConnected.Should().BeTrue();
            client.ConnectCount.Should().Be(1);
            log.MessagesAt(LogLevel.Information).Should().Equal($"Connected to the HAT emulator at 127.0.0.1:{emulator.Port}");
        }
    }

    [TestMethod]
    public async Task WideAccesses_AreLittleEndian_AsOnTheI2cClient()
    {
        var registers = new byte[256];
        await using var emulator = new ScriptedHatEmulator(c => ScriptedHatEmulator.ServeRegistersAsync(c, registers));
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.WriteUInt16(0x40, 0xBEEF);
            client.WriteByte(0x42, 0x01);

            registers[0x40].Should().Be(0xEF);
            registers[0x41].Should().Be(0xBE);
            client.ReadUInt16(0x40).Should().Be(0xBEEF);
            client.ReadUInt32(0x40).Should().Be(0x0001BEEFu);
            var block = new byte[3];
            client.ReadBlock(0x40, block);
            block.Should().Equal(0xEF, 0xBE, 0x01);
        }
    }

    [TestMethod]
    public async Task AnEmulatorForAnotherBusOrAddress_IsRefused_AndTheFailureIsLoggedOnce()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync(busId: 1, address: 0x0F);
            await c.HoldAsync();
        });
        var (client, log) = Connect(emulator.Port);
        using (client)
        {
            var expected = $"The HAT emulator at 127.0.0.1:{emulator.Port} emulates bus 1 address 0x0F; the controller expects bus 1 address 0x0E.";
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage(expected);
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage(expected);

            emulator.Accepted.Should().Be(2, "each access tries a new connection");
            client.IsConnected.Should().BeFalse();
            client.ConnectCount.Should().Be(0);
            log.MessagesAt(LogLevel.Warning).Should().Equal($"HAT emulator unavailable: {expected}");
        }
    }

    [TestMethod]
    public async Task ARefusedHello_FailsTheAccess()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            var hello = await c.ReceiveAsync();
            await c.SendAsync(HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, hello!.Value.Sequence, "Protocol version 1 is not supported; the emulator speaks version 2."));
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(0)).Should().Throw<IOException>()
                .WithMessage("The emulator refused the connection: ProtocolError Protocol version 1 is not supported; the emulator speaks version 2.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public void NoEmulatorListening_FailsEachAccess_AndLogsTheFailureOnce()
    {
        // A bound socket that does not listen refuses connections, and holds the port so no other test can take it.
        using var closedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closedPort.LocalEndPoint!).Port;
        var (client, log) = Connect(port);
        using (client)
        {
            for (var i = 0; i < 3; i++)
            {
                client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>()
                    .WithMessage($"Connecting to the HAT emulator at 127.0.0.1:{port} failed: ConnectionRefused.");
            }

            log.MessagesAt(LogLevel.Warning).Should().ContainSingle()
                .Which.Should().Be($"HAT emulator unavailable: Connecting to the HAT emulator at 127.0.0.1:{port} failed: ConnectionRefused.");
        }
    }

    [TestMethod]
    public async Task AnUnansweredRequest_TimesOut_DropsTheConnection_AndTheNextAccessReconnects()
    {
        var registers = new byte[256];
        registers[3] = 0x05;
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            if (c.Index == 0)
            {
                await c.AnswerHelloAsync();
                await c.HoldAsync();
                return;
            }

            await ScriptedHatEmulator.ServeRegistersAsync(c, registers);
        });
        var (client, log) = Connect(emulator.Port, TimeoutUnderTest);
        using (client)
        {
            var stopwatch = Stopwatch.StartNew();
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("The HAT emulator did not answer within the request timeout.");
            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeoutUnderTest - TimeSpan.FromMilliseconds(20)).And.BeLessThan(TimeSpan.FromSeconds(5));
            client.IsConnected.Should().BeFalse();
            log.MessagesAt(LogLevel.Warning).Should().Equal(
                $"HAT emulator connection to 127.0.0.1:{emulator.Port} dropped: The HAT emulator did not answer within the request timeout.");

            client.ReadByte(3).Should().Be(0x05);

            client.ConnectCount.Should().Be(2);
            emulator.Accepted.Should().Be(2);
            log.MessagesAt(LogLevel.Information).Should().Equal(
                $"Connected to the HAT emulator at 127.0.0.1:{emulator.Port}",
                $"Reconnected to the HAT emulator at 127.0.0.1:{emulator.Port}");
        }
    }

    [TestMethod]
    public async Task AResponseToAnotherRequest_DropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            var request = (await c.ReceiveAsync())!.Value;
            await c.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence + 1, request.Register, 1, new byte[] { 0x01 }));
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("Response 3 does not answer request 2.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task TheEmulatorClosingDuringARequest_FailsTheAccess_AndDropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            await c.ReceiveAsync();
        });
        var (client, log) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.WriteByte(0, 0x08)).Should().Throw<IOException>().WithMessage("The HAT emulator closed the connection.");
            client.IsConnected.Should().BeFalse();
            log.Contains(LogLevel.Warning, "dropped: The HAT emulator closed the connection.").Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task AnIoErrorFromTheEmulator_FailsTheAccess_AndKeepsTheConnection()
    {
        var registers = new byte[256];
        registers[3] = 0x06;
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            var request = (await c.ReceiveAsync())!.Value;
            await c.SendAsync(HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.IoError, request.Sequence, "Injected I2C read failure at register 3."));
            while (await c.ReceiveAsync() is { } next)
            {
                await c.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, next.Sequence, next.Register, next.Count, registers.AsSpan(next.Register, next.Count).ToArray()));
            }
        });
        var (client, log) = Connect(emulator.Port);
        using (client)
        {
            var failure = client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().Which;
            failure.Message.Should().Be("Injected I2C read failure at register 3.");
            failure.Should().BeOfType<IOException>("the HAT library sees what a failed I2C transfer throws");

            client.IsConnected.Should().BeTrue();
            client.ReadByte(3).Should().Be(0x06);
            emulator.Accepted.Should().Be(1);
            log.MessagesAt(LogLevel.Warning).Should().BeEmpty();
        }
    }

    [TestMethod]
    public async Task AProtocolErrorFromTheEmulator_FailsTheAccess_AndDropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            var request = (await c.ReceiveAsync())!.Value;
            await c.SendAsync(HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, request.Sequence, "A Read carries no payload."));
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("The HAT emulator refused the request: A Read carries no payload.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task AReadAnsweredWithTheWrongLength_DropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            var request = (await c.ReceiveAsync())!.Value;
            await c.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence, request.Register, 2, new byte[] { 0x01, 0x02 }));
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("A Read of 1 registers returned 2 bytes.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task ARequestFrameInPlaceOfAResponse_DropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            var request = (await c.ReceiveAsync())!.Value;
            await c.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Read, request.Sequence, 0, 1, ReadOnlyMemory<byte>.Empty));
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("Expected a response, received Read.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task AMalformedResponse_DropsTheConnection()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            await c.ReceiveAsync();
            await c.SendRawAsync(new byte[] { 0x00, 0x03, 0x80, 0x00, 0x00 });
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadByte(3)).Should().Throw<HatEmulatorProtocolException>().WithMessage("Frame body length 3 is outside 8-2048.");
            client.IsConnected.Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task AnAccessPastRegister0xFF_FailsLocally_WithoutConnecting()
    {
        await using var emulator = new ScriptedHatEmulator(c => c.HoldAsync());
        var (client, _) = Connect(emulator.Port);
        using (client)
        {
            client.Invoking(c => c.ReadBlock(0xFF, new byte[2])).Should().Throw<ArgumentOutOfRangeException>().WithMessage("The access runs past register 0xFF.*");
            client.Invoking(c => c.WriteBlock(0xF8, new byte[9])).Should().Throw<ArgumentOutOfRangeException>().WithMessage("The access runs past register 0xFF.*");
            client.Invoking(c => c.ReadUInt32(0xFD)).Should().Throw<ArgumentOutOfRangeException>();

            await Task.Delay(50);
            emulator.Accepted.Should().Be(0);
        }
    }

    [TestMethod]
    public async Task Dispose_ClosesTheConnection_IsIdempotent_AndLaterAccessesThrow()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registers = new byte[256];
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            try
            {
                await ScriptedHatEmulator.ServeRegistersAsync(c, registers);
            }
            finally
            {
                closed.TrySetResult();
            }
        });
        var (client, _) = Connect(emulator.Port);
        client.ReadByte(0);

        client.Dispose();
        client.Dispose();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.IsConnected.Should().BeFalse();
        client.Invoking(c => c.ReadByte(0)).Should().Throw<ObjectDisposedException>();
        client.Invoking(c => c.WriteByte(0, 1)).Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    public async Task AnAccessThatConnects_SharesTheRequestTimeoutWithTheHello()
    {
        var registers = new byte[256];
        registers[3] = 0x05;
        var late = TimeoutUnderTest * 0.6;
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            if (c.Index > 0)
            {
                await ScriptedHatEmulator.ServeRegistersAsync(c, registers);
                return;
            }

            // Each answer alone is within the request timeout; the two together are not.
            var hello = (await c.ReceiveAsync())!.Value;
            await Task.Delay(late, c.Stopping);
            await c.SendAsync(HatEmulatorProtocol.CreateHelloReply(hello.Sequence, 1, 0x0E));
            var read = (await c.ReceiveAsync())!.Value;
            await Task.Delay(late, c.Stopping);
            await c.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, read.Sequence, read.Register, read.Count, new byte[read.Count]));
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port, TimeoutUnderTest);
        using (client)
        {
            var stopwatch = Stopwatch.StartNew();
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>().WithMessage("The HAT emulator did not answer within the request timeout.");
            stopwatch.Elapsed.Should().BeLessThan(late * 2, "the Hello and the access share one request timeout");

            client.ReadByte(3).Should().Be(0x05);
            client.ConnectCount.Should().Be(2, "the first connection passed its Hello; its access timed out and dropped it");
            emulator.Accepted.Should().Be(2);
        }
    }

    [TestMethod]
    public async Task Dispose_DuringAnAccess_FailsItAtOnce_WithoutWaitingForTheTimeout()
    {
        await using var emulator = new ScriptedHatEmulator(async c =>
        {
            await c.AnswerHelloAsync();
            await c.HoldAsync();
        });
        var (client, _) = Connect(emulator.Port, TimeSpan.FromSeconds(30));
        var access = Task.Run(() => client.ReadByte(3));
        await WaitUntilAsync(() => emulator.Received.Count == 2);

        var stopwatch = Stopwatch.StartNew();
        client.Dispose();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Dispose does not wait for the access");

        await access.Awaiting(a => a.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<IOException>();
        client.IsConnected.Should().BeFalse();
        client.Invoking(c => c.ReadByte(3)).Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    public async Task AConnectWithNoAnswer_FailsWithinTheConnectTimeout_AndEachAccessTriesAgain()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Only Linux is known to leave a connect to a full listen queue unanswered.");
        }

        using var unanswered = new UnansweredPort();
        var connectTimeout = TimeSpan.FromMilliseconds(500);
        var (client, log) = Connect(unanswered.Port, connectTimeout: connectTimeout);
        using (client)
        {
            for (var i = 0; i < 2; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>()
                    .WithMessage($"Connecting to the HAT emulator at 127.0.0.1:{unanswered.Port} timed out after 500 ms.");
                stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(connectTimeout - TimeSpan.FromMilliseconds(20)).And.BeLessThan(TimeSpan.FromSeconds(3));
            }

            log.MessagesAt(LogLevel.Warning).Should().ContainSingle();
            client.IsConnected.Should().BeFalse();
        }

        // Dispose aborts an attempt in progress rather than waiting out the connect timeout.
        var (waiting, _) = Connect(unanswered.Port, connectTimeout: TimeSpan.FromSeconds(30));
        var attempt = Task.Run(() => waiting.ReadByte(3));
        await Task.Delay(200);
        waiting.Dispose();
        var failure = await attempt.Awaiting(a => a.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<Exception>();
        failure.Which.Should().Match<Exception>(e => e is IOException || e is ObjectDisposedException);
    }

    [TestMethod]
    public async Task AgainstTheEmulatorServer_AnOutageFailsAccesses_AndTheClientReconnectsWhenItEnds()
    {
        var plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
        using var hat = new EmulatedHatRegisterClient(plant, timing: EmulatedBusTiming.Instant);
        await using var server = new HatEmulatorServer(() => hat, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var (client, log) = Connect(server.LocalEndPoint.Port);
        using (client)
        {
            client.ReadByte(3).Should().Be(plant.InputBits);

            server.Outage = true;
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>();
            client.Invoking(c => c.ReadByte(3)).Should().Throw<IOException>();
            client.IsConnected.Should().BeFalse();

            server.Outage = false;
            client.WriteByte(SmI010Board.RelayValueRegister, 0x08);
            client.ReadByte(SmI010Board.RelayValueRegister).Should().Be(0x08);

            client.ConnectCount.Should().Be(2);
            log.MessagesAt(LogLevel.Information).Last().Should().Be($"Reconnected to the HAT emulator at 127.0.0.1:{server.LocalEndPoint.Port}");
            log.Contains(LogLevel.Warning, $"HAT emulator connection to 127.0.0.1:{server.LocalEndPoint.Port} dropped").Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task AgainstTheEmulatorServer_AStalledResponseTimesOut_AndTheNextConnectionGetsFreshAnswers()
    {
        var plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
        using var hat = new EmulatedHatRegisterClient(plant, timing: EmulatedBusTiming.Instant);
        await using var server = new HatEmulatorServer(() => hat, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var (client, _) = Connect(server.LocalEndPoint.Port, TimeoutUnderTest);
        using (client)
        {
            client.WriteByte(SmI010Board.RelayValueRegister, 0x08);

            server.ResponseDelay = TimeoutUnderTest * 2;
            client.Invoking(c => c.ReadByte(SmI010Board.RevisionRegister)).Should().Throw<IOException>()
                .WithMessage("The HAT emulator did not answer within the request timeout.");

            server.ResponseDelay = TimeSpan.Zero;
            client.ReadByte(SmI010Board.RelayValueRegister).Should().Be(0x08);
            client.ReadByte(SmI010Board.DigitalInputRegister).Should().Be(plant.InputBits);
            client.ConnectCount.Should().Be(2);
        }
    }

    [TestMethod]
    public async Task AgainstTheEmulatorServer_ABusFailureIsAnIoError_OnTheSameConnection()
    {
        var plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
        using var hat = new EmulatedHatRegisterClient(plant, timing: EmulatedBusTiming.Instant);
        await using var server = new HatEmulatorServer(() => hat, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var (client, _) = Connect(server.LocalEndPoint.Port);
        using (client)
        {
            client.ReadByte(0);
            hat.FailNextInputReads = 1;
            plant.SetHatPower(false);

            client.Invoking(c => c.ReadByte(0)).Should().Throw<IOException>().WithMessage("SM-I-010 at 0x0E does not respond (HAT unpowered).");
            plant.SetHatPower(true);
            client.Invoking(c => c.ReadByte(SmI010Board.DigitalInputRegister)).Should().Throw<IOException>()
                .WithMessage("Injected I2C read failure at register 3.");
            client.ReadByte(SmI010Board.DigitalInputRegister).Should().Be(plant.InputBits);

            client.ConnectCount.Should().Be(1);
            server.AcceptedConnections.Should().Be(1);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the condition should hold soon");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A port whose connection attempts get no answer: its listener's queue is full, so Linux drops the SYNs (other systems
    /// may refuse them instead).
    /// </summary>
    private sealed class UnansweredPort : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly Socket _queued = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        public UnansweredPort()
        {
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(0);
            _queued.Connect(_listener.LocalEndPoint!);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

        public void Dispose()
        {
            _queued.Dispose();
            _listener.Dispose();
        }
    }
}
