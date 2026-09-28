using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Emulation;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// A stand-in HAT emulator on a loopback port that answers each connection with a test's script, and records every
/// frame it receives. For tests of the controller's socket register client against misbehaving emulators.
/// </summary>
internal sealed class ScriptedHatEmulator : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<ScriptedConnection, Task> _script;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<HatEmulatorFrame> _received = new();
    private readonly ConcurrentBag<Task> _handlers = new();
    private readonly Task _acceptLoop;
    private int _accepted;

    /// <param name="script">Runs once per connection, with the connection's index (0 for the first).</param>
    public ScriptedHatEmulator(Func<ScriptedConnection, Task> script)
    {
        _script = script;
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Every frame received, on every connection, in order.</summary>
    public IReadOnlyList<HatEmulatorFrame> Received => _received.ToArray();

    /// <summary>Answers the Hello and then every access with the register values from <paramref name="registers"/>.</summary>
    public static async Task ServeRegistersAsync(ScriptedConnection connection, byte[] registers, int busId = 1, int address = 0x0E)
    {
        if (!await connection.AnswerHelloAsync(busId, address))
        {
            return;
        }

        while (await connection.ReceiveAsync() is { } request)
        {
            if (request.Kind == HatEmulatorFrameKind.Read)
            {
                var values = registers.AsSpan(request.Register, request.Count).ToArray();
                await connection.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence, request.Register, request.Count, values));
            }
            else
            {
                request.Payload.Span.CopyTo(registers.AsSpan(request.Register));
                await connection.SendAsync(new HatEmulatorFrame(HatEmulatorFrameKind.Ok, request.Sequence, request.Register, request.Count, ReadOnlyMemory<byte>.Empty));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_handlers);
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var index = Interlocked.Increment(ref _accepted) - 1;
            _handlers.Add(RunAsync(new ScriptedConnection(index, client, _received, _stopping.Token)));
        }
    }

    private async Task RunAsync(ScriptedConnection connection)
    {
        await Task.Yield();
        try
        {
            await _script(connection);
        }
        catch (Exception ex) when (ex is System.IO.IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client closed the connection or the test is over.
        }
        finally
        {
            connection.Dispose();
        }
    }
}

/// <summary>One connection to <see cref="ScriptedHatEmulator"/>.</summary>
internal sealed class ScriptedConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly ConcurrentQueue<HatEmulatorFrame> _received;

    public ScriptedConnection(int index, TcpClient client, ConcurrentQueue<HatEmulatorFrame> received, CancellationToken stopping)
    {
        Index = index;
        _client = client;
        _received = received;
        Stopping = stopping;
        Stream = client.GetStream();
    }

    /// <summary>0 for the first connection.</summary>
    public int Index { get; }

    public NetworkStream Stream { get; }

    public CancellationToken Stopping { get; }

    /// <summary>The next frame, or null when the client closed the connection.</summary>
    public async Task<HatEmulatorFrame?> ReceiveAsync()
    {
        var frame = await HatEmulatorProtocol.ReadFrameAsync(Stream, Stopping);
        if (frame is { } received)
        {
            _received.Enqueue(received);
        }

        return frame;
    }

    public async Task SendAsync(HatEmulatorFrame frame) => await HatEmulatorProtocol.WriteFrameAsync(Stream, frame, Stopping);

    /// <summary>Writes raw bytes, for malformed responses.</summary>
    public async Task SendRawAsync(byte[] bytes)
    {
        await Stream.WriteAsync(bytes, Stopping);
        await Stream.FlushAsync(Stopping);
    }

    /// <summary>Reads the Hello and answers it with the HAT's bus and address; false when the client closed first.</summary>
    public async Task<bool> AnswerHelloAsync(int busId = 1, int address = 0x0E)
    {
        if (await ReceiveAsync() is not { } hello)
        {
            return false;
        }

        await SendAsync(HatEmulatorProtocol.CreateHelloReply(hello.Sequence, busId, address));
        return true;
    }

    /// <summary>Waits until the client closes the connection or the test ends, reading and recording what it sends.</summary>
    public async Task HoldAsync()
    {
        while (await ReceiveAsync() is not null)
        {
        }
    }

    public void Dispose() => _client.Dispose();
}
