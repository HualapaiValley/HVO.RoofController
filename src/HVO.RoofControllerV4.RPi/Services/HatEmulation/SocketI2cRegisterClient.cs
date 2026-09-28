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
/// One access at a time. The connection opens on the first access: the connect (within
/// <see cref="HatEmulatorOptions.ConnectTimeout"/>), then the Hello check of the protocol version and the HAT address. Each
/// access completes within <see cref="HatEmulatorOptions.RequestTimeout"/>; an access that opens the connection shares that
/// timeout with the Hello, so it completes within <c>ConnectTimeout + RequestTimeout</c>. The timeouts are the blocked
/// caller's own waits, so a busy thread pool cannot stretch them.
/// </para>
/// <para>
/// A timeout, a socket error, a malformed or out-of-sequence response, or the emulator closing the connection closes the
/// connection and fails the access; the next access reconnects, with no backoff (while the emulator is away each access
/// makes one connection attempt). An I/O error the emulator reports (an injected bus failure, a powered-down HAT) fails the
/// access and keeps the connection. Only connection changes are logged, never each access.
/// </para>
/// <para>
/// <see cref="Dispose"/> does not wait for an access in flight: it closes the connection, which fails that access at once.
/// </para>
/// </remarks>
public sealed class SocketI2cRegisterClient : II2cRegisterClient
{
    private readonly object _gate = new();
    private readonly HatEmulatorOptions _options;
    private readonly ILogger _logger;
    private Socket? _socket;
    private Socket? _connecting;
    private uint _sequence;
    private bool _everConnected;
    private string? _lastFailure;
    private long _connects;
    private int _disposed;

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
    public bool IsConnected => Volatile.Read(ref _socket) is not null;

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
        // Without the gate, so an access in flight fails now rather than holding up the shutdown until it times out.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _connecting, null)?.Dispose();
        Interlocked.Exchange(ref _socket, null)?.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The same check as the in-process emulated client: an access never runs past register 0xFF.</summary>
    private void CheckRange(byte register, int length)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (register + length > HatEmulatorProtocol.MaxBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The access runs past register 0xFF.");
        }
    }

    private HatEmulatorFrame Exchange(HatEmulatorFrameKind kind, byte register, int count, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var (socket, deadline) = EnsureConnected();
            var sequence = unchecked(++_sequence);
            var request = new HatEmulatorFrame(kind, sequence, register, (ushort)count, payload);
            HatEmulatorFrame response;
            try
            {
                response = Send(socket, request, deadline);
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
            throw Closed(ex);
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

    /// <summary>The open connection, or a new one that passed the Hello check, and the deadline of the access.</summary>
    private (Socket Socket, long Deadline) EnsureConnected()
    {
        if (Volatile.Read(ref _socket) is { } open)
        {
            return (open, Deadline(_options.RequestTimeout));
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        long deadline;
        try
        {
            // Published so that Dispose can abort the attempt; checked after, in case Dispose ran first.
            Interlocked.Exchange(ref _connecting, socket);
            if (IsDisposed)
            {
                throw Closed(null);
            }

            Connect(socket);

            // The Hello and the access share one request timeout.
            deadline = Deadline(_options.RequestTimeout);
            var reply = Send(socket, HatEmulatorProtocol.CreateHello(unchecked(++_sequence)), deadline);
            var (busId, address) = HatEmulatorProtocol.ReadHelloReply(reply);
            if (busId != ConnectionSettings.BusId || address != ConnectionSettings.DeviceAddress)
            {
                throw new HatEmulatorProtocolException(
                    $"The HAT emulator at {Endpoint} emulates bus {busId} address 0x{address:X2}; the controller expects bus {ConnectionSettings.BusId} address 0x{ConnectionSettings.DeviceAddress:X2}.");
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _connecting, null, socket);
            socket.Dispose();

            // Every failure reaches the HAT library as a failed transfer, even one this client does not expect.
            var failure = ex as IOException
                ?? (IsDisposed ? Closed(ex) : new IOException($"Connecting to the HAT emulator at {Endpoint} failed: {ex.Message}", ex));
            if (!IsDisposed && !string.Equals(_lastFailure, failure.Message, StringComparison.Ordinal))
            {
                // Once per distinct failure: the controller retries with every poll while the emulator is away.
                _lastFailure = failure.Message;
                _logger.LogWarning("HAT emulator unavailable: {Reason}", failure.Message);
            }

            if (ReferenceEquals(failure, ex))
            {
                throw;
            }

            throw failure;
        }

        Interlocked.CompareExchange(ref _connecting, null, socket);
        Interlocked.Exchange(ref _socket, socket);
        if (IsDisposed)
        {
            // Dispose ran during the Hello and may have missed this socket.
            Interlocked.Exchange(ref _socket, null)?.Dispose();
            throw Closed(null);
        }

        _lastFailure = null;
        Interlocked.Increment(ref _connects);
        _logger.LogInformation(
            _everConnected ? "Reconnected to the HAT emulator at {Endpoint}" : "Connected to the HAT emulator at {Endpoint}",
            Endpoint);
        _everConnected = true;
        return (socket, deadline);
    }

    /// <summary>
    /// Connects within <see cref="HatEmulatorOptions.ConnectTimeout"/>. The caller's own wait bounds the attempt, not a
    /// timer on the thread pool; a timed-out attempt is abandoned by closing its socket.
    /// </summary>
    private void Connect(Socket socket)
    {
        var connect = socket.ConnectAsync(_options.Host, _options.Port);
        bool completed;
        try
        {
            completed = connect.Wait(_options.ConnectTimeout);
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException socketError)
        {
            throw new IOException($"Connecting to the HAT emulator at {Endpoint} failed: {socketError.SocketErrorCode}.", socketError);
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner)
        {
            throw new IOException($"Connecting to the HAT emulator at {Endpoint} failed: {inner.Message}", inner);
        }

        if (!completed)
        {
            socket.Dispose();
            _ = connect.ContinueWith(static attempt => _ = attempt.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new IOException($"Connecting to the HAT emulator at {Endpoint} timed out after {_options.ConnectTimeout.TotalMilliseconds:0} ms.");
        }
    }

    private void Drop(string reason)
    {
        if (Interlocked.Exchange(ref _socket, null) is not { } socket)
        {
            return;
        }

        socket.Dispose();
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

    private static IOException Closed(Exception? inner) => new("HAT emulator connection closed.", inner);
}
