using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Hubs;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Hubs;

/// <summary>
/// The status hub's publishing, without SignalR: what each connection is sent, in what order, and that a connection that
/// never takes its messages delays neither the controller's status dispatcher nor the other connections (#40).
/// </summary>
[TestClass]
public sealed class RoofStatusBroadcasterTests
{
    /// <summary>Long enough that no heartbeat interferes with a test that does not wait for one.</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromHours(1);

    private readonly FakeRoofControllerService _controller = new() { Snapshot = Version(5) };
    private readonly RecordingStatusSender _sender = new();
    private readonly CapturingLogger<RoofStatusBroadcaster> _logger = new();
    private readonly TestOptionsMonitor<RoofControllerSecurityOptions> _keys = KeyStoreFactory.Monitor(
        KeyStoreFactory.Key("viewer", RoofControllerApiContract.ViewerRole, TestApiKeys.Viewer),
        KeyStoreFactory.Key("operator", RoofControllerApiContract.OperatorRole, TestApiKeys.Operator));

    private RoofApiKeyStore _keyStore = null!;

    [TestInitialize]
    public void Initialize() => _keyStore = KeyStoreFactory.Create(_keys);

    [TestCleanup]
    public void Cleanup()
    {
        _sender.Dispose();
        _keyStore.Dispose();
    }

    [TestMethod]
    public async Task ANewConnection_IsSentTheCurrentSnapshotFirst()
    {
        await using var broadcaster = await StartAsync();
        var before = DateTimeOffset.UtcNow;

        broadcaster.Value.TryRegister("a", Viewer(), () => { }).Should().BeTrue();

        var first = (await _sender.WaitForAsync("a", d => d.Count == 1, "the first message")).Single();
        first.Status.Should().Be(_controller.Snapshot);
        first.Sequence.Should().Be(1);
        first.InstanceId.Should().Be(broadcaster.Value.InstanceId).And.NotBeNullOrWhiteSpace();
        first.ServerTimeUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        _controller.SnapshotCallCount.Should().Be(1, "the snapshot is read when the connection opens");
    }

    [TestMethod]
    public async Task StatusChanges_ReachEveryConnection_InOrder_WithASequenceThatGoesUpByOne()
    {
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("a", Viewer(), () => { });
        broadcaster.Value.TryRegister("b", Viewer(), () => { });
        await _sender.WaitForAsync("b", d => d.Count > 0, "b's first message");

        for (var version = 6; version <= 30; version++)
        {
            _controller.RaiseStatusChanged(Version(version));
            await Task.Delay(2);
        }

        foreach (var connection in new[] { "a", "b" })
        {
            var delivered = await _sender.WaitForAsync(connection, d => d.Count > 0 && d[^1].Status.StatusVersion == 30, "the last change");
            delivered.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            delivered.Select(m => m.Status.StatusVersion).Should().BeInAscendingOrder();
        }

        broadcaster.Value.Latest!.Sequence.Should().Be(27, "a's and b's connect snapshots, then 25 changes");
    }

    [TestMethod]
    public async Task ASnapshotOlderThanTheLastPublished_IsNotPublished()
    {
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("a", Viewer(), () => { });
        _controller.RaiseStatusChanged(Version(7));

        _controller.RaiseStatusChanged(Version(6));
        broadcaster.Value.Latest!.Status.StatusVersion.Should().Be(7, "a heartbeat that raced a change must not turn the status back");
        broadcaster.Value.Latest.Sequence.Should().Be(2);

        _controller.Snapshot = Version(7);
        broadcaster.Value.TryRegister("b", Viewer(), () => { });
        broadcaster.Value.Latest.Sequence.Should().Be(3, "a connect or heartbeat read of the same version is a fresh copy");
        (await _sender.WaitForAsync("a", d => d.Count > 0 && d[^1].Sequence == 3, "the last message"))
            .Should().NotContain(m => m.Status.StatusVersion == 6);
    }

