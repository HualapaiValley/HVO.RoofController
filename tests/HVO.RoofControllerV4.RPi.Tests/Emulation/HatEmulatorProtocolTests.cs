using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Emulation;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>The wire format between the controller's socket register client and the HAT emulator.</summary>
[TestClass]
public sealed class HatEmulatorProtocolTests
{
    [TestMethod]
    public void Encode_WritesTheLengthAndTheHeaderBigEndian_AndDecodeBodyReadsItBack()
    {
        var frame = new HatEmulatorFrame(HatEmulatorFrameKind.Write, 0x01020304, 0x05, 3, new byte[] { 0xAA, 0xBB, 0xCC });

        var bytes = HatEmulatorProtocol.Encode(frame);

        bytes.Should().Equal(0x00, 0x0B, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00, 0x03, 0xAA, 0xBB, 0xCC);
        HatEmulatorProtocol.ReadBodyLength(bytes).Should().Be(11);
        var decoded = HatEmulatorProtocol.DecodeBody(bytes.AsSpan(2));
        decoded.Kind.Should().Be(HatEmulatorFrameKind.Write);
        decoded.Sequence.Should().Be(0x01020304u);
        decoded.Register.Should().Be(0x05);
        decoded.Count.Should().Be(3);
        decoded.Payload.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [TestMethod]
    public void Encode_AcceptsTheLargestPayload_AndRefusesALongerOne()
    {
        var largest = new HatEmulatorFrame(HatEmulatorFrameKind.IoError, 1, 0, 0, new byte[HatEmulatorProtocol.MaxPayloadLength]);
        HatEmulatorProtocol.ReadBodyLength(HatEmulatorProtocol.Encode(largest)).Should().Be(HatEmulatorProtocol.MaxBodyLength);

        var tooLong = largest with { Payload = new byte[HatEmulatorProtocol.MaxPayloadLength + 1] };
        FluentActions.Invoking(() => HatEmulatorProtocol.Encode(tooLong)).Should().Throw<ArgumentException>()
            .WithMessage($"The payload is longer than {HatEmulatorProtocol.MaxPayloadLength} bytes.*");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    [DataRow(2049)]
    [DataRow(65535)]
    public void ReadBodyLength_RefusesALengthOutsideTheLimits(int length)
    {
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)length);

        FluentActions.Invoking(() => HatEmulatorProtocol.ReadBodyLength(prefix)).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage($"Frame body length {length} is outside 8-2048.");
    }

    [TestMethod]
    public void ReadBodyLength_AcceptsTheLimits_AndNeedsTwoBytes()
    {
        HatEmulatorProtocol.ReadBodyLength(new byte[] { 0x00, 0x08 }).Should().Be(8);
        HatEmulatorProtocol.ReadBodyLength(new byte[] { 0x08, 0x00 }).Should().Be(2048);
        FluentActions.Invoking(() => HatEmulatorProtocol.ReadBodyLength(new byte[] { 0x00 })).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    [DataRow((byte)0x00)]
    [DataRow((byte)0x04)]
    [DataRow((byte)0x7F)]
    [DataRow((byte)0x83)]
    [DataRow((byte)0xFF)]
    public void DecodeBody_RefusesAnUnknownKind(byte kind)
    {
        var body = new byte[HatEmulatorProtocol.HeaderLength];
        body[0] = kind;

        FluentActions.Invoking(() => HatEmulatorProtocol.DecodeBody(body)).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage($"Unknown frame kind 0x{kind:X2}.");
    }

    [TestMethod]
    public void DecodeBody_RefusesABodyShorterThanTheHeader()
    {
        FluentActions.Invoking(() => HatEmulatorProtocol.DecodeBody(new byte[7])).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage("Frame body length 7 is outside 8-2048.");
    }

    [TestMethod]
    public void DecodeBody_CopiesThePayload()
    {
        var bytes = HatEmulatorProtocol.Encode(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, 1, 0, 1, new byte[] { 0x42 }));

        var decoded = HatEmulatorProtocol.DecodeBody(bytes.AsSpan(2));
        bytes[^1] = 0x00;

        decoded.Payload.ToArray().Should().Equal(0x42);
    }

    [TestMethod]
    public async Task ReadFrameAsync_ReadsConsecutiveFrames_ThenReturnsNullAtACleanEnd()
    {
        var first = new HatEmulatorFrame(HatEmulatorFrameKind.Read, 1, 3, 1, ReadOnlyMemory<byte>.Empty);
        var second = new HatEmulatorFrame(HatEmulatorFrameKind.Write, 2, 0, 2, new byte[] { 0x01, 0x02 });
        using var stream = new MemoryStream(HatEmulatorProtocol.Encode(first).Concat(HatEmulatorProtocol.Encode(second)).ToArray());

        var one = (await HatEmulatorProtocol.ReadFrameAsync(stream, CancellationToken.None))!.Value;
        var two = (await HatEmulatorProtocol.ReadFrameAsync(stream, CancellationToken.None))!.Value;
        var end = await HatEmulatorProtocol.ReadFrameAsync(stream, CancellationToken.None);

        one.Sequence.Should().Be(1u);
        one.Kind.Should().Be(HatEmulatorFrameKind.Read);
        two.Payload.ToArray().Should().Equal(0x01, 0x02);
        end.Should().BeNull();
    }

