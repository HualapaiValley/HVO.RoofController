using System.Buffers.Binary;
using System.Text;

namespace HVO.RoofControllerV4.Common.Emulation;

/// <summary>The frame kinds of the HAT emulator protocol. Requests are below 0x80, responses at 0x80 and above.</summary>
public enum HatEmulatorFrameKind : byte
{
    /// <summary>Request: the protocol check, sent first on each connection. Payload: the magic and the version.</summary>
    Hello = 0x01,

    /// <summary>Request: read <c>Count</c> registers from <c>Register</c>. No payload.</summary>
    Read = 0x02,

    /// <summary>Request: write the payload (<c>Count</c> bytes) from <c>Register</c>.</summary>
    Write = 0x03,

    /// <summary>
    /// Response: the request succeeded. For Hello the payload is the magic, the version, the bus and the address; for Read it
    /// is the register values; for Write it is empty.
    /// </summary>
    Ok = 0x80,

    /// <summary>
    /// Response: the register access failed as an I2C transfer fails (an injected bus failure, a powered-down HAT). The
    /// payload is the message. The connection stays up.
    /// </summary>
    IoError = 0x81,

    /// <summary>Response: the request was malformed or out of order. The payload is the message; the server then closes the connection.</summary>
    ProtocolError = 0x82
}

/// <summary>One frame: the header fields and the payload.</summary>
public readonly record struct HatEmulatorFrame(HatEmulatorFrameKind Kind, uint Sequence, byte Register, ushort Count, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The payload as UTF-8 text, for the error responses.</summary>
    public string PayloadText => Encoding.UTF8.GetString(Payload.Span);
}

/// <summary>A malformed frame or an unexpected response. The connection cannot be trusted after it.</summary>
public sealed class HatEmulatorProtocolException : IOException
{
    public HatEmulatorProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The wire format between the controller's socket register client and the HAT emulator. Each frame is a big-endian
/// <c>u16</c> body length, then the body: <c>u8</c> kind, <c>u32</c> sequence, <c>u8</c> register, <c>u16</c> count and the
/// payload. A response carries its request's sequence number. The register accesses have the SM-I-010's shape: up to
/// 256 registers from an 8-bit address, never past 0xFF.
/// </summary>
public static class HatEmulatorProtocol
{
    /// <summary>The protocol version; a Hello with another version is refused.</summary>
    public const ushort Version = 1;

    /// <summary>The bytes before the body: its length.</summary>
    public const int LengthPrefixLength = 2;

    /// <summary>The body's fixed part: kind, sequence, register and count.</summary>
    public const int HeaderLength = 8;

    /// <summary>The largest block one access may read or write.</summary>
    public const int MaxBlockLength = 256;

    /// <summary>The largest body either side accepts.</summary>
    public const int MaxBodyLength = 2048;

    /// <summary>The largest payload: an error message longer than this is cut.</summary>
    public const int MaxPayloadLength = MaxBodyLength - HeaderLength;

    private const int HelloReplyPayloadLength = 9;

    /// <summary>The first four payload bytes of a Hello and its reply.</summary>
    public static ReadOnlySpan<byte> Magic => "HVOH"u8;

