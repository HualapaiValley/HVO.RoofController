using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Web.Components.Layout;
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
        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held, "the lease to be taken again");
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
    public async Task AnIdleLease_IsNotTakenAgainWithoutInput_ThePageSaysAlpacaMayNowMove_AndInputTakesItAgain()
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
        harness.Clock.Advance(Step);
        await lease.Stopping;

        lease.IdledOut.Should().BeTrue();
        lease.IsRenewing.Should().BeFalse("a heartbeat without input must not take an idle lease again");
        harness.Console.View.AlpacaIdleWarning.Should().Be(RoofAlpacaText.MayNowMove);
        var sent = Count(requests);
        harness.ServerClock.Advance(Step);
        harness.Clock.Advance(Step);
        await Task.Delay(100);
        Count(requests).Should().Be(sent);
        harness.Alpaca().GetStatus().OperatorLease.Held.Should().BeFalse("Alpaca may now move the roof");

        harness.Console.NoteInput();
        harness.Clock.Advance(TimeSpan.Zero);

        await ClientTestSupport.WaitUntilAsync(() => harness.Alpaca().GetStatus().OperatorLease.Held, "the person's input to take it again");
        requests[^1].Should().Be("POST input");
        lease.IdledOut.Should().BeFalse();
        await harness.WaitForAsync(view => view.AlpacaIdleWarning != RoofAlpacaText.MayNowMove, "the page to stop saying so");
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

        harness.Console.View.AlpacaIdleWarning.Should().BeNull("70 s are left");
        banner.FindAll("[data-testid=alpaca-idle-banner]").Should().BeEmpty();

        await StepAsync(harness, lease, 60);

        var warning = "Your operator lease lapses in 60 s without input; then Alpaca may move the roof. Touch anything to keep it.";
        harness.Console.View.AlpacaIdleWarning.Should().Be(warning);
        await banner.WaitForAssertionAsync(() => Text(banner.Find("[data-testid=alpaca-idle-banner]")).Should().Be(warning));

        harness.Console.NoteInput();

        await ClientTestSupport.WaitUntilAsync(() => lease.State?.IdleSecondsRemaining == 120, "the input to go at once");
        requests[^1].Should().Be("POST input");
        await banner.WaitForAssertionAsync(() => banner.FindAll("[data-testid=alpaca-idle-banner]").Should().BeEmpty());
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
