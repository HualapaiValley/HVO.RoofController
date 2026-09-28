using System.Buffers.Binary;
using System.Device.I2c;
using System.Diagnostics;
using System.Net.Sockets;
using HVO.Iot.Devices.Abstractions;
using HVO.RoofControllerV4.Common.Emulation;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Services.HatEmulation;

/// <summary>
/// <see cref="II2cRegisterClient"/> that sends each register access to the HAT emulator over TCP
/// (<see cref="HatEmulatorProtocol"/>). It fails as a failed I2C transfer does: every failure is an
/// <see cref="IOException"/>, so the HAT library and the controller take the paths they take for a bus error.
/// </summary>
/// <remarks>
/// <para>
/// One access at a time. The connection opens on the first access (within <see cref="HatEmulatorOptions.ConnectTimeout"/>,
/// then the Hello check of the protocol version and the HAT address). Each access must complete within
/// <see cref="HatEmulatorOptions.RequestTimeout"/>.
/// </para>
/// <para>
/// A timeout, a socket error, a malformed or out-of-sequence response, or the emulator closing the connection closes the
/// connection and fails the access; the next access reconnects. An I/O error the emulator reports (an injected bus failure,
/// a powered-down HAT) fails the access and keeps the connection. Only connection changes are logged, never each access.
/// </para>
/// </remarks>
public sealed class SocketI2cRegisterClient : II2cRegisterClient
{
    private readonly object _gate = new();
    private readonly HatEmulatorOptions _options;
    private readonly ILogger _logger;
    private Socket? _socket;
    private uint _sequence;
    private bool _everConnected;
    private string? _lastFailure;
    private long _connects;
    private bool _disposed;

