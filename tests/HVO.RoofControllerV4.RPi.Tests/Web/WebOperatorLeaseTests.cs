using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Web.Components.Layout;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Roof;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static HVO.RoofControllerV4.RPi.Tests.Web.WebAlpacaSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The operator lease an operator's web session holds while any of their pages is open (#220), against the controller's
/// real Alpaca command state: taken by the first page, kept by the person's own actions (never by a timer or a status),
/// released by the last page closing and by signing out, and, once it idles out, not taken again until the person does
/// something.
/// </summary>
[TestClass]
public sealed class WebOperatorLeaseTests
{
    private static readonly TimeSpan Step = RoofAlpacaText.LeaseHeartbeatInterval;

    [TestMethod]
    public async Task AnOperatorsPage_HoldsTheLease_AndOnlyThePersonsActionsCountAsInput()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        var lease = harness.Session!.OperatorLease!;
        lease.IsRenewing.Should().BeTrue("the page's start holds the lease");

        await harness.TakeTheLeaseAsync(requests);
        harness.Alpaca().GetStatus().OperatorLease.Should().BeEquivalentTo(new { Held = true, Holder = "olga" });

        await lease.BeatAsync(requests);
        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Open));
        await harness.WaitForAsync(view => view.Status?.Status == RoofControllerStatus.Open, "a status update");
        await lease.BeatAsync(requests);
        await harness.Console.RefreshAsync();
        await lease.BeatAsync(requests);
        await harness.Session.StopAsync();
        await lease.BeatAsync(requests);

        requests.Should().Equal(
            ["POST input", "POST", "POST", "POST input", "POST input"],
            "the first heartbeat takes the lease; a status update is not input; reading the status and Stop are");
    }

    [TestMethod]
    public async Task AViewersPage_HoldsNoLease()
    {
        using var harness = await WebRoofHarness.CreateAsync(RoofControllerApiContract.ViewerRole, settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();

        harness.Clock.Advance(Step);
        harness.Console.NoteInput();
        await Task.Delay(100);

        harness.Session!.OperatorLease.Should().BeNull();
        requests.Should().BeEmpty();
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheLastPageClosing_ReleasesTheLease_AndAPageOpeningTakesItAgain()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;
        using var second = Page(harness);
        await second.StartAsync();

        harness.Console.Dispose();

        lease.IsRenewing.Should().BeTrue("another of the person's pages is open");
        second.Dispose();
        lease.IsRenewing.Should().BeFalse();
        await lease.Stopping;
        requests.Should().Equal("POST input", "DELETE");
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse();

        using var third = Page(harness);
        await third.StartAsync();
        harness.Clock.Advance(TimeSpan.Zero);
        // The controller handles a request before the test's hook records it.
        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held && Count(requests) == 3, "the lease to be taken again");
        requests.Should().Equal("POST input", "DELETE", "POST input");
    }

    [TestMethod]
    public async Task SigningOut_ReleasesTheLease_WhileTheSessionCanStillReleaseIt()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;

        await harness.Session.SignOutAsync();

        requests.Should().Equal("POST input", "DELETE");
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse();
        lease.IsRenewing.Should().BeFalse();
        harness.Console.NoteInput();
        harness.Clock.Advance(Step);
        await Task.Delay(100);
        requests.Should().HaveCount(2, "nothing is renewed for a session that ended");
    }

    [TestMethod]
    public async Task AnIdleLease_IsNotTakenBackByHeartbeats_ThePageSaysAlpacaMayNowMove_AndInputTakesItBackAtOnce()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(idleMinutes: 1), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;

        // Both clocks move together; the controller's heartbeats count down its idle limit, which the person never resets.
        for (var elapsed = Step; elapsed < TimeSpan.FromMinutes(1); elapsed += Step)
        {
            await StepAsync(harness, lease, (TimeSpan.FromMinutes(1) - elapsed).TotalSeconds);
        }

        harness.ServerClock.Advance(Step);
        await ClientTestSupport.WaitUntilAsync(() => !harness.Alpaca().GetStatus().OperatorLease.Held, "the controller to lapse it");
        for (var beat = 0; beat < 2; beat++)
        {
            var sent = Count(requests);
            harness.ServerClock.Advance(Step);
            harness.Clock.Advance(Step);
            await ClientTestSupport.WaitUntilAsync(() => Count(requests) == sent + 1 && lease.State is { Held: false }, "a heartbeat after the lapse");
        }

        requests.TakeLast(2).Should().Equal(["POST", "POST"], "the heartbeats go on, without input");
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse("a heartbeat without input never takes a lapsed lease back");
        await harness.WaitForAsync(view => view.AlpacaLeaseNotice == RoofAlpacaText.MayNowMove, "the page to say Alpaca may now move the roof");

        var before = Count(requests);
        harness.Console.NoteInput();

        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held && Count(requests) == before + 1, "the person's input to take it back at once");
        requests[^1].Should().Be("POST input");
        await harness.WaitForAsync(view => view.AlpacaLeaseNotice != RoofAlpacaText.MayNowMove, "the page to stop saying so");
    }

    [TestMethod]
    public async Task AGrant_StepsThePersonAway_TheHeartbeatsGoOnWithoutInput_AndTheirNextClickTakesTheLeaseBack()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var grants = new List<string>();
        var requests = harness.RecordLeaseRequests(grants);
        harness.RaiseRefusedClose("scope-not-clear:Main");
        await using var context = Context(harness);
        var page = context.Render<Dashboard>();
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-grant-ask]"));
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;

        // The clicks that ask and confirm are input before the grant, never after it.
        page.Click("[data-testid=alpaca-grant-ask]");
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-question-confirm]").HasAttribute("data-alpaca-step-away").Should().BeTrue());
        page.Click("[data-testid=alpaca-question-confirm]");

        await page.WaitForAssertionAsync(() =>
        {
            var grant = harness.Alpaca().Grants().Should().ContainSingle().Subject;
            harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse("the person stepped away with the grant");
            lease.SteppedAway.Should().BeTrue();
            var stepped = RoofAlpacaText.DescribeSteppedAway(harness.Console.View.Status!.Alpaca!.Grants.Single(), RoofAlpacaText.ClickToTakeBack);
            stepped.Should().StartWith("You stepped away from the operator lease: Alpaca may close the roof once in the next 5 min. Click or type on this page");
            Text(page.Find("[data-testid=alpaca-lease-notice]")).Should().Be(stepped);
            harness.Console.View.AlpacaLeaseNotice.Should().Be(stepped);
        });
        grants.Should().ContainSingle();
        await lease.BeatAsync(requests);
        await lease.BeatAsync(requests);
        requests.Should().Equal(["POST input", "POST", "POST"], "the confirming click is not input after the grant, nor are the renders");
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse();
        harness.Alpaca().Grants().Should().ContainSingle("the grant waits for an Alpaca Close");

        page.Click("[data-testid=refresh]");

        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held && Count(requests) == 4, "the next click to take the lease back");
        requests[^1].Should().Be("POST input");
        harness.Alpaca().Grants().Should().BeEmpty("taking the lease back drops the grant");
        await page.WaitForAssertionAsync(() =>
        {
            lease.SteppedAway.Should().BeFalse();
            page.FindAll("[data-testid=alpaca-lease-notice], [data-testid=alpaca-grant]").Should().BeEmpty();
        });
    }

    [TestMethod]
    public async Task AnOpenGrant_StepsThePersonAway_SayingAlpacaMayOpenTheRoofOnce_AndTheirNextClickTakesTheLeaseBack()
    {
        var open = new AlpacaOpenGrantStandIn { Mode = RoofAlpacaCommandMode.OpenAndClose };
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), configureServices: open.AddTo, operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        harness.RaiseRefusedOpen("weather-unsafe:Sky");
        await using var context = Context(harness);
        var page = context.Render<Dashboard>();
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-grant-ask]"));
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;

        page.Click("[data-testid=alpaca-grant-ask]");
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-question-confirm]").HasAttribute("data-alpaca-step-away").Should().BeTrue());
        page.Click("[data-testid=alpaca-question-confirm]");

        await page.WaitForAssertionAsync(() =>
        {
            open.OpenGrant(harness.Host.Services).Should().NotBeNull();
            harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse("the person stepped away with the grant");
            lease.SteppedAway.Should().BeTrue();
            const string stepped = "You stepped away from the operator lease: Alpaca may open the roof once in the next 5 min. "
                + "Click or type on this page to take control back; that cancels the grant.";
            RoofAlpacaText.DescribeSteppedAway(harness.Console.View.Status!.Alpaca!.Grants, RoofAlpacaText.ClickToTakeBack).Should().Be(stepped);
            Text(page.Find("[data-testid=alpaca-lease-notice]")).Should().Be(stepped);
        });

        page.Click("[data-testid=refresh]");

        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held, "the next click to take the lease back");
        open.OpenGrant(harness.Host.Services).Should().BeNull("taking the lease back drops the grant");
        await page.WaitForAssertionAsync(() => page.FindAll("[data-testid=alpaca-lease-notice], [data-testid=alpaca-grant]").Should().BeEmpty());
    }

    [TestMethod]
    public async Task WithAnOpenGrantAndACloseGrantInForce_TheSteppedAwayNotice_SaysAlpacaMayOpenAndCloseOnceEach()
    {
        // Another person's Open grant is in force when this person grants a Close.
        var open = new AlpacaOpenGrantStandIn { Mode = RoofAlpacaCommandMode.OpenAndClose };
        open.Show(new RoofOverrideGrant
        {
            Id = "open-1",
            Kind = RoofAlpacaCommandKind.Open,
            ReasonKeys = ["weather-unsafe:Sky"],
            GrantedBy = "ada (signed in)",
            Ui = "session",
            GrantedUtc = WebRoofHarness.Start,
            SecondsRemaining = 120
        });
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), configureServices: open.AddTo, operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        harness.RaiseRefusedClose("scope-not-clear:Main");
        await using var context = Context(harness);
        var page = context.Render<Dashboard>();
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-grant-ask]"));
        await harness.TakeTheLeaseAsync(requests);

        page.Click("[data-testid=alpaca-grant-ask]");
        await page.WaitForAssertionAsync(() => page.Find("[data-testid=alpaca-question-confirm]"));
        page.Click("[data-testid=alpaca-question-confirm]");

        await page.WaitForAssertionAsync(() =>
        {
            harness.Console.View.Status!.Alpaca!.Grants.Select(grant => grant.Kind).Should().BeEquivalentTo([RoofAlpacaCommandKind.Close, RoofAlpacaCommandKind.Open]);
            Text(page.Find("[data-testid=alpaca-lease-notice]")).Should().Be(
                "You stepped away from the operator lease: Alpaca may open and close the roof once each in the next 5 min. "
                + "Click or type on this page to take control back; that cancels the grants.");
        });
    }

    [TestMethod]
    public async Task TheIdleWarning_IsOnEveryPage_InTheLastMinute_AndTheNextInputClearsItAtOnce()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(idleMinutes: 2), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;
        await using var context = Context(harness);
        var banner = context.Render<AlpacaBanner>();

        for (var elapsed = Step; elapsed <= TimeSpan.FromSeconds(50); elapsed += Step)
        {
            await StepAsync(harness, lease, (TimeSpan.FromMinutes(2) - elapsed).TotalSeconds);
        }

        harness.Console.View.AlpacaLeaseNotice.Should().BeNull("70 s are left");
        banner.FindAll("[data-testid=alpaca-lease-banner]").Should().BeEmpty();

        await StepAsync(harness, lease, 60);

        var warning = "Your operator lease lapses in 60 s without input; then Alpaca may move the roof. Touch anything to keep it.";
        harness.Console.View.AlpacaLeaseNotice.Should().Be(warning);
        await banner.WaitForAssertionAsync(() => Text(banner.Find("[data-testid=alpaca-lease-banner]")).Should().Be(warning));

        harness.Console.NoteInput();

        await ClientTestSupport.WaitUntilAsync(() => lease.State?.IdleSecondsRemaining == 120, "the input to go at once");
        requests[^1].Should().Be("POST input");
        await banner.WaitForAssertionAsync(() => banner.FindAll("[data-testid=alpaca-lease-banner]").Should().BeEmpty());
    }

    [TestMethod]
    public async Task TheBanner_PassesThePersonsInputAndNavigation_ToTheLease()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await harness.StartLiveAsync();
        await harness.TakeTheLeaseAsync(requests);
        var lease = harness.Session!.OperatorLease!;
        await using var context = Context(harness);
        var watch = context.JSInterop.SetupVoid(AlpacaBanner.WatchFunction, _ => true);
        var banner = context.Render<AlpacaBanner>();
        await banner.WaitForAssertionAsync(() => watch.Invocations.Should().ContainSingle("the page's script starts passing the person's input"));

        await lease.BeatAsync(requests);
        await banner.InvokeAsync(banner.Instance.NoteInput);
        await lease.BeatAsync(requests);
        await lease.BeatAsync(requests);
        await banner.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>().NavigateTo("health"));
        await lease.BeatAsync(requests);

        requests.Should().Equal("POST input", "POST", "POST input", "POST", "POST input");
    }

    // One heartbeat interval on both clocks: the controller's first, then the page's, whose heartbeat answers with
    // what is left of the idle limit.
    private static async Task StepAsync(WebRoofHarness harness, WebOperatorLease lease, double left)
    {
        harness.ServerClock.Advance(Step);
        harness.Clock.Advance(Step);
        await ClientTestSupport.WaitUntilAsync(() => lease.State?.IdleSecondsRemaining == left, $"the heartbeat with {left} s left");
    }

    private static WebRoofConsole Page(WebRoofHarness harness)
        => new(harness.Accessor, new WebCircuitMonitor(), WebTestSupport.StopPass(time: harness.Clock), harness.Clock, harness.LoggerFactory.CreateLogger<WebRoofConsole>());
}