    [TestMethod]
    public async Task ReadFrameAsync_ReassemblesAFrameThatArrivesOneByteAtATime()
    {
        var frame = new HatEmulatorFrame(HatEmulatorFrameKind.Ok, 77, 0x78, 4, new byte[] { 1, 2, 3, 4 });
        using var stream = new OneByteAtATimeStream(HatEmulatorProtocol.Encode(frame));

        var read = (await HatEmulatorProtocol.ReadFrameAsync(stream, CancellationToken.None))!.Value;

        read.Sequence.Should().Be(77u);
        read.Register.Should().Be(0x78);
        read.Payload.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(9)]
    public async Task ReadFrameAsync_AnEndInsideAFrame_ThrowsEndOfStream(int available)
    {
        var bytes = HatEmulatorProtocol.Encode(new HatEmulatorFrame(HatEmulatorFrameKind.Read, 1, 0, 1, ReadOnlyMemory<byte>.Empty));
        using var stream = new MemoryStream(bytes[..available]);

        await FluentActions.Awaiting(async () => await HatEmulatorProtocol.ReadFrameAsync(stream, CancellationToken.None))
            .Should().ThrowAsync<EndOfStreamException>();
    }

    [TestMethod]
    public async Task WriteFrameAsync_WritesTheEncodedFrame()
    {
        var frame = new HatEmulatorFrame(HatEmulatorFrameKind.Write, 9, 1, 1, new byte[] { 0x08 });
        using var stream = new MemoryStream();

        await HatEmulatorProtocol.WriteFrameAsync(stream, frame, CancellationToken.None);

        stream.ToArray().Should().Equal(HatEmulatorProtocol.Encode(frame));
    }

    [TestMethod]
    public void Hello_CarriesTheMagicAndTheVersion_AndPassesTheCheck()
    {
        var hello = HatEmulatorProtocol.CreateHello(5);

        hello.Kind.Should().Be(HatEmulatorFrameKind.Hello);
        hello.Sequence.Should().Be(5u);
        hello.Payload.ToArray().Should().Equal((byte)'H', (byte)'V', (byte)'O', (byte)'H', 0x00, 0x01);
        HatEmulatorProtocol.CheckHello(hello).Should().BeNull();
    }

    [TestMethod]
    public void CheckHello_RefusesAnotherKind_AnotherMagic_AndAnotherVersion()
    {
        HatEmulatorProtocol.CheckHello(new HatEmulatorFrame(HatEmulatorFrameKind.Read, 1, 0, 1, ReadOnlyMemory<byte>.Empty))
            .Should().Be("Expected Hello first, received Read.");
        HatEmulatorProtocol.CheckHello(new HatEmulatorFrame(HatEmulatorFrameKind.Hello, 1, 0, 0, "HVOX\0\u0001"u8.ToArray()))
            .Should().Be("Hello does not carry the HVOH magic.");
        HatEmulatorProtocol.CheckHello(new HatEmulatorFrame(HatEmulatorFrameKind.Hello, 1, 0, 0, "HVOH"u8.ToArray()))
            .Should().Be("Hello does not carry the HVOH magic.");
        HatEmulatorProtocol.CheckHello(HatEmulatorProtocol.CreateHello(1, version: 2))
            .Should().Be("Protocol version 2 is not supported; the emulator speaks version 1.");
    }

    [TestMethod]
    public void HelloReply_CarriesTheBusAndTheAddress()
    {
        var reply = HatEmulatorProtocol.CreateHelloReply(9, busId: 1, address: 0x0E);

        reply.Kind.Should().Be(HatEmulatorFrameKind.Ok);
        reply.Sequence.Should().Be(9u);
        HatEmulatorProtocol.ReadHelloReply(reply).Should().Be((1, 0x0E));
        HatEmulatorProtocol.ReadHelloReply(HatEmulatorProtocol.CreateHelloReply(1, busId: 65535, address: 0x7F)).Should().Be((65535, 0x7F));
    }

