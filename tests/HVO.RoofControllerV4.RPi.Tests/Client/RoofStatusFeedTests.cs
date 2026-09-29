using System.Collections.Concurrent;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client's status feed against the controller's real status hub, in process (#44): the first snapshot, ordering,
/// staleness, a refused credential, and a controller that restarts.
/// </summary>
[TestClass]
public sealed class RoofStatusFeedTests
{
    private static void RaiseStatus(RoofApiTestHost host, RoofStatusResponse status)
        => host.RoofService.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(status));

    [TestMethod]
    public async Task TheFeed_ShowsTheFirstSnapshot_ThenEachChange_InOrder()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        await using var feed = client.CreateStatusFeed();
        var received = new ConcurrentQueue<RoofStatusReceivedEventArgs>();
        feed.StatusReceived += (_, e) => received.Enqueue(e);

        feed.State.Should().Be(RoofStatusFeedState.Stopped);
        feed.Current.Should().BeNull();
        feed.Start();
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot");
        // The hub keeps only the newest snapshot waiting for each connection, so each change is awaited before the next.
        RaiseStatus(host, RoofServiceMock.Snapshot() with { StatusVersion = 12 });
        await WaitUntilAsync(() => feed.Status?.StatusVersion == 12, "the first change");
        RaiseStatus(host, RoofServiceMock.Snapshot(faultLatched: true) with { StatusVersion = 13 });
        await WaitUntilAsync(() => feed.Status?.StatusVersion == 13, "the second change");

        var messages = received.ToArray();
        messages.Select(e => e.Status.StatusVersion).Should().Equal(11, 12, 13);
        messages.Select(e => e.Message.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        messages[0].Previous.Should().BeNull();
        messages[1].Previous.Should().BeSameAs(messages[0].Message);
        messages.Should().OnlyContain(e => !e.IsNewInstance);
        messages[1].SafetyAlert.Should().BeNull();
        messages[2].SafetyAlert.Should().NotBeNull();
        messages[2].SafetyAlert!.Title.Should().Be("Fault latched");
        feed.IsStale.Should().BeFalse();
        feed.LastError.Should().BeNull();
        feed.ConnectionCount.Should().Be(1);
        feed.LastMessageUtc.Should().NotBeNull();
    }

    [TestMethod]
    public void AnOlderCopy_DoesNotReplaceTheSnapshot()
    {
        var first = new RoofStatusHubMessage(RoofServiceMock.Snapshot(), 5, DateTimeOffset.UtcNow, "instance-a");

        RoofStatusFeed.ShouldApply(null, first).Should().BeTrue("the first message is always applied");
        RoofStatusFeed.ShouldApply(first, first with { Sequence = 6 }).Should().BeTrue();
        RoofStatusFeed.ShouldApply(first, first with { Sequence = 5 }).Should().BeFalse("a repeat is not newer");
        RoofStatusFeed.ShouldApply(first, first with { Sequence = 4 }).Should().BeFalse("an older message is dropped");
        RoofStatusFeed.ShouldApply(first, first with { Sequence = 1, InstanceId = "instance-b" }).Should().BeTrue(
            "a restarted controller starts its sequence again");
    }

    [TestMethod]
    public async Task TheView_GoesStale_WhenTheControllerFallsSilent_AndCurrentWithTheNextMessage()
    {
        // The controller's clock is frozen, so it sends no heartbeat; the client's clock moves only when told.
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), time: clientClock);
        await using var feed = client.CreateStatusFeed();
        var changes = 0;
        feed.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot");

        clientClock.Advance(ClientTestSupport.FastFeed.StaleAfter - TimeSpan.FromMilliseconds(1));
        feed.IsStale.Should().BeFalse();
        var before = Volatile.Read(ref changes);
        clientClock.Advance(TimeSpan.FromMilliseconds(1));

        feed.IsStale.Should().BeTrue("no message arrived for StaleAfter");
        feed.StaleSince.Should().Be(clientClock.GetUtcNow());
        feed.State.Should().Be(RoofStatusFeedState.Connected, "stale is about the data, not the connection");
        feed.Current.Should().NotBeNull("the last known state is kept, shown as stale");
        Volatile.Read(ref changes).Should().Be(before + 1);

        RaiseStatus(host, RoofServiceMock.Snapshot() with { StatusVersion = 12 });
        await WaitUntilAsync(() => !feed.IsStale && feed.Status?.StatusVersion == 12, "the view to be current again");
        feed.StaleSince.Should().BeNull();
        Volatile.Read(ref changes).Should().Be(before + 2);

        await feed.StopAsync();

        feed.State.Should().Be(RoofStatusFeedState.Stopped);
        feed.IsStale.Should().BeTrue("a stopped feed no longer knows the state");
        feed.Status!.StatusVersion.Should().Be(12);
    }

    [TestMethod]
    public async Task ARefusedCredential_WaitsForANewOne_ThenConnects()
    {
        using var host = RoofClientApiTests.CreateHost();
        var slowRetry = new RoofStatusFeedOptions
        {
            InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
            MaxReconnectDelay = TimeSpan.FromHours(1),
            ReconnectJitter = 0
        };
        using var client = CreateClient(host, credential: null, feed: slowRetry);
        await using var feed = client.CreateStatusFeed();

        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Unauthorized, "the refusal");
        feed.Current.Should().BeNull();
        feed.LastError.Should().NotBeNull();

        client.Credential = new RoofApiKeyCredential(TestApiKeys.Viewer);

        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the connection with the new key");
        feed.LastError.Should().BeNull();
    }

    [TestMethod]
    public async Task AKioskFeed_UsesTheDeviceKey_SoAnEndedPinSessionDoesNotInterruptIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, Security.TestSecrets.Pin);
        var kiosk = new RoofKioskCredential(RoofClientApiTests.KioskKey);
        using var client = CreateClient(host, kiosk);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await using var feed = client.CreateStatusFeed();

        // Signed in first, so the feed's one connection is made while the kiosk holds a session.
        var session = await client.Auth.SignInWithPinAsync("olive", Security.TestSecrets.Pin);
        kiosk.GetHeaders(RoofCredentialUse.Request).Select(header => header.Key).Should().Contain("Authorization");
        kiosk.GetHeaders(RoofCredentialUse.StatusHub).Should().ContainSingle()
            .Which.Key.Should().Be(RoofControllerApiContract.ApiKeyHeaderName, "the hub gets the device key, never the person's session");
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot");

        await admin.Identity.EndSessionAsync(session.SessionId);
        RaiseStatus(host, RoofServiceMock.Snapshot() with { StatusVersion = 12 });

        await WaitUntilAsync(() => feed.Status?.StatusVersion == 12, "status after the PIN session ended");
        feed.State.Should().Be(RoofStatusFeedState.Connected);
        feed.ConnectionCount.Should().Be(1, "ending the person's session does not touch the hub connection");
    }

    [TestMethod]
    public async Task AThrowingHandler_IsLogged_AndTheFeedAndTheOtherHandlersCarryOn()
    {
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), time: clientClock);
        await using var feed = client.CreateStatusFeed();
        var stateChanges = 0;
        var received = new ConcurrentQueue<long>();
        feed.StateChanged += (_, _) => throw new InvalidOperationException("A state handler failed (test).");
        feed.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);
        feed.StatusReceived += (_, _) => throw new ObjectDisposedException("view (test)");
        feed.StatusReceived += (_, e) => received.Enqueue(e.Status.StatusVersion);

        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && received.Contains(11), "the first snapshot");

        // The silence timer runs on the calling thread here, so an escaping exception would fail this line.
        var before = Volatile.Read(ref stateChanges);
        clientClock.Advance(ClientTestSupport.FastFeed.StaleAfter);
        feed.IsStale.Should().BeTrue();
        Volatile.Read(ref stateChanges).Should().Be(before + 1, "the handler after the throwing one still ran");

        RaiseStatus(host, RoofServiceMock.Snapshot() with { StatusVersion = 12 });
        await WaitUntilAsync(() => received.Contains(12) && !feed.IsStale, "the next snapshot after a handler threw");
        feed.State.Should().Be(RoofStatusFeedState.Connected);
        feed.ConnectionCount.Should().Be(1, "a throwing handler does not end the connection or the loop");
        feed.LastError.Should().BeNull();
    }

    [TestMethod]
    public async Task StartingAgain_WhileAStopIsEnding_RunsOneLoop_AndEndsConnected()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        await using var feed = client.CreateStatusFeed();
        var states = new ConcurrentQueue<RoofStatusFeedState>();
        feed.StateChanged += (_, _) => states.Enqueue(feed.State);
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot");

        var stopping = feed.StopAsync();
        feed.Start();
        await stopping;
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.ConnectionCount == 2, "the second connection");
        await Task.Delay(300);

        feed.State.Should().Be(RoofStatusFeedState.Connected, "the old loop's last state change comes before the new loop's first");
        feed.ConnectionCount.Should().Be(2, "one connection before the restart and one after");
        states.Last().Should().Be(RoofStatusFeedState.Connected);
        RaiseStatus(host, RoofServiceMock.Snapshot() with { StatusVersion = 12 });
        await WaitUntilAsync(() => feed.Status?.StatusVersion == 12, "status on the new connection");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task WhenTheControllerRestarts_TheFeedReconnects_AndReportsANewInstance()
    {
        RoofApiTestHost? current = RoofClientApiTests.CreateHost();
        _ = current.Server;
        TestServer Server() => Volatile.Read(ref current)?.Server ?? throw new ObjectDisposedException("controller");
        using var client = CreateClient(Server, new RoofApiKeyCredential(TestApiKeys.Viewer));
        await using var feed = client.CreateStatusFeed();
        var received = new ConcurrentQueue<RoofStatusReceivedEventArgs>();
        feed.StatusReceived += (_, e) => received.Enqueue(e);
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot");
        var before = feed.Current!.InstanceId;

        var stopping = Volatile.Read(ref current)!;
        Volatile.Write(ref current, null);
        stopping.Dispose();
        await WaitUntilAsync(() => feed.IsStale && feed.State == RoofStatusFeedState.Reconnecting, "the feed to notice the restart");
        feed.Current!.InstanceId.Should().Be(before, "the last known state is kept while the controller is away");

        using var restarted = RoofClientApiTests.CreateHost();
        _ = restarted.Server;
        Volatile.Write(ref current, restarted);
        await WaitUntilAsync(() => feed.Current?.InstanceId != before && !feed.IsStale, "the restarted controller's snapshot", TimeSpan.FromSeconds(20));

        received.Last().IsNewInstance.Should().BeTrue();
        received.Where(e => e.IsNewInstance).Should().ContainSingle();
        feed.State.Should().Be(RoofStatusFeedState.Connected);
        feed.ConnectionCount.Should().Be(2);
    }
}