    /// <summary>Encodes the frame with its length prefix.</summary>
    public static byte[] Encode(in HatEmulatorFrame frame)
    {
        if (frame.Payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException($"The payload is longer than {MaxPayloadLength} bytes.", nameof(frame));
        }

        var bodyLength = HeaderLength + frame.Payload.Length;
        var buffer = new byte[LengthPrefixLength + bodyLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)bodyLength);
        span[2] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(span[3..], frame.Sequence);
        span[7] = frame.Register;
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], frame.Count);
        frame.Payload.Span.CopyTo(span[(LengthPrefixLength + HeaderLength)..]);
        return buffer;
    }

    /// <summary>Reads the body length from the prefix and checks it.</summary>
    public static int ReadBodyLength(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < LengthPrefixLength)
        {
            throw new ArgumentException("The length prefix is two bytes.", nameof(prefix));
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length is < HeaderLength or > MaxBodyLength)
        {
            throw new HatEmulatorProtocolException($"Frame body length {length} is outside {HeaderLength}-{MaxBodyLength}.");
        }

        return length;
    }

    /// <summary>Decodes a body (without the length prefix). The payload is copied.</summary>
    public static HatEmulatorFrame DecodeBody(ReadOnlySpan<byte> body)
    {
        if (body.Length is < HeaderLength or > MaxBodyLength)
        {
            throw new HatEmulatorProtocolException($"Frame body length {body.Length} is outside {HeaderLength}-{MaxBodyLength}.");
        }

        var kind = (HatEmulatorFrameKind)body[0];
        if (!Enum.IsDefined(kind))
        {
            throw new HatEmulatorProtocolException($"Unknown frame kind 0x{body[0]:X2}.");
        }

        return new HatEmulatorFrame(
            kind,
            BinaryPrimitives.ReadUInt32BigEndian(body[1..]),
            body[5],
            BinaryPrimitives.ReadUInt16BigEndian(body[6..]),
            body[HeaderLength..].ToArray());
    }

    /// <summary>
    /// Reads one frame. Returns null when the stream ends before the frame's first byte (the peer closed the connection);
    /// an end inside a frame throws <see cref="EndOfStreamException"/>.
    /// </summary>
    public static async ValueTask<HatEmulatorFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[LengthPrefixLength];
        var first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var body = new byte[ReadBodyLength(prefix)];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return DecodeBody(body);
    }

    /// <summary>Writes one frame and flushes.</summary>
    public static async ValueTask WriteFrameAsync(Stream stream, HatEmulatorFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(Encode(frame), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Hello request.</summary>
    public static HatEmulatorFrame CreateHello(uint sequence) => new(HatEmulatorFrameKind.Hello, sequence, 0, 0, HelloPayload(Version));

    /// <summary>The Hello request with another version, for tests of the version check.</summary>
    public static HatEmulatorFrame CreateHello(uint sequence, ushort version) => new(HatEmulatorFrameKind.Hello, sequence, 0, 0, HelloPayload(version));

    /// <summary>The Ok reply to a Hello: the magic, the version and the HAT's bus and address.</summary>
    public static HatEmulatorFrame CreateHelloReply(uint sequence, int busId, int address)
    {
        if (busId is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(busId));
        }

        if (address is < 0 or > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        var payload = new byte[HelloReplyPayloadLength];
        Magic.CopyTo(payload);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), (ushort)busId);
        payload[8] = (byte)address;
        return new HatEmulatorFrame(HatEmulatorFrameKind.Ok, sequence, 0, 0, payload);
    }

    /// <summary>Checks a Hello request; returns the problem, or null when the peer speaks this version.</summary>
    public static string? CheckHello(in HatEmulatorFrame frame)
    {
        if (frame.Kind != HatEmulatorFrameKind.Hello)
        {
            return $"Expected Hello first, received {frame.Kind}.";
        }

        var payload = frame.Payload.Span;
        if (payload.Length != Magic.Length + 2 || !payload[..Magic.Length].SequenceEqual(Magic))
        {
            return "Hello does not carry the HVOH magic.";
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(payload[Magic.Length..]);
        return version == Version ? null : $"Protocol version {version} is not supported; the emulator speaks version {Version}.";
    }

    /// <summary>Reads the Hello reply: the HAT's bus and address. Throws when the reply is not a matching Hello reply.</summary>
    public static (int BusId, int Address) ReadHelloReply(in HatEmulatorFrame frame)
    {
        if (frame.Kind != HatEmulatorFrameKind.Ok)
        {
            throw new HatEmulatorProtocolException($"The emulator refused the connection: {frame.Kind} {frame.PayloadText}");
        }

        var payload = frame.Payload.Span;
        if (payload.Length != HelloReplyPayloadLength || !payload[..Magic.Length].SequenceEqual(Magic))
        {
            throw new HatEmulatorProtocolException("The Hello reply does not carry the HVOH magic.");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        if (version != Version)
        {
            throw new HatEmulatorProtocolException($"The emulator speaks protocol version {version}; this controller speaks {Version}.");
        }

        return (BinaryPrimitives.ReadUInt16BigEndian(payload[6..]), payload[8]);
    }

    /// <summary>Checks a Read or Write request's shape; returns the problem, or null when it is valid.</summary>
    public static string? CheckAccess(in HatEmulatorFrame frame)
    {
        switch (frame.Kind)
        {
            case HatEmulatorFrameKind.Read:
                if (!frame.Payload.IsEmpty)
                {
                    return "A Read carries no payload.";
                }

                break;
            case HatEmulatorFrameKind.Write:
                if (frame.Payload.Length != frame.Count)
                {
                    return $"A Write of {frame.Count} registers carries {frame.Payload.Length} bytes.";
                }

                break;
            default:
                return $"Expected Read or Write, received {frame.Kind}.";
        }

        if (frame.Count > MaxBlockLength)
        {
            return $"An access covers at most {MaxBlockLength} registers; received {frame.Count}.";
        }

        return frame.Register + frame.Count > MaxBlockLength ? "The access runs past register 0xFF." : null;
    }

    /// <summary>An error response with the message as UTF-8 (cut to the largest payload).</summary>
    public static HatEmulatorFrame CreateError(HatEmulatorFrameKind kind, uint sequence, string message)
    {
        if (kind is not (HatEmulatorFrameKind.IoError or HatEmulatorFrameKind.ProtocolError))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        if (bytes.Length > MaxPayloadLength)
        {
            // Cut at a character boundary: back off while the first dropped byte continues a character.
            var cut = MaxPayloadLength;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            {
                cut--;
            }

            bytes = bytes[..cut];
        }

        return new HatEmulatorFrame(kind, sequence, 0, 0, bytes);
    }

    private static byte[] HelloPayload(ushort version)
    {
        var payload = new byte[Magic.Length + 2];
        Magic.CopyTo(payload);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(Magic.Length), version);
        return payload;
    }
}