    /// <param name="options">The emulator endpoint and timeouts.</param>
    /// <param name="busId">The I2C bus the HAT is on; the emulator must report the same.</param>
    /// <param name="address">The HAT's I2C address; the emulator must report the same.</param>
    /// <param name="logger">Receives the connection changes.</param>
    public SocketI2cRegisterClient(HatEmulatorOptions options, int busId, int address, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(options));
        }

        _options = options;
        _logger = logger;
        ConnectionSettings = new I2cConnectionSettings(busId, address);
    }

    public I2cConnectionSettings ConnectionSettings { get; }

    public object SyncRoot { get; } = new();

    /// <summary>The emulator endpoint, <c>host:port</c>.</summary>
    public string Endpoint => _options.Endpoint;

    /// <summary>True while a connection is open.</summary>
    public bool IsConnected
    {
        get { lock (_gate) { return _socket is not null; } }
    }

    /// <summary>The connections opened so far (each passed the Hello check).</summary>
    public long ConnectCount => Interlocked.Read(ref _connects);

    public byte ReadByte(byte register)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadBlock(register, buffer);
        return buffer[0];
    }

    public ushort ReadUInt16(byte register)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadBlock(register, buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    public uint ReadUInt32(byte register)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadBlock(register, buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    public void ReadBlock(byte register, Span<byte> destination)
    {
        CheckRange(register, destination.Length);
        var response = Exchange(HatEmulatorFrameKind.Read, register, destination.Length, ReadOnlyMemory<byte>.Empty);
        response.Payload.Span.CopyTo(destination);
    }

    public void WriteByte(byte register, byte value)
    {
        ReadOnlySpan<byte> buffer = [value];
        WriteBlock(register, buffer);
    }

    public void WriteUInt16(byte register, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        WriteBlock(register, buffer);
    }

    public void WriteBlock(byte register, ReadOnlySpan<byte> data)
    {
        CheckRange(register, data.Length);
        Exchange(HatEmulatorFrameKind.Write, register, data.Length, data.ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _socket?.Dispose();
            _socket = null;
        }
    }

    /// <summary>The same check as the in-process client and the library: an access never runs past register 0xFF.</summary>
    private void CheckRange(byte register, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (register + length > HatEmulatorProtocol.MaxBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The access runs past register 0xFF.");
        }
    }

    private HatEmulatorFrame Exchange(HatEmulatorFrameKind kind, byte register, int count, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var socket = EnsureConnected();
            var sequence = unchecked(++_sequence);
            var request = new HatEmulatorFrame(kind, sequence, register, (ushort)count, payload);
            HatEmulatorFrame response;
            try
            {
                response = Send(socket, request, Deadline(_options.RequestTimeout));
                if (response.Kind == HatEmulatorFrameKind.Ok && kind == HatEmulatorFrameKind.Read && response.Payload.Length != count)
                {
                    throw new HatEmulatorProtocolException($"A Read of {count} registers returned {response.Payload.Length} bytes.");
                }

                if (response.Kind == HatEmulatorFrameKind.ProtocolError)
                {
                    throw new HatEmulatorProtocolException($"The HAT emulator refused the request: {response.PayloadText}");
                }
            }
            catch (IOException ex)
            {
                Drop(ex.Message);
                throw;
            }

            if (response.Kind == HatEmulatorFrameKind.IoError)
            {
                throw new IOException(response.PayloadText);
            }

            return response;
        }
    }

    /// <summary>Sends the request and returns the response with its sequence number. Every failure is an <see cref="IOException"/>.</summary>
    private static HatEmulatorFrame Send(Socket socket, HatEmulatorFrame request, long deadline)
    {
        try
        {
            var bytes = HatEmulatorProtocol.Encode(request);
            var sent = 0;
            while (sent < bytes.Length)
            {
                socket.SendTimeout = RemainingMilliseconds(deadline);
                sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
            }

            Span<byte> prefix = stackalloc byte[HatEmulatorProtocol.LengthPrefixLength];
            ReceiveExactly(socket, prefix, deadline);
            var body = new byte[HatEmulatorProtocol.ReadBodyLength(prefix)];
            ReceiveExactly(socket, body, deadline);
            var response = HatEmulatorProtocol.DecodeBody(body);
            if (response.Sequence != request.Sequence)
            {
                throw new HatEmulatorProtocolException($"Response {response.Sequence} does not answer request {request.Sequence}.");
            }

            if (response.Kind < HatEmulatorFrameKind.Ok)
            {
                throw new HatEmulatorProtocolException($"Expected a response, received {response.Kind}.");
            }

            return response;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
        {
            throw TimedOut(ex);
        }
        catch (SocketException ex)
        {
            throw new IOException($"HAT emulator connection failed: {ex.SocketErrorCode}.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("HAT emulator connection closed.", ex);
        }
    }

    private static void ReceiveExactly(Socket socket, Span<byte> buffer, long deadline)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            socket.ReceiveTimeout = RemainingMilliseconds(deadline);
            var read = socket.Receive(buffer[received..], SocketFlags.None);
            if (read == 0)
            {
                throw new IOException("The HAT emulator closed the connection.");
            }

            received += read;
        }
    }

    private Socket EnsureConnected()
    {
        if (_socket is { } open)
        {
            return open;
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using (var timeout = new CancellationTokenSource(_options.ConnectTimeout))
            {
                try
                {
                    socket.ConnectAsync(_options.Host, _options.Port, timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException ex)
                {
                    throw new IOException($"Connecting to the HAT emulator at {Endpoint} timed out after {_options.ConnectTimeout.TotalMilliseconds:0} ms.", ex);
                }
                catch (SocketException ex)
                {
                    throw new IOException($"Connecting to the HAT emulator at {Endpoint} failed: {ex.SocketErrorCode}.", ex);
                }
            }

            var reply = Send(socket, HatEmulatorProtocol.CreateHello(unchecked(++_sequence)), Deadline(_options.RequestTimeout));
            var (busId, address) = HatEmulatorProtocol.ReadHelloReply(reply);
            if (busId != ConnectionSettings.BusId || address != ConnectionSettings.DeviceAddress)
            {
                throw new HatEmulatorProtocolException(
                    $"The HAT emulator at {Endpoint} emulates bus {busId} address 0x{address:X2}; the controller expects bus {ConnectionSettings.BusId} address 0x{ConnectionSettings.DeviceAddress:X2}.");
            }
        }
        catch (IOException ex)
        {
            socket.Dispose();
            if (!string.Equals(_lastFailure, ex.Message, StringComparison.Ordinal))
            {
                // Once per distinct failure: the controller retries with every poll while the emulator is away.
                _lastFailure = ex.Message;
                _logger.LogWarning("HAT emulator unavailable: {Reason}", ex.Message);
            }

            throw;
        }

        _socket = socket;
        _lastFailure = null;
        Interlocked.Increment(ref _connects);
        _logger.LogInformation(
            _everConnected ? "Reconnected to the HAT emulator at {Endpoint}" : "Connected to the HAT emulator at {Endpoint}",
            Endpoint);
        _everConnected = true;
        return socket;
    }

    private void Drop(string reason)
    {
        if (_socket is null)
        {
            return;
        }

        _socket.Dispose();
        _socket = null;
        _lastFailure = reason;
        _logger.LogWarning("HAT emulator connection to {Endpoint} dropped: {Reason}", Endpoint, reason);
    }

    private static long Deadline(TimeSpan timeout) => Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

    private static int RemainingMilliseconds(long deadline)
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        if (remaining <= TimeSpan.Zero)
        {
            throw TimedOut(null);
        }

        // Socket timeouts are whole milliseconds and 0 means infinite.
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
    }

    private static IOException TimedOut(Exception? inner) => new("The HAT emulator did not answer within the request timeout.", inner);
}
