using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Hubs;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Hubs;

/// <summary>
/// The status hub over SignalR (#40): who may connect, what arrives on the wire, and that the hub accepts no commands.
/// </summary>
[TestClass]
public sealed class RoofStatusHubTests
{
    private static readonly IPAddress LanClient = IPAddress.Parse("192.168.1.50");

    [TestMethod]
    [DataRow(HttpTransportType.WebSockets)]
    [DataRow(HttpTransportType.LongPolling)]
    public async Task AConnection_IsSentTheCurrentSnapshotFirst(HttpTransportType transport)
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer, transport);

        var first = await hub.NextAsync();

        first.Sequence.Should().BeGreaterThan(0);
        first.InstanceId.Should().Be(host.Services.GetRequiredService<RoofStatusBroadcaster>().InstanceId);
        first.Status.Status.Should().Be(RoofControllerStatus.Closed);
        first.Status.StatusVersion.Should().Be(11);
        first.ServerTimeUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task OnTheWire_NamesAreCamelCase_AndEnumsAreStrings()
    {
        using var host = new RoofApiTestHost();
        var raw = new BlockingCollection<JsonElement>();
        await using var connection = StatusHub.Build(host, TestApiKeys.Viewer);
        connection.On<JsonElement>(RoofStatusHubContract.StatusMethod, raw.Add);
        await connection.StartAsync();

        raw.TryTake(out var message, TimeSpan.FromSeconds(10)).Should().BeTrue("the first message should arrive");

        message.GetProperty("sequence").GetInt64().Should().BeGreaterThan(0);
        message.GetProperty("instanceId").GetString().Should().NotBeNullOrWhiteSpace();
        message.TryGetProperty("serverTimeUtc", out _).Should().BeTrue();
        var status = message.GetProperty("status");
        status.GetProperty("status").GetString().Should().Be("Closed");
        status.GetProperty("lastStopReason").GetString().Should().Be("NormalStop");
        status.GetProperty("statusVersion").GetInt64().Should().Be(11);
    }

    [TestMethod]
    public async Task StatusChanges_ArriveInOrder()
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);
        await hub.NextAsync();

        foreach (var (version, status) in new[] { (12L, RoofControllerStatus.Opening), (13L, RoofControllerStatus.Open), (14L, RoofControllerStatus.Stopped) })
        {
            host.RoofService.Raise(s => s.StatusChanged += null, new RoofStatusChangedEventArgs(RoofServiceMock.Snapshot(status) with { StatusVersion = version }));
        }

        var received = await hub.UntilAsync(m => m.Status.StatusVersion == 14);
        received.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        received.Select(m => m.Status.StatusVersion).Should().BeInAscendingOrder();
        received[^1].Status.Status.Should().Be(RoofControllerStatus.Stopped);
    }

    [TestMethod]
    public async Task AClientThatNeverReads_DelaysNeitherStatusChanges_NorOtherClients()
    {
        var sends = new SendTracker();
        using var host = new RoofApiTestHost(configureServices: services =>
            services.AddSingleton<IRoofStatusSender>(provider => sends.Wrap(ActivatorUtilities.CreateInstance<HubRoofStatusSender>(provider))));
        await using var live = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);
        await live.NextAsync();

        // A long-polling client that completes the handshake and then never polls: what the server sends it stays in
        // the connection's transport buffer until that is full, and then the send to it waits.
        using var stalled = host.CreateApiClient(TestApiKeys.Viewer);
        var negotiated = await ApiJson.ReadElementAsync(await stalled.PostAsync($"{RoofStatusHubContract.Path}/negotiate?negotiateVersion=1", content: null));
        var stalledId = negotiated.GetProperty("connectionId").GetString()!;
        var poll = $"{RoofStatusHubContract.Path}?id={Uri.EscapeDataString(negotiated.GetProperty("connectionToken").GetString()!)}";
        (await stalled.GetAsync(poll)).StatusCode.Should().Be(HttpStatusCode.OK, "the first poll opens the connection");
        (await stalled.PostAsync(poll, new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var version = 11L;
        var clock = Stopwatch.StartNew();
        while (sends.WaitingFor(stalledId) < TimeSpan.FromMilliseconds(500))
        {
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the stalled client's buffer should fill and hold up its sends");
            host.RoofService.Raise(s => s.StatusChanged += null, new RoofStatusChangedEventArgs(RoofServiceMock.Snapshot() with { StatusVersion = ++version }));
            await Task.Delay(2);
        }

        var raising = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                host.RoofService.Raise(s => s.StatusChanged += null, new RoofStatusChangedEventArgs(RoofServiceMock.Snapshot() with { StatusVersion = ++version }));
            }
        });
        (await Task.WhenAny(raising, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(raising, "the controller's status dispatcher never waits for a client");

        var last = version;
        await live.UntilAsync(m => m.Status.StatusVersion == last);
        sends.WaitingFor(stalledId).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500), "the stalled client is still not reading");
        host.Services.GetRequiredService<RoofStatusBroadcaster>().ConnectionCount.Should().Be(2);
    }

    [TestMethod]
    public async Task WhileNothingChanges_AHeartbeatArrivesAboutOnceASecond()
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);

        var received = await hub.UntilAsync(m => false, count: 4, timeout: TimeSpan.FromSeconds(15));

        received.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        var gaps = received.Zip(received.Skip(1), (a, b) => b.ServerTimeUtc - a.ServerTimeUtc).ToList();
        gaps.Should().OnlyContain(gap => gap >= TimeSpan.FromMilliseconds(900), "a heartbeat is sent only after a quiet interval");
        gaps.Should().OnlyContain(
            gap => gap <= TimeSpan.FromSeconds(2),
            "a heartbeat follows about one interval later, well inside the 3 s after which a client treats its view as stale");
    }

    [TestMethod]
    public async Task ANewProcess_HasANewInstanceId_AndStartsTheSequenceAgain()
    {
        string firstInstance;
        using (var host = new RoofApiTestHost())
        {
            await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);
            var received = await hub.UntilAsync(m => false, count: 2);
            firstInstance = received[0].InstanceId;
        }

        using var restarted = new RoofApiTestHost();
        await using var again = await StatusHub.ConnectAsync(restarted, TestApiKeys.Viewer);
        var first = await again.NextAsync();

        first.InstanceId.Should().NotBe(firstInstance);
        first.Sequence.Should().Be(1);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("test-unknown-key-not-a-real-secret-00")]
    public async Task WithoutAValidKey_TheConnectionIsRefused(string? key)
    {
        using var host = new RoofApiTestHost();
        await using var connection = StatusHub.Build(host, key);

        var refused = await FluentActions.Awaiting(() => connection.StartAsync()).Should().ThrowAsync<HttpRequestException>();

        refused.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Services.GetRequiredService<RoofStatusBroadcaster>().ConnectionCount.Should().Be(0);
    }

    [TestMethod]
    [DataRow("access_token")]
    [DataRow("X-Api-Key")]
    [DataRow("api_key")]
    public async Task AKeyInTheQueryString_IsNotAccepted(string parameter)
    {
        using var host = new RoofApiTestHost();
        using var client = host.CreateApiClient();

        var response = await client.PostAsync(
            $"{RoofStatusHubContract.Path}/negotiate?negotiateVersion=1&{parameter}={Uri.EscapeDataString(TestApiKeys.Viewer)}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a key in a URL ends up in logs and browser history");
    }

    [TestMethod]
    public async Task AClientMessageOverTheLimit_ClosesTheConnection()
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Admin);
        await hub.NextAsync();

        await FluentActions.Awaiting(() => hub.Connection.InvokeAsync("Stop", new string('x', 8 * 1024))).Should().ThrowAsync<Exception>();

        await hub.ClosedAsync();
        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never);
        host.Services.GetRequiredService<RoofStatusBroadcaster>().ConnectionCount.Should().Be(0);
    }

    [TestMethod]
    public async Task TheConsoleCookie_DoesNotOpenAConnection()
    {
        using var host = new RoofApiTestHost();
        var cookie = await ConsoleSignInAsync(host, TestApiKeys.Admin);
        using var client = host.CreateApiClient();

        foreach (var (method, path) in new[] { (HttpMethod.Post, RoofStatusHubContract.Path + "/negotiate?negotiateVersion=1"), (HttpMethod.Get, RoofStatusHubContract.Path) })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", cookie);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} must not accept the console cookie");
        }
    }

    [TestMethod]
    [DataRow(TestApiKeys.Viewer)]
    [DataRow(TestApiKeys.HashedViewer)]
    [DataRow(TestApiKeys.Operator)]
    [DataRow(TestApiKeys.Admin)]
    public async Task EveryRole_MayConnect(string key)
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, key);

        (await hub.NextAsync()).Status.Should().NotBeNull();
    }

    [TestMethod]
    [DataRow("Stop")]
    [DataRow("Open")]
    [DataRow("Close")]
    public async Task TheHub_AcceptsNoCommands(string method)
    {
        using var host = new RoofApiTestHost();
        await using var hub = await StatusHub.ConnectAsync(host, TestApiKeys.Admin);
        await hub.NextAsync();

        await FluentActions.Awaiting(() => hub.Connection.InvokeAsync(method)).Should().ThrowAsync<HubException>();

        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never);
        host.RoofService.Verify(s => s.Open(), Times.Never);
        host.RoofService.Verify(s => s.Close(), Times.Never);
        hub.Connection.State.Should().Be(HubConnectionState.Connected, "an unknown method is an error for that call only");
    }

    [TestMethod]
    public async Task AConnectionWhoseKeyIsRemoved_IsClosed()
    {
        var keys = KeyStoreFactory.Monitor(
            KeyStoreFactory.Key("viewer", RoofControllerApiContract.ViewerRole, TestApiKeys.Viewer),
            KeyStoreFactory.Key("operator", RoofControllerApiContract.OperatorRole, TestApiKeys.Operator));
        using var host = new RoofApiTestHost(configureServices: services =>
            services.AddSingleton(_ => new RoofApiKeyStore(keys, NullLogger<RoofApiKeyStore>.Instance)));
        await using var viewer = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);
        await using var operatorHub = await StatusHub.ConnectAsync(host, TestApiKeys.Operator);
        await viewer.NextAsync();

        keys.Set(new RoofControllerSecurityOptions
        {
            ApiKeys = [KeyStoreFactory.Key("operator", RoofControllerApiContract.OperatorRole, TestApiKeys.Operator)]
        });

        await viewer.ClosedAsync();
        operatorHub.Connection.State.Should().Be(HubConnectionState.Connected);
        var afterwards = await operatorHub.UntilAsync(m => false, count: 3, timeout: TimeSpan.FromSeconds(10));
        afterwards.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task PastTheConnectionLimit_ANewConnectionIsClosed_WithTheReason()
    {
        using var host = new RoofApiTestHost();
        var broadcaster = host.Services.GetRequiredService<RoofStatusBroadcaster>();
        for (var i = 0; i < RoofStatusBroadcaster.DefaultMaxConnections; i++)
        {
            broadcaster.TryRegister($"filler-{i}", null, () => { }).Should().BeTrue();
        }

        await using (var refused = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer))
        {
            var closed = await refused.ClosedAsync();
            closed.Should().NotBeNull();
            closed!.Message.Should().Contain("not accepting more status connections");
            refused.Received.Should().BeEmpty();
        }

        broadcaster.Unregister("filler-0");
        await using var accepted = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer);
        (await accepted.NextAsync()).Status.Should().NotBeNull();
    }

    [TestMethod]
    public async Task PastTheLimitForOneKey_ANewConnectionWithThatKeyIsClosed_WhileOtherKeysConnect()
    {
        using var host = new RoofApiTestHost();
        var broadcaster = host.Services.GetRequiredService<RoofStatusBroadcaster>();
        host.Services.GetRequiredService<RoofApiKeyStore>().TryValidate(TestApiKeys.Viewer, out var viewerKey).Should().BeTrue();
        var viewer = RoofPrincipalFactory.Create(viewerKey!, RoofControllerSecurityDefaults.ApiKeyScheme);
        for (var i = 0; i < RoofStatusHubContract.MaxConnectionsPerKey; i++)
        {
            broadcaster.TryRegister($"leaked-{i}", viewer, () => { }).Should().BeTrue();
        }

        await using (var refused = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer))
        {
            var closed = await refused.ClosedAsync();
            closed!.Message.Should().Contain("not accepting more status connections for this key");
            refused.Received.Should().BeEmpty();
        }

        await using var otherKey = await StatusHub.ConnectAsync(host, TestApiKeys.Operator);
        (await otherKey.NextAsync()).Status.Should().NotBeNull("one key's leaked connections do not lock out the others");
    }

    [TestMethod]
    public async Task InProduction_PlainHttpFromTheLan_IsRefused()
    {
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: LanClient);

        await using (var plain = StatusHub.Build(host, TestApiKeys.Viewer))
        {
            var refused = await FluentActions.Awaiting(() => plain.StartAsync()).Should().ThrowAsync<HttpRequestException>();
            refused.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await using var secure = await StatusHub.ConnectAsync(host, TestApiKeys.Viewer, HttpTransportType.LongPolling, https: true);
        (await secure.NextAsync()).Status.Should().NotBeNull();
    }

    private static async Task<string> ConsoleSignInAsync(RoofApiTestHost host, string accessKey)
    {
        var antiforgery = host.Services.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(new DefaultHttpContext { RequestServices = host.Services });
        var cookieName = host.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;
        using var request = new HttpRequestMessage(HttpMethod.Post, RoofControllerSecurityDefaults.LoginPostPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [RoofControllerSecurityDefaults.AccessKeyFormField] = accessKey,
                [RoofControllerSecurityDefaults.ReturnUrlFormField] = "/",
                [tokens.FormFieldName] = tokens.RequestToken!
            })
        };
        request.Headers.Add("Cookie", $"{cookieName}={tokens.CookieToken}");
        using var client = host.CreateApiClient();
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        return values!.Single(v => v.StartsWith(RoofSecurityServiceCollectionExtensions.ConsoleCookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }
}

