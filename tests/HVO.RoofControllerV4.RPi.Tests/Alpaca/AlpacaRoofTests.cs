using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ClientTestSupport = HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The roof as the Alpaca server holds it (#220, review round 1): a status is never served older than the feed's 3 s,
/// whether or not the feed's timer has run, and a command's answer from a controller instance the hub has moved on
/// from never replaces the hub's status. The hub here is a stand-in the test drives message by message.
/// </summary>
[TestClass]
public sealed class AlpacaRoofTests
{
    [TestMethod]
    public async Task AStatusOlderThanTheStaleTime_IsNotServed_EvenBeforeTheFeedsTimerRuns()
    {
        var time = new HeldTimersTimeProvider();
        await using var hub = await StatusHub.StartAsync();
        using var client = hub.CreateClient(time);
        await using var roof = new AlpacaRoof(client, NullLogger<AlpacaRoof>.Instance);
        await roof.StartAsync(CancellationToken.None);
        await hub.WaitForClientAsync();
        await hub.SendAsync(Status("A", 1, RoofControllerStatus.Closed), sequence: 1);
        await WaitUntilAsync(() => roof.TryGetCurrent() is not null, "the first status");

        // The hub stays silent, and the feed's stale timer never runs (as if its callback were held up).
        time.Advance(client.Options.StatusFeed.StaleAfter + TimeSpan.FromSeconds(1));

        var read = () => roof.GetCurrent();
        read.Should().Throw<AlpacaException>().Which.Number.Should().Be(AlpacaErrors.ControllerUnavailable);
    }

    [TestMethod]
    public async Task AStatusWithinTheStaleTime_IsServed()
    {
        var time = new HeldTimersTimeProvider();
        await using var hub = await StatusHub.StartAsync();
        using var client = hub.CreateClient(time);
        await using var roof = new AlpacaRoof(client, NullLogger<AlpacaRoof>.Instance);
        await roof.StartAsync(CancellationToken.None);
        await hub.WaitForClientAsync();
        await hub.SendAsync(Status("A", 1, RoofControllerStatus.Closed), sequence: 1);
        await WaitUntilAsync(() => roof.TryGetCurrent() is not null, "the first status");

        time.Advance(client.Options.StatusFeed.StaleAfter - TimeSpan.FromMilliseconds(100));

        roof.GetCurrent().Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task AnAnswerFromARetiredControllerInstance_NeverReplacesTheHubsStatus()
    {
        await using var hub = await StatusHub.StartAsync();
        using var client = hub.CreateClient(TimeProvider.System);
        await using var roof = new AlpacaRoof(client, NullLogger<AlpacaRoof>.Instance);
        await roof.StartAsync(CancellationToken.None);
        await hub.WaitForClientAsync();
        await hub.SendAsync(Status("A", 99, RoofControllerStatus.Open), sequence: 1);
        await WaitUntilAsync(() => roof.TryGetCurrent()?.ControllerInstanceId == "A", "instance A");

        // The controller restarts as B, and only then does A's delayed Stop answer arrive.
        await hub.SendAsync(Status("B", 1, RoofControllerStatus.Opening, RoofMotionDirection.Opening), sequence: 1, instance: "B");
        await WaitUntilAsync(() => roof.TryGetCurrent()?.ControllerInstanceId == "B", "instance B");
        roof.Offer(Status("A", 100, RoofControllerStatus.Closed));

        var current = roof.GetCurrent();
        current.ControllerInstanceId.Should().Be("B");
        current.Status.Should().Be(RoofControllerStatus.Opening);
    }

    [TestMethod]
    public async Task AnAnswerFromTheCurrentInstance_IsServedOnlyWhenItIsNewer()
    {
        await using var hub = await StatusHub.StartAsync();
        using var client = hub.CreateClient(TimeProvider.System);
        await using var roof = new AlpacaRoof(client, NullLogger<AlpacaRoof>.Instance);
        await roof.StartAsync(CancellationToken.None);
        await hub.WaitForClientAsync();
        await hub.SendAsync(Status("A", 10, RoofControllerStatus.Opening, RoofMotionDirection.Opening), sequence: 1);
        await WaitUntilAsync(() => roof.TryGetCurrent() is not null, "the first status");

        roof.Offer(Status("A", 9, RoofControllerStatus.Closed));
        roof.GetCurrent().StatusVersion.Should().Be(10, "an older answer is ignored");

        roof.Offer(Status("A", 11, RoofControllerStatus.PartiallyOpen));
        roof.GetCurrent().StatusVersion.Should().Be(11, "a newer answer is served before the hub repeats it");

        await hub.SendAsync(Status("A", 10, RoofControllerStatus.Opening, RoofMotionDirection.Opening), sequence: 2);
        await Task.Delay(100);
        roof.GetCurrent().StatusVersion.Should().Be(11, "an older hub message does not undo it");
    }

    private static RoofStatusResponse Status(string instance, long version, RoofControllerStatus status, RoofMotionDirection motion = RoofMotionDirection.None)
        => RoofServiceMock.Snapshot(status, motion) with { ControllerInstanceId = instance, StatusVersion = version };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>A clock the test moves, whose timers never fire: as if every timer callback were held up.</summary>
    private sealed class HeldTimersTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 31, 6, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new HeldTimer();

        private sealed class HeldTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A status hub that sends only what the test tells it to.</summary>
    private sealed class StatusHub : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StatusHub(WebApplication app) => _app = app;

        public static async Task<StatusHub> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<Connections>();
            builder.Services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions = RoofClientJson.Create());
            var app = builder.Build();
            app.MapHub<TestHub>(RoofStatusHubContract.Path);
            await app.StartAsync();
            return new StatusHub(app);
        }

        public RoofControllerClient CreateClient(TimeProvider time)
            => ClientTestSupport.CreateClient(() => _app.GetTestServer(), new RoofApiKeyCredential(TestApiKeys.Viewer), time: time);

        public Task WaitForClientAsync() => _app.Services.GetRequiredService<Connections>().First.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task SendAsync(RoofStatusResponse status, long sequence, string instance = "A")
            => _app.Services.GetRequiredService<IHubContext<TestHub>>().Clients.All.SendAsync(
                RoofStatusHubContract.StatusMethod, new RoofStatusHubMessage(status, sequence, DateTimeOffset.UtcNow, instance));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class Connections
        {
            public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class TestHub(Connections connections) : Hub
        {
            public override Task OnConnectedAsync()
            {
                connections.First.TrySetResult();
                return base.OnConnectedAsync();
            }
        }
    }
}
