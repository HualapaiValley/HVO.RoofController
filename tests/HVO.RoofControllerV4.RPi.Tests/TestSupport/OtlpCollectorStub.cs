using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>What the stand-in collector does with the controller's OTLP exports.</summary>
internal enum CollectorBehavior
{
    /// <summary>Accepts every export (HTTP 200), as a healthy collector does.</summary>
    Reachable,

    /// <summary>Accepts the connection and never answers, as an address that times out does.</summary>
    BlackHole,

    /// <summary>Nothing listens on the port, so every connection is refused at once.</summary>
    Refused
}

/// <summary>
/// A stand-in OpenTelemetry collector on loopback for <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> with the
/// <c>http/protobuf</c> protocol the compose profiles and the deploy script use.
/// </summary>
internal sealed class OtlpCollectorStub : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TcpClient> _held = [];
    private readonly HttpListener? _http;
    private readonly TcpListener? _tcp;
    private readonly Task _loop = Task.CompletedTask;
    private int _connections;
    private int _exports;

    private OtlpCollectorStub(CollectorBehavior behavior)
    {
        Behavior = behavior;
        var port = FreePort();
        Endpoint = $"http://127.0.0.1:{port}";
        switch (behavior)
        {
            case CollectorBehavior.Reachable:
                _http = new HttpListener();
                _http.Prefixes.Add($"{Endpoint}/");
                _http.Start();
                _loop = Task.Run(AnswerAsync);
                break;
            case CollectorBehavior.BlackHole:
                _tcp = new TcpListener(IPAddress.Loopback, port);
                _tcp.Start();
                _loop = Task.Run(HoldAsync);
                break;
        }
    }

    public CollectorBehavior Behavior { get; }

    /// <summary>The value for <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.</summary>
    public string Endpoint { get; }

    /// <summary>Connections the black hole accepted and never answered.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Connections the black hole holds now: the exporter has not yet given up on them.</summary>
    public int Held
    {
        get
        {
            lock (_held)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Exports the reachable collector accepted.</summary>
    public int Exports => Volatile.Read(ref _exports);

    public static OtlpCollectorStub Start(CollectorBehavior behavior) => new(behavior);

    /// <summary>The settings that point the controller at this collector, exporting metrics every second.</summary>
    public IEnumerable<KeyValuePair<string, string?>> Settings()
    {
        yield return new("OTEL_EXPORTER_OTLP_ENDPOINT", Endpoint);
        yield return new("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        yield return new("OTEL_METRIC_EXPORT_INTERVAL", "1000");
        yield return new("OTEL_BSP_SCHEDULE_DELAY", "200");
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _http?.Close();
        _tcp?.Stop();
        lock (_held)
        {
            foreach (var client in _held)
            {
                client.Dispose();
            }
        }

        try
        {
            await _loop;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SocketException or HttpListenerException or OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AnswerAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var context = await _http!.GetContextAsync();
            await context.Request.InputStream.CopyToAsync(System.IO.Stream.Null);
            Interlocked.Increment(ref _exports);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        }
    }

    private async Task HoldAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var client = await _tcp!.AcceptTcpClientAsync(_stopping.Token);
            Interlocked.Increment(ref _connections);
            lock (_held)
            {
                _held.Add(client);
            }

            _ = DrainAsync(client);
        }
    }

    /// <summary>
    /// Reads and discards the export without answering, and lets the connection go once the exporter gives up on it, as
    /// an address that times out keeps no connection open: a long outage (the soak) holds no more sockets over time.
    /// </summary>
    private async Task DrainAsync(TcpClient client)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            var stream = client.GetStream();
            while (await stream.ReadAsync(buffer, _stopping.Token) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException or ObjectDisposedException or SocketException or OperationCanceledException or InvalidOperationException)
        {
        }

        lock (_held)
        {
            _held.Remove(client);
        }

        client.Dispose();
    }
}

/// <summary>Counts the OTLP exporter's own failure events (it reports them through its EventSource, not the logs).</summary>
internal sealed class OtlpExporterFailures : EventListener
{
    private const string SourceName = "OpenTelemetry-Exporter-OpenTelemetryProtocol";
    private int _count;

    public int Count => Volatile.Read(ref _count);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == SourceName && eventData.Level <= EventLevel.Warning)
        {
            Interlocked.Increment(ref _count);
        }
    }
}