    [TestMethod]
    public async Task AChangeEvent_WhoseVersionWasAlreadyPublished_IsNotPublishedAgain()
    {
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("a", Viewer(), () => { });

        // A new connection read version 8 while the event for 8, raised earlier, still waited on the dispatcher.
        _controller.Snapshot = Version(8) with { SnapshotUtc = DateTimeOffset.UtcNow };
        broadcaster.Value.TryRegister("b", Viewer(), () => { });
        _controller.RaiseStatusChanged(Version(8) with { SnapshotUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });

        broadcaster.Value.Latest!.Sequence.Should().Be(2, "the event's copy of version 8 is older than the one already sent");
        broadcaster.Value.Latest.Status.Should().Be(_controller.Snapshot);
        _controller.RaiseStatusChanged(Version(9));
        broadcaster.Value.Latest.Sequence.Should().Be(3);
    }

    [TestMethod]
    public async Task AConnectionThatNeverTakesItsMessages_DelaysNeitherTheStatusDispatcherNorTheOtherConnections()
    {
        _sender.Script("stuck", SendBehavior.NeverComplete);
        _sender.Script("blocking", SendBehavior.BlockThread);
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("stuck", Viewer(), () => { });
        broadcaster.Value.TryRegister("blocking", Viewer(), () => { });
        broadcaster.Value.TryRegister("live", Viewer(), () => { });
        await _sender.WaitForAttemptsAsync("stuck", 1);
        await _sender.WaitForAttemptsAsync("blocking", 1);

        // Neither stuck send ever completes on its own, so a handler that waited for one would never return.
        var raising = Task.Run(() =>
        {
            for (var version = 6; version < 1006; version++)
            {
                _controller.RaiseStatusChanged(Version(version));
            }
        });
        (await Task.WhenAny(raising, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(raising, "the dispatcher's handler only fills each connection's slot");

        var live = await _sender.WaitForAsync("live", d => d.Count > 0 && d[^1].Status.StatusVersion == 1005, "the newest change");
        live.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        _sender.Attempts("stuck").Should().Be(1, "the stuck connection has one send in progress and nothing queued behind it");
        _sender.Attempts("blocking").Should().Be(1);

        broadcaster.Value.Unregister("stuck");
        await WaitUntilAsync(() => _sender.Cancelled("stuck") == 1, "the stuck send to be cancelled");
        broadcaster.Value.ConnectionCount.Should().Be(2);
    }

    [TestMethod]
    public async Task ASlowConnection_GetsTheNewestSnapshot_NotABacklog()
    {
        _sender.Script("slow", SendBehavior.HoldFirst);
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("slow", Viewer(), () => { });
        await _sender.WaitForAttemptsAsync("slow", 1);

        for (var version = 6; version <= 50; version++)
        {
            _controller.RaiseStatusChanged(Version(version));
        }

        _sender.Release();

        var delivered = await _sender.WaitForAsync("slow", d => d.Count == 2, "the held message and the newest");
        delivered.Select(m => m.Status.StatusVersion).Should().Equal(5, 50);
        await Task.Delay(100);
        _sender.Delivered("slow").Should().HaveCount(2, "the 44 changes in between were replaced, not queued");
    }

    [TestMethod]
    public async Task WhileNothingChanges_AHeartbeatSendsTheCurrentSnapshot()
    {
        await using var broadcaster = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(50));
        broadcaster.Value.TryRegister("a", Viewer(), () => { });
        await _sender.WaitForAsync("a", d => d.Count >= 3, "two heartbeats");

        _controller.Snapshot = Version(9);

        var delivered = await _sender.WaitForAsync("a", d => d[^1].Status.StatusVersion == 9, "a heartbeat with the new snapshot");
        delivered.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        delivered.Should().OnlyContain(m => m.InstanceId == broadcaster.Value.InstanceId);
    }

    [TestMethod]
    public async Task TheHeartbeat_IsDueOneIntervalAfterTheLastMessage()
    {
        var time = new TestSupport.ManualTimeProvider();
        var broadcaster = new RoofStatusBroadcaster(_controller, _sender, RoofCredentialValidator.ForKeysOnly(_keyStore), time, _logger, TimeSpan.FromSeconds(1), RoofStatusBroadcaster.DefaultMaxConnections);
        await using var stopping = new AsyncBroadcaster(broadcaster);
        broadcaster.TryRegister("a", Viewer(), () => { });
        await broadcaster.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => time.ActiveTimerCount == 1, "the heartbeat to wait");
        broadcaster.Latest!.Sequence.Should().Be(1, "the first heartbeat is due one interval after the broadcaster starts");

        time.Advance(TimeSpan.FromMilliseconds(600));
        _controller.Snapshot = Version(6);
        _controller.RaiseStatusChanged(Version(6));
        time.Advance(TimeSpan.FromMilliseconds(400));
        await WaitUntilAsync(() => time.ActiveTimerCount == 1, "the heartbeat to wait again");
        broadcaster.Latest.Sequence.Should().Be(2, "the change 400 ms ago moved the heartbeat on");

        time.Advance(TimeSpan.FromMilliseconds(600));
        await WaitUntilAsync(() => broadcaster.Latest.Sequence == 3, "the heartbeat one interval after the change");
        _controller.SnapshotCallCount.Should().Be(2, "one read when the connection opened, one for the heartbeat");
    }

