using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Safety;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The last-moment re-read of the blocking telescopes (#220, §2, §16 Y1/Z1, §18 R8-1/R8-2, §19 L4, §20 N5): issued at the
/// ticket, at most two in flight and 250 ms apart per telescope, a command using only a read issued after it arrived,
/// results ordered by issue in the telescope's one reading slot, and the Alpaca mount re-read with connected and atpark
/// in parallel (and canpark until a clean full read has shown it).
/// </summary>
[TestClass]
public sealed class RoofAlpacaRereadTests
{
    private const byte CloseRelay = 2;

    [TestMethod]
    public async Task ABurstOfTwentyCloses_ReReadsEachTelescopeAtMostTwice_AndParkThenCloseStillCloses()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Scope.Parked = true; // parked after the poll
        var before = rig.Scope.Rereads;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => rig.CloseAsync()));

        (rig.Scope.Rereads - before).Should().BeLessThanOrEqualTo(2);
        outcomes.Should().OnlyContain(outcome => outcome.Result.IsSuccessful);
        rig.RelayOnWrites(CloseRelay).Should().Be(1);
    }

    [TestMethod]
    public async Task TwentyCloseRetriesOverTwoSeconds_ReReadAtMostNineTimes()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var before = rig.Scope.Rereads;
        var clock = Stopwatch.StartNew();
        var closes = new List<Task<RoofAlpacaCommandOutcome>>();

        // A client retrying every 100 ms for two seconds, not waiting for the answers.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            closes.Add(rig.CloseAsync());
            var due = TimeSpan.FromMilliseconds(100 * (attempt + 1));
            if (clock.Elapsed < due)
            {
                await Task.Delay(due - clock.Elapsed);
            }
        }

        (await Task.WhenAll(closes)).Should().OnlyContain(outcome => outcome.Result.IsSuccessful);
        (rig.Scope.Rereads - before).Should().BeLessThanOrEqualTo(9, "re-reads of one telescope are issued no closer than 250 ms apart");
        rig.RelayOnWrites(CloseRelay).Should().Be(1);
    }

    [TestMethod]
    public async Task ASharedReRead_IsCancelledOnlyAtTheLatestJoiningBudget_AndTheLaterCloseClosesWithinItsOwn()
    {
        // §19 L2 (PR #230 rereview O-F9): two Closes wait for the same re-read; it is cancelled only when the later one's
        // budget ends, so a read that answers after the earlier one's budget still lets the later one close in time.
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(budgetMs: 1000, time: time)).Ready();
        var (first, second, shared, advance) = await TwoClosesSharingAReReadAsync(rig, time);

        await advance(850); // 1100 ms: past the first Close's budget, not the second's
        var early = await first.WaitAsync(TimeSpan.FromSeconds(10));
        shared.IsCancellationRequested.Should().BeFalse("the shared re-read goes on for the later Close's budget");
        second.IsCompleted.Should().BeFalse("the shared re-read is still running for the later Close");

        rig.ReleaseHeldRereads(parked: true); // the shared re-read answers: parked
        var late = await second.WaitAsync(TimeSpan.FromSeconds(10));

        early.Result.IsSuccessful.Should().BeFalse("its budget ended before the read answered");
        late.Result.IsSuccessful.Should().BeTrue(late.Result.Error?.Message);
        rig.Scope.Rereads.Should().Be(2, "both Closes waited for one shared re-read");
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task ASharedReReadThatNeverAnswers_IsCancelledAtTheLatestJoiningBudget_NeverAtAnEarlierOne()
    {
        // PR #230 rereview round 3, Sol O-F9: the re-read's own cancellation token, observed directly.
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(budgetMs: 1000, time: time)).Ready();
        var (first, second, shared, advance) = await TwoClosesSharingAReReadAsync(rig, time);

        await advance(850); // 1100 ms: the first Close's budget (1050 ms) has ended
        shared.IsCancellationRequested.Should().BeFalse("an earlier joining budget never cancels the shared re-read");
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Result.IsSuccessful.Should().BeFalse();
        await advance(40); // 1140 ms
        shared.IsCancellationRequested.Should().BeFalse("the later Close's budget ends at 1150 ms");
        await advance(20); // 1160 ms
        shared.IsCancellationRequested.Should().BeTrue("the re-read is cancelled when the latest joining budget ends");

        (await second.WaitAsync(TimeSpan.FromSeconds(10))).Result.IsSuccessful.Should().BeFalse("the re-read never answered");
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task AReRead_IsCancelledAtTheReadTimeoutFromItsIssue_EvenWhenItsJoiningBudgetsEndLater()
    {
        // The read timeout (500 ms here) caps every re-read from its issue, however late a joining budget ends (2 s here).
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(budgetMs: 2000, time: time, readTimeout: TimeSpan.FromMilliseconds(500))).Ready();
        var (first, second, shared, advance) = await TwoClosesSharingAReReadAsync(rig, time);

        await advance(490); // 740 ms: 490 ms after its issue at 250 ms
        shared.IsCancellationRequested.Should().BeFalse();
        await advance(20); // 760 ms: past the read timeout from its issue; the budgets end at 2050 and 2150 ms
        shared.IsCancellationRequested.Should().BeTrue("the read timeout caps the re-read from its issue");

        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Result.IsSuccessful.Should().BeFalse();
        (await second.WaitAsync(TimeSpan.FromSeconds(10))).Result.IsSuccessful.Should().BeFalse();
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    /// <summary>
    /// A held re-read issued for nobody at 0 ms, then a Close at 50 ms whose re-read is queued for 250 ms (the spacing) and
    /// a Close at 150 ms that joins it: returns both Closes, the shared re-read's cancellation token, and a clock step.
    /// </summary>
    private static async Task<(Task<RoofAlpacaCommandOutcome> First, Task<RoofAlpacaCommandOutcome> Second, CancellationToken Shared, Func<int, Task> Advance)>
        TwoClosesSharingAReReadAsync(AlpacaCommandRig rig, ManualTimeProvider time)
    {
        rig.Scope.HoldRereads = true;
        async Task Advance(int milliseconds)
        {
            time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            await Task.Delay(50);
        }

        await Advance(10); // past the background poll the rig started with
        rig.Ticket(); // a re-read issued now, for nobody below: neither Close may use it
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Rereads == 1, "the first re-read");
        await Advance(50);
        var first = rig.CloseAsync(); // arrives at 50 ms: its re-read is queued for 250 ms (the spacing)
        await Advance(100);
        var second = rig.CloseAsync(); // arrives at 150 ms: joins that queued re-read
        await Advance(100);
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Rereads == 2, "the shared re-read, issued at 250 ms");
        return (first, second, rig.Scope.RereadTokens[1], Advance);
    }

    [TestMethod]
    public async Task ALateClear_NeverOverwritesANewerNotClear_InTheSlot()
    {
        var source = new OrderedTelescope("Main");
        await using var ordered = (await AlpacaCommandRig.StartAsync(budgetMs: 2000, telescopes: [source])).Ready();

        var first = ordered.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => source.Held == 1, "the first re-read");
        await Task.Delay(RoofSafetyService.RereadSpacing);
        var second = ordered.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => source.Held == 2, "the second re-read");

        source.Release(index: 1, parked: false); // the newer read ends first: not parked
        (await second).Rule().Should().Be("NotSafe"); // waits only for that read, so it is in the slot now
        source.Release(index: 0, parked: true);  // then the older one: a late "parked"

        (await first).Rule().Should().Be("NotSafe", "the slot keeps the newer not-clear");
        ordered.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task ACloseAfterAPark_NeverJoinsAReReadIssuedBeforeItArrived()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false, budgetMs: 2000)).Ready();
        rig.Scope.HoldRereads = true;
        var early = rig.CloseAsync(); // a re-read issued now sees the mount unparked
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Held == 1, "the early re-read");

        await Task.Delay(200); // the mount parks; a Close comes 200 ms later
        var late = rig.CloseAsync();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Held == 2, "the late Close's own re-read");
        rig.Scope.ReleaseOne(false);
        (await early).Rule().Should().Be("NotSafe");
        rig.Scope.ReleaseOne(true);

        (await late).Result.IsSuccessful.Should().BeTrue("its own re-read, issued after it arrived, saw the park");
    }

    [TestMethod]
    public async Task AMountAnsweringEachGetIn200Ms_ParkThenCloseCloses_WithinTheDeadline()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Scope.Parked = true;
        rig.Scope.RereadDelay = TimeSpan.FromMilliseconds(200);
        var clock = Stopwatch.StartNew();

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600 + 400), "budget + 400 ms");
    }

    [TestMethod]
    public async Task AReReadThatTimesOut_IsUnknown_AndTheRefusalComesWithinTheDeadline()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(budgetMs: 300)).Ready();
        rig.Scope.RereadDelay = TimeSpan.FromSeconds(3);
        var clock = Stopwatch.StartNew();

        var outcome = await rig.CloseAsync();

        outcome.ReasonKeys.Should().Equal("scope-unknown:Main");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(300 + 400), "budget + 400 ms");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task ABackgroundPollStuckInFlight_DoesNotHoldUpParkThenClose()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Scope.HoldPolls = true;
        _ = rig.Safety.PollAsync(CancellationToken.None); // stuck
        rig.Scope.Parked = true;

        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue("a re-read never waits behind a background poll");
    }

    // ---- The Alpaca mount's re-read ------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheAlpacaMountsReRead_SendsConnectedAndAtParkInParallel_AndCanParkUntilAFullReadShowedIt()
    {
        var device = new FakeTelescope();
        var source = new AlpacaTelescopeParkSource(AlpacaSafetySourceTests.Telescope("Main"), new AlpacaSafetySourceTests.Factory(() => device));

        (await source.RereadAsync(CancellationToken.None)).IsParked.Should().BeTrue();
        device.Members().Should().BeEquivalentTo("connected", "atpark", "canpark");

        device.Clear();
        await source.ReadParkStateAsync(CancellationToken.None);
        device.Clear();
        (await source.RereadAsync(CancellationToken.None)).IsParked.Should().BeTrue();
        device.Members().Should().BeEquivalentTo("connected", "atpark");
        source.RecentLatencies.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task TheAlpacaMountsReRead_NeverClearsAMountNotConnected_OrOneThatCannotPark()
    {
        var device = new FakeTelescope { Connected = false };
        var source = new AlpacaTelescopeParkSource(AlpacaSafetySourceTests.Telescope("Main"), new AlpacaSafetySourceTests.Factory(() => device));
        await FluentActions.Awaiting(() => source.RereadAsync(CancellationToken.None)).Should().ThrowAsync<RoofSafetySourceException>();

        device.Connected = true;
        device.CanPark = false; // reported parked, but it cannot park: a configuration error, never clear
        await FluentActions.Awaiting(() => source.RereadAsync(CancellationToken.None)).Should().ThrowAsync<RoofSafetySourceException>()
            .WithMessage(AlpacaTelescopeParkSource.CannotParkFailure);
        await FluentActions.Awaiting(() => source.ReadParkStateAsync(CancellationToken.None)).Should().ThrowAsync<RoofSafetySourceException>();
        await FluentActions.Awaiting(() => source.RereadAsync(CancellationToken.None)).Should().ThrowAsync<RoofSafetySourceException>(
            "a full read that found CanPark false keeps the re-read asking");
    }

    /// <summary>A telescope whose re-reads each wait for the test, released in any order.</summary>
    internal sealed class OrderedTelescope(string name) : ITelescopeRereadSource
    {
        private readonly List<TaskCompletionSource<bool>> _held = [];

        public string Name { get; } = name;

        public TimeSpan? MaxReadingAge => null;

        public bool BlocksRoof => true;

        public IReadOnlyList<TimeSpan> RecentLatencies => [];

        public int Held
        {
            get { lock (_held) { return _held.Count; } }
        }

        public void Release(int index, bool parked)
        {
            lock (_held)
            {
                _held[index].TrySetResult(parked);
            }
        }

        public Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken) => Task.FromResult(new TelescopeParkReading(false));

        public async Task<TelescopeParkReading> RereadAsync(CancellationToken cancellationToken)
        {
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_held)
            {
                _held.Add(held);
            }

            return new TelescopeParkReading(await held.Task.WaitAsync(cancellationToken));
        }
    }

    /// <summary>An Alpaca telescope answering connected, canpark and atpark as set, recording what it was asked.</summary>
    private sealed class FakeTelescope : HttpMessageHandler
    {
        private readonly List<string> _members = [];

        public bool Connected { get; set; } = true;

        public bool CanPark { get; set; } = true;

        public bool AtPark { get; set; } = true;

        public IReadOnlyList<string> Members()
        {
            lock (_members)
            {
                return [.. _members];
            }
        }

        public void Clear()
        {
            lock (_members)
            {
                _members.Clear();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var member = request.RequestUri!.AbsolutePath.Split('/')[^1];
            lock (_members)
            {
                _members.Add(member);
            }

            var transaction = HttpUtility.ParseQueryString(request.RequestUri.Query)["ClientTransactionID"] ?? "0";
            var value = member switch
            {
                "connected" => Connected,
                "canpark" => CanPark,
                _ => AtPark
            };
            var body = "{\"Value\":" + (value ? "true" : "false") + ",\"ClientTransactionID\":" + transaction + ",\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