    [TestMethod]
    [DataRow(-1, 0x0E)]
    [DataRow(65536, 0x0E)]
    [DataRow(1, -1)]
    [DataRow(1, 0x80)]
    public void CreateHelloReply_RefusesABusOrAddressOutOfRange(int busId, int address)
    {
        FluentActions.Invoking(() => HatEmulatorProtocol.CreateHelloReply(1, busId, address)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ReadHelloReply_ThrowsForARefusal_AnotherMagic_AndAnotherVersion()
    {
        var refusal = HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, 1, "Protocol version 2 is not supported; the emulator speaks version 1.");
        FluentActions.Invoking(() => HatEmulatorProtocol.ReadHelloReply(refusal)).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage("The emulator refused the connection: ProtocolError Protocol version 2 is not supported; the emulator speaks version 1.");

        var reply = HatEmulatorProtocol.CreateHelloReply(1, 1, 0x0E);
        var otherMagic = reply.Payload.ToArray();
        otherMagic[0] = (byte)'X';
        FluentActions.Invoking(() => HatEmulatorProtocol.ReadHelloReply(reply with { Payload = otherMagic })).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage("The Hello reply does not carry the HVOH magic.");
        FluentActions.Invoking(() => HatEmulatorProtocol.ReadHelloReply(reply with { Payload = reply.Payload[..8] })).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage("The Hello reply does not carry the HVOH magic.");

        var otherVersion = reply.Payload.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(otherVersion.AsSpan(4), 2);
        FluentActions.Invoking(() => HatEmulatorProtocol.ReadHelloReply(reply with { Payload = otherVersion })).Should().Throw<HatEmulatorProtocolException>()
            .WithMessage("The emulator speaks protocol version 2; this controller speaks 1.");
    }

    [TestMethod]
    [DataRow(HatEmulatorFrameKind.Read, 3, 1, 0, null)]
    [DataRow(HatEmulatorFrameKind.Read, 0, 256, 0, null)]
    [DataRow(HatEmulatorFrameKind.Read, 0xFF, 1, 0, null)]
    [DataRow(HatEmulatorFrameKind.Read, 5, 0, 0, null)]
    [DataRow(HatEmulatorFrameKind.Write, 0, 2, 2, null)]
    [DataRow(HatEmulatorFrameKind.Write, 5, 0, 0, null)]
    [DataRow(HatEmulatorFrameKind.Read, 3, 1, 1, "A Read carries no payload.")]
    [DataRow(HatEmulatorFrameKind.Write, 0, 2, 1, "A Write of 2 registers carries 1 bytes.")]
    [DataRow(HatEmulatorFrameKind.Hello, 0, 0, 0, "Expected Read or Write, received Hello.")]
    [DataRow(HatEmulatorFrameKind.Ok, 0, 0, 0, "Expected Read or Write, received Ok.")]
    [DataRow(HatEmulatorFrameKind.Read, 0, 257, 0, "An access covers at most 256 registers; received 257.")]
    [DataRow(HatEmulatorFrameKind.Write, 0, 257, 257, "An access covers at most 256 registers; received 257.")]
    [DataRow(HatEmulatorFrameKind.Read, 0xFF, 2, 0, "The access runs past register 0xFF.")]
    [DataRow(HatEmulatorFrameKind.Write, 0xF8, 9, 9, "The access runs past register 0xFF.")]
    public void CheckAccess_AcceptsTheHatsAccessShapes_AndNamesTheProblemOtherwise(
        HatEmulatorFrameKind kind, int register, int count, int payloadLength, string? expected)
    {
        var frame = new HatEmulatorFrame(kind, 1, (byte)register, (ushort)count, new byte[payloadLength]);

        HatEmulatorProtocol.CheckAccess(frame).Should().Be(expected);
    }

    [TestMethod]
    public void CreateError_CarriesTheMessage_ForTheErrorKindsOnly()
    {
        var error = HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.IoError, 4, "Injected I2C read failure at register 3.");

        error.Kind.Should().Be(HatEmulatorFrameKind.IoError);
        error.Sequence.Should().Be(4u);
        error.PayloadText.Should().Be("Injected I2C read failure at register 3.");
        HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, 1, null!).PayloadText.Should().BeEmpty();
        FluentActions.Invoking(() => HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.Ok, 1, "x")).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.Read, 1, "x")).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void CreateError_CutsALongMessageToTheLargestPayload()
    {
        var error = HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.IoError, 1, new string('x', 5000));

        error.Payload.Length.Should().Be(HatEmulatorProtocol.MaxPayloadLength);
        HatEmulatorProtocol.ReadBodyLength(HatEmulatorProtocol.Encode(error)).Should().Be(HatEmulatorProtocol.MaxBodyLength);
    }

    [TestMethod]
    [DataRow("a", "€")]
    [DataRow("ab", "€")]
    [DataRow("", "\U0001F52D")]
    [DataRow("abc", "\U0001F52D")]
    public void CreateError_CutsAtACharacterBoundary(string prefix, string character)
    {
        var message = prefix + string.Concat(Enumerable.Repeat(character, 1000));

        var error = HatEmulatorProtocol.CreateError(HatEmulatorFrameKind.ProtocolError, 1, message);

        error.Payload.Length.Should().BeLessThanOrEqualTo(HatEmulatorProtocol.MaxPayloadLength)
            .And.BeGreaterThan(HatEmulatorProtocol.MaxPayloadLength - System.Text.Encoding.UTF8.GetByteCount(character));
        error.PayloadText.Should().NotContain("�");
        message.Should().StartWith(error.PayloadText);
    }

    /// <summary>A stream that returns at most one byte per read, as a fragmented TCP stream may.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