    [TestMethod]
    public async Task WithNoConnections_TheHeartbeatDoesNotReadTheController()
    {
        await using var broadcaster = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(20));

        await Task.Delay(300);

        _controller.SnapshotCallCount.Should().Be(0);
        broadcaster.Value.Latest.Should().BeNull();
    }

    [TestMethod]
    public async Task AHeartbeatThatCannotReadTheController_IsLogged_AndTheNextOneSends()
    {
        await using var broadcaster = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(50));
        broadcaster.Value.TryRegister("a", Viewer(), () => { });
        await _sender.WaitForAsync("a", d => d.Count >= 1, "the first message");

        _controller.SnapshotException = new InvalidOperationException("controller unavailable");
        await WaitUntilAsync(() => _logger.Contains(LogLevel.Warning, "heartbeat failed"), "the failed heartbeat to be logged");
        var sentWhileFailing = _sender.Delivered("a").Count;
        _controller.SnapshotException = null;

        await _sender.WaitForAsync("a", d => d.Count > sentWhileFailing, "a heartbeat after the controller recovers");
    }

    [TestMethod]
    [DataRow("removed")]
    [DataRow("rotated")]
    [DataRow("re-roled")]
    public async Task AConnectionWhoseKeyIsRevoked_IsClosedWithinAKeyCheckInterval(string change)
    {
        await using var broadcaster = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(50));
        var viewerClosed = 0;
        var operatorClosed = 0;
        broadcaster.Value.TryRegister("viewer", Viewer(), () => Interlocked.Increment(ref viewerClosed));
        broadcaster.Value.TryRegister("operator", Principal(TestApiKeys.Operator), () => Interlocked.Increment(ref operatorClosed));

        var viewer = change switch
        {
            "removed" => null,
            "rotated" => KeyStoreFactory.Key("viewer", RoofControllerApiContract.ViewerRole, TestApiKeys.HashedViewer),
            _ => KeyStoreFactory.Key("viewer", RoofControllerApiContract.OperatorRole, TestApiKeys.Viewer)
        };
        _keys.Set(new RoofControllerSecurityOptions
        {
            ApiKeys = new[] { viewer, KeyStoreFactory.Key("operator", RoofControllerApiContract.OperatorRole, TestApiKeys.Operator) }
                .OfType<RoofApiKeyOptions>().ToList()
        });

        await WaitUntilAsync(() => Volatile.Read(ref viewerClosed) == 1, "the viewer connection to be closed");
        broadcaster.Value.ConnectionCount.Should().Be(1);
        Volatile.Read(ref operatorClosed).Should().Be(0, "the operator's key did not change");
        _logger.Contains(LogLevel.Information, "key or session 'viewer' that opened it was removed, rotated, re-roled or ended").Should().BeTrue();
        _logger.Entries.Should().NotContain(e => e.Message.Contains(TestApiKeys.Viewer, StringComparison.Ordinal), "key values are never logged");
    }

    [TestMethod]
    public async Task WhileStatusChangesFasterThanTheHeartbeat_ARevokedKeysConnection_IsStillClosed()
    {
        await using var broadcaster = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(200));
        var viewerClosed = 0;
        broadcaster.Value.TryRegister("viewer", Viewer(), () => Interlocked.Increment(ref viewerClosed));
        using var moving = new CancellationTokenSource();
        var version = 5L;
        var changes = Task.Run(async () =>
        {
            // A change every 60 ms, as a moving roof's countdowns give one every second: the heartbeat is never due.
            while (!moving.IsCancellationRequested)
            {
                _controller.RaiseStatusChanged(Version(Interlocked.Increment(ref version)));
                await Task.Delay(60);
            }
        });

        await Task.Delay(300);
        _keys.Set(new RoofControllerSecurityOptions
        {
            ApiKeys = [KeyStoreFactory.Key("operator", RoofControllerApiContract.OperatorRole, TestApiKeys.Operator)]
        });

        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref viewerClosed) == 1, "the revoked key's connection to be closed while changes flow");
        }
        finally
        {
            await moving.CancelAsync();
            await changes;
        }

        broadcaster.Value.ConnectionCount.Should().Be(0);
        Interlocked.Read(ref version).Should().BeGreaterThan(8, "changes kept flowing until the connection closed");
    }

    [TestMethod]
    public async Task PastTheLimitForOneKey_ANewConnectionWithThatKeyIsRefused_OtherKeysAreNot()
    {
        await using var broadcaster = await StartAsync(maxConnectionsPerKey: 2);
        broadcaster.Value.TryRegister("a", Viewer(), () => { }).Should().BeTrue();
        broadcaster.Value.TryRegister("b", Viewer(), () => { }).Should().BeTrue();

        broadcaster.Value.TryRegister("c", Viewer(), () => { }, out var refusal).Should().BeFalse();
        refusal.Should().Be("The controller is not accepting more status connections for this key or session.");
        _logger.Contains(LogLevel.Warning, "for this key or session. (2 are already open;").Should().BeTrue();
        broadcaster.Value.TryRegister("d", Principal(TestApiKeys.Operator), () => { }).Should().BeTrue();

        broadcaster.Value.Unregister("a");
        broadcaster.Value.TryRegister("c", Viewer(), () => { }).Should().BeTrue();
        broadcaster.Value.ConnectionCount.Should().Be(3);
    }

    [TestMethod]
    public async Task PastTheConnectionLimit_ANewConnectionIsRefused_UntilOneCloses()
    {
        await using var broadcaster = await StartAsync(maxConnections: 2);
        broadcaster.Value.TryRegister("a", Viewer(), () => { }).Should().BeTrue();
        broadcaster.Value.TryRegister("b", Viewer(), () => { }).Should().BeTrue();

        broadcaster.Value.TryRegister("c", Principal(TestApiKeys.Operator), () => { }, out var refusal).Should().BeFalse();
        refusal.Should().Be("The controller is not accepting more status connections.");
        _logger.Contains(LogLevel.Warning, "status connections. (2 are already open;").Should().BeTrue();

        broadcaster.Value.Unregister("a");
        broadcaster.Value.TryRegister("c", Viewer(), () => { }).Should().BeTrue();
        broadcaster.Value.ConnectionCount.Should().Be(2);
    }

    [TestMethod]
    public async Task ARefusedConnection_DoesNotReadTheController_AndRefusalsAreLoggedAtMostOnceAnInterval()
    {
        var time = new TestSupport.ManualTimeProvider();
        var broadcaster = new RoofStatusBroadcaster(_controller, _sender, RoofCredentialValidator.ForKeysOnly(_keyStore), time, _logger, NoHeartbeat, maxConnections: 1);
        await using var stopping = new AsyncBroadcaster(broadcaster);
        broadcaster.TryRegister("a", Viewer(), () => { }).Should().BeTrue();
        var reads = _controller.SnapshotCallCount;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            broadcaster.TryRegister($"refused-{attempt}", Viewer(), () => { }).Should().BeFalse();
        }

        _controller.SnapshotCallCount.Should().Be(reads, "a connection past a limit is refused before the controller is read");
        _logger.MessagesAt(LogLevel.Warning).Where(m => m.Contains("refused", StringComparison.Ordinal)).Should().ContainSingle();

        time.Advance(RoofStatusBroadcaster.RefusalLogInterval);
        broadcaster.TryRegister("refused-5", Viewer(), () => { }).Should().BeFalse();
        _logger.MessagesAt(LogLevel.Warning).Where(m => m.Contains("refused", StringComparison.Ordinal)).Should().HaveCount(2)
            .And.Contain(m => m.Contains("4 more refusal(s)", StringComparison.Ordinal), "the four in between are counted");
    }

    [TestMethod]
    public async Task AConnectionAlreadyRegistered_IsNotRegisteredTwice()
    {
        await using var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("a", Viewer(), () => { }).Should().BeTrue();

        broadcaster.Value.TryRegister("a", Viewer(), () => { }).Should().BeFalse();
        broadcaster.Value.ConnectionCount.Should().Be(1);
    }

    [TestMethod]
    public async Task Stopping_Unsubscribes_EndsEverySend_AndRefusesNewConnections()
    {
        _sender.Script("stuck", SendBehavior.NeverComplete);
        var broadcaster = await StartAsync();
        broadcaster.Value.TryRegister("stuck", Viewer(), () => { });
        await _sender.WaitForAttemptsAsync("stuck", 1);
        _controller.HasStatusChangedHandlers.Should().BeTrue();

        await broadcaster.Value.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        _controller.HasStatusChangedHandlers.Should().BeFalse();
        _sender.Cancelled("stuck").Should().Be(1);
        broadcaster.Value.ConnectionCount.Should().Be(0);
        broadcaster.Value.TryRegister("late", Viewer(), () => { }).Should().BeFalse();
        broadcaster.Value.Dispose();
    }

    private async Task<AsyncBroadcaster> StartAsync(
        TimeSpan? heartbeat = null,
        int maxConnections = RoofStatusBroadcaster.DefaultMaxConnections,
        int maxConnectionsPerKey = RoofStatusBroadcaster.DefaultMaxConnectionsPerKey)
    {
        var broadcaster = new RoofStatusBroadcaster(
            _controller, _sender, RoofCredentialValidator.ForKeysOnly(_keyStore), TimeProvider.System, _logger, heartbeat ?? NoHeartbeat, maxConnections, maxConnectionsPerKey);
        await broadcaster.StartAsync(CancellationToken.None);
        return new AsyncBroadcaster(broadcaster);
    }

    private ClaimsPrincipal Viewer() => Principal(TestApiKeys.Viewer);

    private ClaimsPrincipal Principal(string key)
    {
        _keyStore.TryValidate(key, out var identity).Should().BeTrue();
        return RoofPrincipalFactory.Create(identity!, RoofControllerSecurityDefaults.ApiKeyScheme);
    }

    private static RoofStatusResponse Version(long version) => FakeRoofControllerService.HealthySnapshot() with { StatusVersion = version };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Stops and disposes the broadcaster at the end of a test.</summary>
    private sealed class AsyncBroadcaster(RoofStatusBroadcaster value) : IAsyncDisposable
    {
        public RoofStatusBroadcaster Value { get; } = value;

        public async ValueTask DisposeAsync()
        {
            await Value.StopAsync(CancellationToken.None);
            Value.Dispose();
        }
    }
}
