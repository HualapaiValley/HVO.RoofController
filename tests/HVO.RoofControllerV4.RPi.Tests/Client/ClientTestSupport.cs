using System.Collections.Concurrent;
using System.Net.WebSockets;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.TestHost;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Connects the client library to an in-process controller: REST through the test server's handler and the status hub
/// through its WebSocket client, both looked up for each request so a client can follow a controller that restarts.
/// </summary>
internal static class ClientTestSupport
{
    public static readonly Uri BaseAddress = new("http://localhost/");

    /// <summary>Reconnects quickly and without jitter, so the tests do not wait on the production delays.</summary>
    public static readonly RoofStatusFeedOptions FastFeed = new()
    {
        InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        ReconnectJitter = 0,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };

    public static RoofControllerClient CreateClient(
        RoofApiTestHost host,
        RoofCredential? credential = null,
        RoofStatusFeedOptions? feed = null,
        TimeProvider? time = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? requestTimeout = null,
        Func<HttpMessageHandler>? handler = null)
        => CreateClient(() => host.Server, credential, feed, time, stopTimeout, requestTimeout, handler);

    public static RoofControllerClient CreateClient(
        Func<TestServer> server,
        RoofCredential? credential = null,
        RoofStatusFeedOptions? feed = null,
        TimeProvider? time = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? requestTimeout = null,
        Func<HttpMessageHandler>? handler = null)
        => new(new RoofConnectionOptions
        {
            BaseAddress = BaseAddress,
            Credential = credential,
            CreateHandler = handler ?? (() => new CurrentServerHandler(server)),
            WebSocketFactory = (uri, headers, cancellationToken) => ConnectAsync(server, uri, headers, cancellationToken),
            StatusFeed = feed ?? FastFeed,
            TimeProvider = time ?? TimeProvider.System,
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10),
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30)
        });

    /// <summary>Waits until <paramref name="condition"/> holds, or fails after <paramref name="timeout"/> (10 s).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Opens the status hub's WebSocket on the test server running now.</summary>
    public static RoofWebSocketFactory WebSocketFactory(Func<TestServer> server)
        => (uri, headers, cancellationToken) => ConnectAsync(server, uri, headers, cancellationToken);

    /// <summary>Sends each request to the test server running now.</summary>
    public static HttpMessageHandler CreateHandler(Func<TestServer> server) => new CurrentServerHandler(server);

    private static async ValueTask<WebSocket> ConnectAsync(
        Func<TestServer> server,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var client = CurrentServerHandler.Resolve(server).CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            foreach (var header in headers)
            {
                request.Headers[header.Key] = header.Value;
            }
        };
        return await client.ConnectAsync(uri, cancellationToken);
    }

    /// <summary>Sends each request to the test server running now. A stopped server answers as a refused connection does.</summary>
    private sealed class CurrentServerHandler(Func<TestServer> server) : HttpMessageHandler
    {
        public static TestServer Resolve(Func<TestServer> server)
        {
            try
            {
                return server();
            }
            catch (ObjectDisposedException ex)
            {
                throw new HttpRequestException("The controller is not running (test).", ex);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var invoker = new HttpMessageInvoker(Resolve(server).CreateHandler(), disposeHandler: false);
            try
            {
                return await invoker.SendAsync(request, cancellationToken);
            }
            catch (ObjectDisposedException ex)
            {
                throw new HttpRequestException("The controller is not running (test).", ex);
            }
        }
    }
}

/// <summary>A handler that fails or stalls every request, standing in for a controller that cannot be reached.</summary>
internal sealed class FailingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public static FailingHandler Refused() => new((_, _) => throw new HttpRequestException("Connection refused (test)."));

    /// <summary>Never answers; the request ends only when it is cancelled.</summary>
    public static FailingHandler Silent() => new(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => respond(request, cancellationToken);
}

/// <summary>A request as it was sent: its method, path and headers.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Sends each request to the test server and records it in <paramref name="log"/>. <paramref name="answered"/>, when
/// given, runs after each answer and before the client reads it.
/// </summary>
internal sealed class RecordingHandler(TestServer server, ConcurrentQueue<RecordedRequest> log, Action<RecordedRequest>? answered = null)
    : DelegatingHandler(server.CreateHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
        log.Enqueue(recorded);
        var response = await base.SendAsync(request, cancellationToken);
        answered?.Invoke(recorded);
        return response;
    }
}
