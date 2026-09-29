using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// One page's roof console against the controller's API: the live status and what it offers, the operator lease a motion
/// started here holds (and every way it lets go), Stop winning over an Open that crossed it, and a stale status offering
/// no motion.
/// </summary>
[TestClass]
public sealed class WebRoofConsoleTests
{
    /// <summary>How often the page renews the lease of <see cref="WebRoofHarness.Opening"/>.</summary>
    private static readonly TimeSpan Renewal = TimeSpan.FromSeconds(2);

    private const string LeaseHeld =
        "Open accepted. This page renews the operator lease while the roof moves; if the page closes or loses its connection, the roof stops when the lease runs out. Stop roof stops it now.";

    // ---- The live status ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Started_ShowsTheLiveStatus_AndOffersWhatTheRoofIsNotAlreadyDoing()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var changes = 0;
        harness.Console.Changed += () => Interlocked.Increment(ref changes);
        harness.Console.View.Should().BeSameAs(WebRoofView.NotStarted);

        await harness.StartLiveAsync();
        await harness.Console.StartAsync();

        var view = harness.Console.View;
        view.IsStarted.Should().BeTrue();
        view.HasSession.Should().BeTrue();
        view.CanOperate.Should().BeTrue();
        view.Status!.Status.Should().Be(RoofControllerStatus.Closed);
        view.FeedState.Should().Be(RoofStatusFeedState.Connected);
        view.IsStale.Should().BeFalse();
        view.StaleSince.Should().BeNull();
        view.FeedBanner.Should().BeNull();
        view.FeedRefused.Should().BeFalse();
        view.OpenBlock.Should().BeNull();
        view.CloseBlock.Should().Be(RoofCommandRules.AlreadyClosed);
        view.ClearFaultBlock.Should().Be(RoofCommandRules.NoFault);
        view.HoldsLease.Should().BeFalse();
        view.Notices.Should().BeEmpty();
        changes.Should().BePositive();
    }

    [TestMethod]
    public async Task BeforeTheLiveConnectionStartsIt_NothingIsOffered_OrSent()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();

        await harness.Console.OpenAsync();
        await harness.Console.ClearFaultAsync();

        var view = harness.Console.View;
        view.IsStarted.Should().BeFalse();
        view.OpenBlock.Should().Be(WebRoofConsole.NotStarted);
        view.CloseBlock.Should().Be(WebRoofConsole.NotStarted);
        view.ClearFaultBlock.Should().Be(WebRoofConsole.NotStarted);
        view.Notices.Select(notice => notice.Text).Should().Equal("Not sent: the page is still connecting.", "Not sent: the page is still connecting.");
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
        WebRoofView.NotStarted.OpenBlock.Should().Be(WebRoofConsole.NotStarted, "a prerendered page offers nothing either");
    }

    [TestMethod]
    public async Task AQuietFeed_IsStale_OffersNoMotion_UntilTheNextStatus()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();

        harness.Clock.Advance(ClientTestSupport.FastFeed.StaleAfter);

        await harness.WaitForAsync(view => view.FeedLabel == "STALE", "the stale status");
        var stale = harness.Console.View;
        var since = WebRoofHarness.Start + ClientTestSupport.FastFeed.StaleAfter;
        stale.StaleSince.Should().Be(since);
        stale.IsStale.Should().BeTrue();
        stale.Status!.Status.Should().Be(RoofControllerStatus.Closed, "the last known state is still shown");
        stale.FeedBanner.Should().Be($"STALE: no status since {RoofStatusText.Time(since)}. Showing the last known state; Stop still works.");
        stale.OpenBlock.Should().Be(RoofCommandRules.Stale);

        await harness.Console.OpenAsync();

        harness.Console.View.Notices.Should().ContainSingle()
            .Which.Should().Be(new WebRoofNotice($"Not sent: {RoofCommandRules.Stale}.", WebRoofNoticeLevel.Warning, since));
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);

        harness.Push(RoofServiceMock.Snapshot());

        await harness.WaitForAsync(view => view.FeedLabel == "live", "the live status again");
        harness.Console.View.StaleSince.Should().BeNull();
        harness.Console.View.FeedBanner.Should().BeNull();
        harness.Console.View.OpenBlock.Should().BeNull();
    }

    [TestMethod]
    public async Task Refresh_ReadsTheStatus_AndSaysWhen()
    {
        using var harness = await WebRoofHarness.CreateAsync(RoofControllerApiContract.ViewerRole);
        await harness.StartLiveAsync();
        harness.Report(RoofServiceMock.Snapshot(RoofControllerStatus.Open));

        await harness.Console.RefreshAsync();

        var view = harness.Console.View;
        view.Status!.Status.Should().Be(RoofControllerStatus.Open);
        view.FeedLabel.Should().Be("live");
        view.Notices.Should().ContainSingle().Which.Text.Should().Be($"Read the status at {RoofStatusText.Time(harness.Current.SnapshotUtc)}.");
    }

    // ---- Who may do what ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AViewer_IsOfferedNoCommand_AndNothingIsSent()
    {
        using var harness = await WebRoofHarness.CreateAsync(RoofControllerApiContract.ViewerRole);
        await harness.StartLiveAsync();

        var view = harness.Console.View;
        view.CanOperate.Should().BeFalse();
        view.OpenBlock.Should().Be(RoofCommandRules.MotionRoleNeeded);
        view.CloseBlock.Should().Be(RoofCommandRules.MotionRoleNeeded);

        await harness.Console.OpenAsync();
        await harness.Console.CloseAsync();

        harness.Console.View.Notices.Select(notice => (notice.Text, notice.Level)).Should().Equal(
            ("Not sent: the Operator role is needed to open or close the roof.", WebRoofNoticeLevel.Warning),
            ("Not sent: the Operator role is needed to open or close the roof.", WebRoofNoticeLevel.Warning));
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
        harness.Calls(nameof(IRoofControllerServiceV4.Close)).Should().Be(0);
    }

    [TestMethod]
    public async Task ASignedOutPage_ShowsNoStatus_AndSendsNothing()
    {
        using var harness = await WebRoofHarness.CreateAsync(role: null);

        await harness.Console.StartAsync();

        var view = harness.Console.View;
        view.IsStarted.Should().BeTrue();
        view.HasSession.Should().BeFalse();
        view.CanOperate.Should().BeFalse();
        view.Status.Should().BeNull();
        view.FeedLabel.Should().Be("not connected");
        view.FeedBanner.Should().BeNull();
        view.StopAfterSessionEnds.Should().Be(WebStopTexts.MayBeRefused, "without the web UI's Stop key, a signed-out page's Stop has no credential");
        view.OpenBlock.Should().Be(WebRoofConsole.NoSession);
        view.CloseBlock.Should().Be(WebRoofConsole.NoSession);
        view.ClearFaultBlock.Should().Be(WebRoofConsole.NoSession);

        await harness.Console.OpenAsync();
        await harness.Console.RefreshAsync();

        harness.Console.View.Notices.Should().ContainSingle().Which.Text.Should().Be("Not sent: you are signed out.");
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
    }

    [TestMethod]
    public async Task Notices_KeepTheNewest()
    {
        using var harness = await WebRoofHarness.CreateAsync(role: null);
        await harness.Console.StartAsync();

        for (var i = 0; i < WebRoofConsole.NoticeLimit + 2; i++)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await harness.Console.OpenAsync();
        }

        harness.Console.View.Notices.Should().HaveCount(WebRoofConsole.NoticeLimit);
        harness.Console.View.Notices[0].At.Should().Be(WebRoofHarness.Start.AddSeconds(WebRoofConsole.NoticeLimit + 2), "the newest comes first");
    }

    // ---- The operator lease ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_HoldsTheLease_RenewsItWhileTheRoofMoves_AndLetsGoWhenItStops()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.HoldsLease.Should().BeTrue();
        view.Busy.Should().BeNull();
        view.Status!.Status.Should().Be(RoofControllerStatus.Opening);
        view.OpenBlock.Should().Be(RoofCommandRules.Moving);
        view.CloseBlock.Should().Be(RoofCommandRules.Moving);
        view.Notices.Should().ContainSingle().Which.Should().Be(new WebRoofNotice(LeaseHeld, WebRoofNoticeLevel.Info, WebRoofHarness.Start));

        harness.Clock.Advance(Renewal - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);
        Renewals(harness).Should().Be(0, "the renewal is not due yet");

        await RenewedAsync(harness, 1);
        await RenewedAsync(harness, 2);
        harness.Console.View.HoldsLease.Should().BeTrue();

        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Open));

        await harness.WaitForAsync(view => !view.HoldsLease, "the lease to be let go");
        await NoRenewalAsync(harness, 2);
    }

    [TestMethod]
    public async Task Stop_FromAnyOfThePersonsPages_LetsGoOfTheLease()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();
        harness.Console.View.HoldsLease.Should().BeTrue();

        var stop = await harness.Session!.StopAsync();

        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        harness.Console.View.HoldsLease.Should().BeFalse();
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
        await NoRenewalAsync(harness, 0);
    }

    [TestMethod]
    public async Task ALostConnection_LetsGoOfTheLease_AndThePageSaysSoWhenItIsBack()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();

        harness.Circuit.SetConnected(false);

        harness.Console.View.HoldsLease.Should().BeFalse();
        await NoRenewalAsync(harness, 0);
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("lost while it held the operator lease"));

        harness.Circuit.SetConnected(true);

        harness.Console.View.Notices[0].Should().Be(
            new WebRoofNotice(WebRoofConsole.LeaseDroppedOnDisconnect, WebRoofNoticeLevel.Warning, WebRoofHarness.Start.AddSeconds(10)));
        harness.Console.View.HoldsLease.Should().BeFalse("the lease is not taken back: the roof may have stopped");
    }

    [TestMethod]
    public async Task AMotionAcceptedWhileTheConnectionIsDown_IsNotKeptMoving()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        harness.Circuit.SetConnected(false);

        await harness.Console.OpenAsync();

        harness.Console.View.HoldsLease.Should().BeFalse();
        harness.Console.View.Notices[0].Text.Should().Be("Open accepted. Stop roof stops it.");
        await NoRenewalAsync(harness, 0);

        harness.Circuit.SetConnected(true);

        harness.Console.View.Notices[0].Text.Should().Be(WebRoofConsole.LeaseDroppedOnDisconnect);
    }

    [TestMethod]
    [DataRow(false, WebStopTexts.MayBeRefused)]
    [DataRow(true, WebStopTexts.StillWorks)]
    public async Task ASessionTheControllerRefuses_SaysSo_AndWhatStopDoesNow(bool withStopKey, string stop)
    {
        using var harness = await WebRoofHarness.CreateAsync(stopKey: withStopKey ? WebTestSupport.StopKey() : null);
        await harness.Session!.Client.Auth.SignOutAsync();

        await harness.Console.StartAsync();

        await harness.WaitForAsync(view => view.FeedRefused, "the controller's refusal");
        harness.Console.View.FeedBanner.Should().Be($"No status: the controller refused your session. Sign in again. {stop}");
        harness.Console.View.StopAfterSessionEnds.Should().Be(stop);
    }

    [TestMethod]
    public async Task TheSessionEnding_LetsGoOfTheLease_AndSaysSo()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();

        harness.Session!.End();

        await harness.WaitForAsync(view => !view.HoldsLease, "the lease to be let go");
        harness.Console.View.Notices[0].Should().Be(new WebRoofNotice(
            "Your session ended, so this page stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.",
            WebRoofNoticeLevel.Warning,
            WebRoofHarness.Start));
        await NoRenewalAsync(harness, 0);
    }

    [TestMethod]
    public async Task ARenewalRefused_AsNoLease_LetsGo_WithoutAWarning()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();
        harness.Roof.Setup(service => service.RenewLease())
            .Returns(Result<RoofStatusResponse>.Failure(new RoofControllerException(RoofControllerErrorCode.LeaseNotActive, "No motion lease is active.")));

        harness.Clock.Advance(Renewal);

        await harness.WaitForAsync(view => !view.HoldsLease, "the lease to be let go");
        Renewals(harness).Should().Be(1);
        harness.Console.View.Notices.Should().ContainSingle("another page's Stop, or the roof stopping, ends a lease: nothing went wrong");
        await NoRenewalAsync(harness, 1);
    }

    [TestMethod]
    public async Task ARenewalThatFails_IsTriedAgainSoon_AndThePageWarns()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();
        harness.Roof.Setup(service => service.RenewLease()).Returns(Result<RoofStatusResponse>.Failure(new InvalidOperationException("The bus is busy.")));

        harness.Clock.Advance(Renewal);

        await harness.WaitForAsync(view => view.Notices.Count == 2, "the warning");
        var warning = harness.Console.View.Notices[0];
        warning.Level.Should().Be(WebRoofNoticeLevel.Danger);
        warning.Text.Should().StartWith("The lease could not be renewed: ").And.EndWith(" If the controller is running, it stops the roof when the lease runs out.");
        harness.Console.View.HoldsLease.Should().BeTrue();

        harness.Clock.Advance(WebRoofConsole.LeaseRetryDelay);

        await ClientTestSupport.WaitUntilAsync(() => Renewals(harness) == 2, "the second try");
    }

    [TestMethod]
    public async Task ClosingThePage_LetsGoOfTheLease_AndItChangesNoMore()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();
        var changes = 0;
        harness.Console.Changed += () => Interlocked.Increment(ref changes);

        harness.Console.Dispose();
        harness.Console.Dispose();

        await NoRenewalAsync(harness, 0);
        harness.Push(RoofServiceMock.Snapshot());
        await harness.Console.OpenAsync();
        await harness.Console.StartAsync();
        await Task.Delay(100);
        changes.Should().Be(0);
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("closed while it held the operator lease"));
    }

    // ---- Refusals, and Stop winning ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task ARefusedOpen_SaysWhy_AndShowsTheStatusThatCameWithIt()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var latched = RoofServiceMock.Snapshot(faultLatched: true) with { StatusVersion = 50 };
        harness.Roof.Setup(service => service.Open())
            .Returns(Result<RoofControllerStatus>.Failure(new RoofControllerException(RoofControllerErrorCode.FaultLatched, "Relay 2 read back high.", latched)));
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        var notice = view.Notices.Should().ContainSingle().Subject;
        notice.Level.Should().Be(WebRoofNoticeLevel.Danger);
        notice.Text.Should().StartWith("A fault is latched.").And.EndWith(" Relay 2 read back high.");
        view.Status!.IsFaultLatched.Should().BeTrue();
        view.OpenBlock.Should().Be(RoofCommandRules.FaultLatched);
        view.ClearFaultBlock.Should().BeNull();
        view.HoldsLease.Should().BeFalse();
        view.Busy.Should().BeNull();
    }

    [TestMethod]
    public async Task AnOpenAcceptedAfterStopWasSent_IsStoppedAgain()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        harness.OpensTheRoof(whileOpening: () =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        await harness.StartLiveAsync();

        var open = harness.Console.OpenAsync();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        harness.Console.View.Busy.Should().Be("Sending Open…");
        harness.Console.View.OpenBlock.Should().Be(RoofCommandRules.CommandInFlight);
        await harness.Session!.StopAsync();
        release.Release();
        await open;

        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(2);
        var view = harness.Console.View;
        view.HoldsLease.Should().BeFalse();
        view.Notices.Select(notice => notice.Level).Should().Equal(WebRoofNoticeLevel.Info, WebRoofNoticeLevel.Danger);
        view.Notices[1].Text.Should().Be("Open was accepted after Stop was sent, so Stop is sent again.");
        await NoRenewalAsync(harness, 0);
    }

    [TestMethod]
    public async Task AnUnansweredOpen_SaysTheRoofMayBeMoving_AndIsStoppedAgainIfAStopCrossedIt()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var stopCrosses = false;
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        harness.OpensTheRoof(whileOpening: () =>
        {
            if (stopCrosses)
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        harness.CutAnswer = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Open", StringComparison.OrdinalIgnoreCase);
        var unanswered = RoofCommandRules.DescribeUnanswered(
            new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), "Open", $"press {RoofStopText.ButtonLabel}");
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();

        harness.Console.View.Notices.Should().ContainSingle().Which.Should().Be(new WebRoofNotice(unanswered, WebRoofNoticeLevel.Danger, WebRoofHarness.Start));
        harness.Console.View.HoldsLease.Should().BeFalse("there is no lease to renew without an answer");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(0, "no Stop crossed it: the notice says how to stop the roof");

        stopCrosses = true;
        var open = harness.Console.OpenAsync();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await harness.Session!.StopAsync();
        release.Release();
        await open;

        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(2);
        harness.Console.View.Notices[1].Text.Should().Be(unanswered);
        harness.Console.View.Notices[0].Level.Should().Be(WebRoofNoticeLevel.Info, "Stop was acknowledged");
    }

    private static int Renewals(WebRoofHarness harness) => harness.Calls(nameof(IRoofControllerServiceV4.RenewLease));

    // Moves to the next renewal, then waits until the page has its answer (so the one after is scheduled).
    private static async Task RenewedAsync(WebRoofHarness harness, int renewals)
    {
        var before = harness.Console.View.Status!.StatusVersion;
        harness.Clock.Advance(Renewal);
        await ClientTestSupport.WaitUntilAsync(() => Renewals(harness) == renewals, $"renewal {renewals}");
        await harness.WaitForAsync(view => view.Status!.StatusVersion > before, $"the answer to renewal {renewals}");
    }

    private static async Task NoRenewalAsync(WebRoofHarness harness, int renewals)
    {
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Renewals(harness).Should().Be(renewals, "the page no longer renews the lease");
    }
}