/// <summary>A status hub client on a <see cref="RoofApiTestHost"/>'s in-memory server, recording what it receives.</summary>
internal sealed class StatusHub : IAsyncDisposable
{
    private readonly BlockingCollection<RoofStatusHubMessage> _pending = new();
    private readonly List<RoofStatusHubMessage> _received = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StatusHub(HubConnection connection)
    {
        Connection = connection;
        connection.On<RoofStatusHubMessage>(RoofStatusHubContract.StatusMethod, message =>
        {
            lock (_received)
            {
                _received.Add(message);
            }

            _pending.Add(message);
        });
        connection.Closed += error =>
        {
            _closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public IReadOnlyList<RoofStatusHubMessage> Received
    {
        get
        {
            lock (_received)
            {
                return _received.ToArray();
            }
        }
    }

    public static HubConnection Build(
        RoofApiTestHost host,
        string? apiKey,
        HttpTransportType transport = HttpTransportType.WebSockets,
        bool https = false)
    {
        var server = host.Server;
        var baseAddress = new Uri(https ? "https://localhost/" : "http://localhost/");
        return new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, RoofStatusHubContract.Path.TrimStart('/')), options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        if (apiKey is not null)
                        {
                            request.Headers[RoofControllerApiContract.ApiKeyHeaderName] = apiKey;
                        }
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
                if (apiKey is not null)
                {
                    options.Headers[RoofControllerApiContract.ApiKeyHeaderName] = apiKey;
                }
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
    }

    public static Task<StatusHub> ConnectAsync(
        RoofApiTestHost host,
        string apiKey,
        HttpTransportType transport = HttpTransportType.WebSockets,
        bool https = false)
        => StartAsync(Build(host, apiKey, transport, https));

    /// <summary>Connects over the network, as a client on another machine does.</summary>
    public static Task<StatusHub> ConnectAsync(Uri baseAddress, string apiKey)
        => StartAsync(new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, RoofStatusHubContract.Path.TrimStart('/')), options => options.Headers[RoofControllerApiContract.ApiKeyHeaderName] = apiKey)
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build());

    private static async Task<StatusHub> StartAsync(HubConnection connection)
    {
        var hub = new StatusHub(connection);
        try
        {
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return hub;
        }
        catch
        {
            await hub.DisposeAsync();
            throw;
        }
    }

    public Task<RoofStatusHubMessage> NextAsync(TimeSpan? timeout = null)
        => Task.Run(() => _pending.TryTake(out var message, timeout ?? TimeSpan.FromSeconds(10))
            ? message
            : throw new AssertFailedException($"No status message arrived. Received: {string.Join(", ", Received.Select(RecordingStatusSender.Describe))}."));

    /// <summary>Takes messages until one satisfies <paramref name="done"/> or <paramref name="count"/> have been taken.</summary>
    public async Task<IReadOnlyList<RoofStatusHubMessage>> UntilAsync(
        Func<RoofStatusHubMessage, bool> done,
        int count = int.MaxValue,
        TimeSpan? timeout = null)
    {
        var taken = new List<RoofStatusHubMessage>();
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        while (taken.Count < count)
        {
            var remaining = limit - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new AssertFailedException($"Timed out after {taken.Count} message(s): {string.Join(", ", taken.Select(RecordingStatusSender.Describe))}.");
            }

            var message = await NextAsync(remaining);
            taken.Add(message);
            if (done(message))
            {
                break;
            }
        }

        return taken;
    }

    /// <summary>Waits for the server to close the connection; returns the error it gave, if any.</summary>
    public Task<Exception?> ClosedAsync(TimeSpan? timeout = null) => _closed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        _pending.Dispose();
    }
}

/// <summary>Wraps the hub's sender and records how long the send to each connection has been waiting.</summary>
internal sealed class SendTracker
{
    private readonly ConcurrentDictionary<string, long> _waitingSince = new(StringComparer.Ordinal);

    public IRoofStatusSender Wrap(IRoofStatusSender inner) => new Tracking(inner, this);

    /// <summary>How long the send in progress to <paramref name="connectionId"/> has waited; zero when none is.</summary>
    public TimeSpan WaitingFor(string connectionId)
        => _waitingSince.TryGetValue(connectionId, out var since) ? Stopwatch.GetElapsedTime(since) : TimeSpan.Zero;

    private sealed class Tracking(IRoofStatusSender inner, SendTracker tracker) : IRoofStatusSender
    {
        public async Task SendAsync(string connectionId, RoofStatusHubMessage message, CancellationToken cancellationToken)
        {
            tracker._waitingSince[connectionId] = Stopwatch.GetTimestamp();
            try
            {
                await inner.SendAsync(connectionId, message, cancellationToken);
            }
            finally
            {
                tracker._waitingSince.TryRemove(connectionId, out _);
            }
        }
    }
}
