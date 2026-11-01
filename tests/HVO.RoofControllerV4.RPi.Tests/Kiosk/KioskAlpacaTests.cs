using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's and the Mac app's Alpaca page and operator lease (#220, phase 3), against the controller's real API in
/// process: the Alpaca nav item and page (the commands setting, the Stop hold with Allow Alpaca again, the operator lease,
/// grants with Revoke, alerts newest first with Acknowledge and Allow this Close once, the warnings, also in the summary
/// banner); the grant sent with exactly the alert's reasons after the question, and nothing on Cancel; Allow this Open once
/// (#220, phase 4, with Alpaca Open and Close on), disabled with the reason in Block mode; and the operator
/// lease the screens hold while an operator is signed in, whose heartbeats say "input" only after the person touched the
/// screen, which stops when it lapses for want of input until the next touch, and which is given up when they lock.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KioskAlpacaTests
{
    private const string Pager = "alpaca-pages";

    private static readonly Dictionary<string, string?> CloseOnly = new() { ["RoofController:Alpaca:AllowCommands"] = "CloseOnly" };

    public TestContext TestContext { get; set; } = null!;

    // ---- The operator lease -------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TheOperatorLease_IsHeldOnlyWhileAnOperatorIsSignedIn_AndGivenUpWhenTheyLock(bool mac)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly, options: mac ? new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false } : null);
        await harness.StartLiveAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaLease).Should().Be(0, "nobody is signed in");
        harness.Console.View.KeepsOperatorLease.Should().BeFalse();

        if (mac)
        {
            await harness.PasswordAsync(KioskHarness.Operator);
        }
        else
        {
            await harness.UnlockAsync(KioskHarness.Operator);
        }

        harness.Console.View.KeepsOperatorLease.Should().BeTrue();
        // The keeper's first heartbeat goes at once, and takes the lease.
        harness.Clock.Advance(TimeSpan.Zero);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator }, "the operator lease taken");
        Alpaca(harness).GetStatus().OperatorLease.Holder.Should().Be(KioskHarness.Operator);

        await harness.Console.LockAsync();

        harness.Sent(HttpMethod.Delete, RoofApiRoutes.AlpacaLease).Should().Be(1, "locking gives the lease up, while the session still works");
        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse();
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: false } && !view.KeepsOperatorLease, "the lease given up");
        var sent = harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaLease);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaLease).Should().Be(sent, "no heartbeat goes once the screens are locked");
    }

    [TestMethod]
    public async Task AViewer_HoldsNoOperatorLease()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly, options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await RoofClientApiTests.AddUserAsync(harness.Host, "vera", RoofControllerApiContract.ViewerRole);
        await harness.StartLiveAsync();
        await harness.PasswordAsync("vera");

        harness.Clock.Advance(TimeSpan.Zero);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaLease).Should().Be(0, "only an operator holds it");
        harness.Console.View.KeepsOperatorLease.Should().BeFalse();
        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheHeartbeats_SayInput_OnlyAfterATouch_NeverForAStatusOrATimer()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        var inputs = new ConcurrentQueue<bool>();
        harness.AlterRequest = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/" + RoofApiRoutes.AlpacaLease, StringComparison.Ordinal))
            {
                inputs.Enqueue(request.RequestUri.Query.Contains("input=true", StringComparison.Ordinal));
            }
        };
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);

        harness.Clock.Advance(TimeSpan.Zero);
        await AnsweredAsync(harness, 1);
        await HeartbeatAsync(harness, 2);
        var version = harness.Current.StatusVersion;
        harness.Push(KioskHarness.Snapshot(RoofControllerStatus.Closed));
        await harness.WaitForAsync(view => view.Status?.StatusVersion > version, "the status update");
        await HeartbeatAsync(harness, 3);
        await screen.TouchAsync(screen.Find("title")!);
        await HeartbeatAsync(harness, 4);
        await HeartbeatAsync(harness, 5);

        inputs.Should().Equal([true, false, false, true, false],
            "taking the lease, a timer's heartbeat, one after a status update, one after a touch on the screen, and one more after nothing");
    }

    [TestMethod]
    public async Task AnIdleLease_IsWarnedOf_ThenLapses_AndStaysLapsedUntilTheNextTouch()
    {
        var settings = new Dictionary<string, string?>(CloseOnly)
        {
            ["RoofController:Lease:OperatorLeaseIdleMinutes"] = "2",
            ["RoofControllerUi:KioskScreenTimeout"] = "00:00:30"
        };
        await using var harness = await KioskHarness.CreateAsync(settings: settings, options: new KioskConsoleOptions { IdleLock = TimeSpan.FromMinutes(10) });
        var inputs = new ConcurrentQueue<bool>();
        harness.AlterRequest = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/" + RoofApiRoutes.AlpacaLease, StringComparison.Ordinal))
            {
                inputs.Enqueue(request.RequestUri.Query.Contains("input=true", StringComparison.Ordinal));
            }
        };
        await harness.StartLiveAsync();
        await ClientTestSupport.WaitUntilAsync(() => harness.Answered(HttpMethod.Get, RoofApiRoutes.Settings) >= 1, "the screen timeout read");
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(TimeSpan.Zero);
        await AnsweredAsync(harness, 1);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator }, "the operator lease taken");

        // The controller's clock and the kiosk's move together, a heartbeat every 10 s, none of them input.
        for (var heartbeat = 2; heartbeat <= 12; heartbeat++)
        {
            await TogetherAsync(harness, TimeSpan.FromSeconds(10));
            await AnsweredAsync(harness, heartbeat);
            if (heartbeat == 6)
            {
                harness.Console.View.AlpacaIdleWarning.Should().BeNull("70 s are left: the warning is for the last minute");
                harness.Console.View.IsBlank.Should().BeTrue("the screen blanked 30 s after the last touch");
            }

            if (heartbeat == 8)
            {
                harness.Console.View.AlpacaIdleWarning.Should().NotBeNull();
                harness.Console.View.IsBlank.Should().BeFalse("the screen came on with the idle warning");
            }
        }

        await harness.WaitForAsync(view => view.AlpacaIdleWarning?.StartsWith("Your operator lease lapses in", StringComparison.Ordinal) == true, "the idle warning");
        harness.Console.View.AlpacaIdleWarning.Should().EndWith("then Alpaca may move the roof. Touch anything to keep it.");
        harness.Console.View.OffersAlpaca.Should().BeTrue();

        // The lease lapses at the controller; the heartbeats go on, and none takes it back.
        await TogetherAsync(harness, TimeSpan.FromSeconds(10));
        await AnsweredAsync(harness, 13);
        await harness.WaitForAsync(view => view.AlpacaIdleWarning == RoofAlpacaText.MayNowMove, "the lapse");
        Alpaca(harness).GetStatus().OperatorLease.Should().Be(new RoofOperatorLeaseState { Held = false, IdleLapsedHolder = KioskHarness.Operator });
        for (var heartbeat = 14; heartbeat <= 16; heartbeat++)
        {
            await TogetherAsync(harness, TimeSpan.FromSeconds(10));
            await AnsweredAsync(harness, heartbeat);
        }

        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse("a heartbeat alone never takes it back");
        harness.Console.View.AlpacaIdleWarning.Should().Be(RoofAlpacaText.MayNowMove);
        harness.Console.View.KeepsOperatorLease.Should().BeTrue("the heartbeats go on");

        // A touch takes it back as it ends: no waiting for the next heartbeat.
        harness.Console.Touch();
        harness.Console.TouchEnded();
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator } && view.AlpacaIdleWarning is null, "the lease taken back");
        inputs.Should().HaveCount(17);
        inputs.First().Should().BeTrue();
        inputs.Last().Should().BeTrue("the touch took it back");
        inputs.Skip(1).SkipLast(1).Should().OnlyContain(input => !input, "a heartbeat alone is never input");
    }

    [TestMethod]
    public async Task AGrant_StepsThePersonAway_TheHeartbeatsGoOnWithoutInput_AndALaterTouchTakesControlBack()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        var inputs = new ConcurrentQueue<bool>();
        harness.AlterRequest = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/" + RoofApiRoutes.AlpacaLease, StringComparison.Ordinal))
            {
                inputs.Enqueue(request.RequestUri.Query.Contains("input=true", StringComparison.Ordinal));
            }
        };
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(TimeSpan.Zero);
        await AnsweredAsync(harness, 1);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator }, "the operator lease taken");
        Refuse(harness, "scope-not-clear:Main");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));

        // The taps that ask for the grant and answer the question are not input after it.
        await screen.TouchAsync(await FindAsync(screen, "alpaca-grant-close-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");
        await screen.TouchAsync(screen.Find("alpaca-answer-0")!);
        await UntilAsync(() => harness.Answered(HttpMethod.Post, RoofApiRoutes.AlpacaGrants) == 1 && screen.Shell.Alpaca.Busy is null, "the grant");

        Alpaca(harness).Grants().Should().ContainSingle();
        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse("the person stepped away with the grant");
        await harness.WaitForAsync(view => view.AlpacaSteppedAway is not null && view.Status?.Alpaca?.Grants.Count == 1, "the step away");
        var away = RoofAlpacaText.DescribeSteppedAway(harness.Console.View.Status!.Alpaca!.Grants[0]);
        away.Should().Be("You stepped away from the operator lease: Alpaca may close the roof once in the next 5 min. Touch anything to take control back; that cancels the grant.");
        await OnUiAsync(() =>
        {
            screen.Shell.Update(harness.Console.View);
            screen.Text("safety-summary").Should().Be(away, "it is said on every page");
        });
        await screen.RenderAsync("alpaca-stepped-away", TestContext);

        // The heartbeats go on, without input: the lease stays released, and the grant stays.
        for (var heartbeat = 2; heartbeat <= 4; heartbeat++)
        {
            await HeartbeatAsync(harness, heartbeat);
        }

        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse();
        Alpaca(harness).Grants().Should().ContainSingle();
        harness.Console.View.AlpacaSteppedAway.Should().NotBeNull();

        // A later touch takes control back at once, and cancels the grant.
        await screen.TouchAsync(screen.Find("title")!);
        await harness.WaitForAsync(view => view.Status?.Alpaca is { OperatorLease: { Held: true, Holder: KioskHarness.Operator }, Grants.Count: 0 }, "control taken back");
        harness.Console.View.AlpacaSteppedAway.Should().BeNull();
        Alpaca(harness).Grants().Should().BeEmpty("taking the lease back drops the grant");
        inputs.Should().Equal([true, false, false, false, true], "the first heartbeat, three without input after the grant, and the touch");
    }

    // ---- The page -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task WithAlpacaCommandsOff_NoLeaseIsHeld_AndWithNothingToShow_ThePageIsNotOffered()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        harness.Clock.Advance(TimeSpan.Zero);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(20));

        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaLease).Should().Be(0, "with Alpaca commands off, a lease holds nothing back");
        harness.Console.View.KeepsOperatorLease.Should().BeFalse();
        await OnUiAsync(() =>
        {
            harness.Console.View.Status!.Alpaca!.Mode.Should().Be(RoofAlpacaCommandMode.Off);
            harness.Console.View.OffersAlpaca.Should().BeFalse();
            screen.Visible("nav-alpaca").Should().BeFalse();
            ((KioskNavPanel)screen.Find("nav")!).IsCompact.Should().BeFalse("the bar keeps its comfortable widths while they fit");
            screen.Shell.ShowPage(KioskPage.Alpaca);
            screen.Shell.Page.Should().Be(KioskPage.Roof);
        });
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task WithAlpacaOffered_TheBottomBarStaysOneRow_AndTheRoofPageFits(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));

        await screen.RenderAsync("alpaca-roof", TestContext);

        await OnUiAsync(() =>
        {
            var buttons = new[] { "nav-roof", "nav-readings", "nav-notices", "nav-alpaca", "nav-about", "nav-terminal", "nav-history", "nav-change-pin", "nav-lock" }
                .Select(name => screen.Find(name)!).ToList();
            buttons.Should().OnlyContain(button => button.IsEffectivelyVisible);
            buttons.Select(button => button.TranslatePoint(default, screen.Window)!.Value.Y).Distinct().Should().ContainSingle("the bar is one row");
            buttons.Should().OnlyContain(button => button.Bounds.Width >= screen.Metrics.Touch && button.Bounds.Height >= screen.Metrics.Touch);
            screen.Shell.RoofPage.Open.IsEffectivelyVisible.Should().BeTrue();
            screen.Shell.RoofPage.ClearFault.IsEffectivelyVisible.Should().BeTrue();
            screen.OffScreen(name => name is "open" or "close" or "clear-fault").Should().BeEmpty("the motion controls are whole on the page");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ThePage_ShowsTheStateToAnyone_AndAllowAlpacaAgainClearsTheHold_ForAnOperator(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);

        // Locked: the page reads, and offers nothing but Stop.
        await UntilAsync(() => screen.Visible("nav-alpaca"), "Alpaca offered");
        await screen.TouchAsync(screen.Find("nav-alpaca")!);
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Alpaca);
            screen.Text("safety-summary").Should().Be("Alpaca warning. Readings has details.", "the controller's Alpaca warning is in the summary");
        });
        var locked = await ReadEveryPageAsync(screen, "alpaca-locked");
        locked["alpaca-mode"].Should().Be(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.CloseOnly));
        locked["alpaca-needs"].Should().Be("Unlock the kiosk with a PIN to allow Alpaca again, grant, revoke or acknowledge.");
        locked["alpaca-warning-0"].Should().Be(RoofAlpacaCommands.NoBlockingScopeWarning);
        locked["alpaca-hold-text"].Should().Be("Alpaca commands are held after a Stop (the controller's start): a person must allow them again.");
        locked["alpaca-lease"].Should().Be(KioskAlpacaPage.NoLease);
        locked["alpaca-grants-none"].Should().Be(KioskAlpacaPage.NoGrants);
        locked["alpaca-alerts-none"].Should().Be(KioskAlpacaPage.NoAlerts);
        locked.Keys.Should().NotContain("alpaca-clear-hold", "only an operator signed in may allow Alpaca again");

        // An operator unlocks it: the kiosk takes the operator lease, and offers Allow Alpaca again.
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(TimeSpan.Zero);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true }, "the operator lease taken");
        await UntilAsync(() => screen.Shell.Page == KioskPage.Roof && screen.Visible("nav-alpaca"), "the roof page after unlocking");
        await screen.TouchAsync(screen.Find("nav-alpaca")!);
        var unlocked = await ReadEveryPageAsync(screen, "alpaca-operator");
        unlocked.Keys.Should().NotContain("alpaca-needs");
        unlocked["alpaca-clear-hold"].Should().Be(RoofAlpacaText.ClearHoldLabel);
        unlocked["alpaca-lease"].Should().Be("Operator lease held by olga: Alpaca commands are refused; it lapses after 10 min without input.");
        unlocked["alpaca-lease-kept"].Should().Be(KioskWording.Kiosk.KeepsOperatorLease);

        await screen.TouchAsync(await FindAsync(screen, "alpaca-clear-hold"));

        await harness.WaitForAsync(view => view.Status?.Alpaca?.Hold is { Held: false }, "the hold cleared");
        harness.Sent(HttpMethod.Delete, RoofApiRoutes.AlpacaHold).Should().Be(1);
        Alpaca(harness).GetStatus().Hold.Held.Should().BeFalse();
        await UntilAsync(() => screen.Shell.Alpaca.Busy is null, "the answer");
        var cleared = await ReadEveryPageAsync(screen, "alpaca-cleared");
        cleared["alpaca-hold-text"].Should().Be(KioskAlpacaPage.NoHold);
        cleared["alpaca-message"].Should().StartWith("The Stop hold is cleared");
        cleared.Keys.Should().NotContain("alpaca-clear-hold");
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AllowThisCloseOnce_AsksFirst_SendsExactlyTheAlertsReasons_AndCancelSendsNothing(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        Refuse(harness, "scope-not-clear:Main", "scope-unknown:Guide");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        var alert = harness.Console.View.Status!.Alpaca!.Alerts[0];
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));
        var shown = await ReadEveryPageAsync(screen, "alpaca-alert");
        shown["alpaca-alert-0-text"].Should().Be(RoofAlpacaText.DescribeAlert(alert));
        shown["alpaca-grant-close-0"].Should().Be(RoofAlpacaText.GrantCloseLabel);
        shown["alpaca-acknowledge-0"].Should().Be(RoofAlpacaText.AcknowledgeLabel);

        // Asked first: what it lifts, for whom, for how long; nothing is sent.
        await screen.TouchAsync(await FindAsync(screen, "alpaca-grant-close-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");
        var asked = await ReadEveryPageAsync(screen, "alpaca-grant-question");
        asked["alpaca-question-title"].Should().Be(KioskAlpacaPanel.GrantQuestionTitle);
        Enumerable.Range(0, 3).Select(i => asked[$"alpaca-question-{i}"]).Should().Equal(RoofAlpacaText.DescribeGrantQuestion(alert, 5));
        await OnUiAsync(() =>
        {
            screen.Text("alpaca-answer-0").Should().Be(RoofAlpacaText.GrantCloseLabel);
            screen.Visible("alpaca-answer-0").Should().BeTrue("the answers stay on the screen, whatever page of the question is shown");
            screen.Visible("alpaca-question-cancel").Should().BeTrue();
            screen.Visible("stop").Should().BeTrue();
        });
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0, "nothing is sent before the person answers");

        await screen.TouchAsync(screen.Find("alpaca-question-cancel")!);
        await UntilAsync(() => screen.Shell.Alpaca.Question is null, "Cancel");
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0, "Cancel sends nothing");
        Alpaca(harness).Grants().Should().BeEmpty();

        // Asked again, and answered.
        await screen.TouchAsync(await FindAsync(screen, "alpaca-grant-close-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question again");
        await screen.TouchAsync(screen.Find("alpaca-answer-0")!);
        await UntilAsync(() => harness.Answered(HttpMethod.Post, RoofApiRoutes.AlpacaGrants) == 1 && screen.Shell.Alpaca.Busy is null, "the grant");

        var grant = Alpaca(harness).Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Close);
        grant.ReasonKeys.Should().Equal(alert.ReasonKeys, "exactly the reasons the person was shown");
        grant.Ui.Should().Be("kiosk", "granted with the person's PIN session");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Grants.Count == 1, "the grant in the status");
        var granted = await ReadEveryPageAsync(screen, "alpaca-granted");
        granted["alpaca-message"].Should().StartWith("Granted. One Close allowed by");
        granted["alpaca-revoke-0"].Should().Be(RoofAlpacaText.RevokeLabel);

        // Acknowledged: no longer offered to acknowledge, nor to grant from. The touch is the person's input after the
        // grant, so it takes control back, which cancels the grant.
        await screen.TouchAsync(await FindAsync(screen, "alpaca-acknowledge-0"));
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts[0].Acknowledged == true, "the acknowledgement");
        Alpaca(harness).Alerts()[0].AcknowledgedBy.Should().Contain(KioskHarness.Operator);
        await harness.WaitForAsync(view => view.Status?.Alpaca is { OperatorLease.Held: true, Grants.Count: 0 }, "control taken back");
        await UntilAsync(() => screen.Shell.Alpaca.Busy is null, "the answer");
        var acknowledged = await ReadEveryPageAsync(screen, "alpaca-acknowledged");
        acknowledged["alpaca-alert-0-text"].Should().Contain("Acknowledged by");
        acknowledged.Keys.Should().NotContain(["alpaca-acknowledge-0", "alpaca-grant-close-0", "alpaca-revoke-0"]);
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(1);
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AllowThisOpenOnce_AsksTheOpenQuestion_SendsAnOpenGrantWithExactlyTheAlertsReasons_AndStepsThePersonAway(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: AlpacaOpenUiSupport.OpenAndClose());
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(TimeSpan.Zero);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator }, "the operator lease taken");
        RefuseOpen(harness, "weather-unsafe:Sky", RoofAlpacaContract.NoBlockingScopesKey);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        var alert = harness.Console.View.Status!.Alpaca!.Alerts[0];
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));
        var shown = await ReadEveryPageAsync(screen, "alpaca-open-alert");
        shown["alpaca-mode"].Should().Be(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.OpenAndClose));
        shown["alpaca-grant-open-0"].Should().Be(RoofAlpacaText.GrantOpenLabel);
        shown.Keys.Should().NotContain(["alpaca-grant-close-0", "alpaca-alert-0-unavailable"]);
        var grantOpen = await FindAsync(screen, "alpaca-grant-open-0");
        await OnUiAsync(() => grantOpen.IsEnabled.Should().BeTrue());

        await screen.TouchAsync(grantOpen);
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");
        var asked = await ReadEveryPageAsync(screen, "alpaca-open-question");
        asked["alpaca-question-title"].Should().Be(RoofAlpacaText.GrantQuestionTitleFor(RoofAlpacaCommandKind.Open));
        Enumerable.Range(0, 4).Select(i => asked[$"alpaca-question-{i}"]).Should().Equal(RoofAlpacaText.DescribeGrantQuestion(alert, 5));
        await OnUiAsync(() => screen.Text("alpaca-answer-0").Should().Be(RoofAlpacaText.GrantOpenLabel));
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0, "nothing is sent before the person answers");

        await screen.TouchAsync(screen.Find("alpaca-answer-0")!);
        await UntilAsync(() => harness.Answered(HttpMethod.Post, RoofApiRoutes.AlpacaGrants) == 1 && screen.Shell.Alpaca.Busy is null, "the grant");

        var grant = Alpaca(harness).Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Open);
        grant.ReasonKeys.Should().Equal(alert.ReasonKeys, "exactly the reasons the person was shown");
        grant.Ui.Should().Be("kiosk", "granted with the person's PIN session");
        await OnUiAsync(() => screen.Shell.Alpaca.Message!.Text.Should().StartWith("Granted. One Open allowed by"));
        Alpaca(harness).GetStatus().OperatorLease.Held.Should().BeFalse("the person stepped away with the grant");
        await harness.WaitForAsync(view => view.AlpacaSteppedAway is not null && view.Status?.Alpaca?.Grants.Count == 1, "the step away");
        harness.Console.View.AlpacaSteppedAway.Should().Be(
            "You stepped away from the operator lease: Alpaca may open the roof once in the next 5 min. Touch anything to take control back; that cancels the grant.");

        // PR #232 rereviews F7 and F12: another person's grant is never promised here (it is dropped once anyone takes the
        // operator lease).
        Alpaca(harness).Grant(AlpacaCommandRig.Person("grace", sessionId: "grace-web"),
            new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"])).Grant.Should().NotBeNull();
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Grants.Count == 2, "the other person's grant in the status");
        harness.Console.View.AlpacaSteppedAway.Should().Be(
            "You stepped away from the operator lease: Alpaca may open the roof once in the next 5 min. Touch anything to take control back; that cancels the grant.",
            "the notice names only this person's own grant");
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task InBlockMode_AnOpensGrant_IsShownDisabledWithTheReason_AndSendsNothing_WhileACloseIsUnaffected(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: AlpacaOpenUiSupport.OpenAndClose(block: true));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        Refuse(harness, "scope-not-clear:Main");
        harness.ServerClock.Advance(TimeSpan.FromSeconds(1));
        RefuseOpen(harness, "weather-unknown:Sky");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 2, "the alerts");
        var refusedOpen = harness.Console.View.Status!.Alpaca!.Alerts.Single(alert => alert.Command == RoofAlpacaCommandKind.Open);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));

        var shown = await ReadEveryPageAsync(screen, "alpaca-open-block");

        shown["alpaca-alert-0-text"].Should().Be(RoofAlpacaText.DescribeAlert(refusedOpen), "newest first");
        shown["alpaca-grant-open-0"].Should().Be(RoofAlpacaText.GrantOpenLabel);
        shown["alpaca-alert-0-unavailable"].Should().Be(RoofAlpacaCommands.OpenGrantsBlocked);
        shown["alpaca-grant-close-1"].Should().Be(RoofAlpacaText.GrantCloseLabel);
        shown.Keys.Should().NotContain("alpaca-alert-1-unavailable", "Block never gates a Close grant");
        var grantClose = await FindAsync(screen, "alpaca-grant-close-1");
        await OnUiAsync(() => grantClose.IsEnabled.Should().BeTrue());
        var grantOpen = await FindAsync(screen, "alpaca-grant-open-0");
        await OnUiAsync(() => grantOpen.IsEnabled.Should().BeFalse("no grant opens the roof in Block mode"));

        // Touched anyway, or asked from a stale view: nothing is asked or sent, and why is said.
        await screen.TouchAsync(grantOpen);
        await OnUiAsync(() =>
        {
            screen.Shell.Alpaca.Question.Should().BeNull();
            screen.Shell.Alpaca.AskGrant(refusedOpen);
            screen.Shell.Alpaca.Question.Should().BeNull();
            screen.Shell.Alpaca.Message!.Text.Should().Be($"Not sent: {RoofAlpacaCommands.OpenGrantsBlocked}");
        });
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0);
        Alpaca(harness).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnOpenGrantTheControllerRefuses_IsSaidInItsWords()
    {
        // The interlock turns to Block after the kiosk's status, just before the grant: the controller refuses it (403,
        // BlockMode).
        await using var harness = await KioskHarness.CreateAsync(settings: AlpacaOpenUiSupport.OpenAndClose());
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        RefuseOpen(harness, "weather-unsafe:Sky");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        harness.AlterRequest = request => AlpacaOpenUiSupport.BlockBeforeTheGrant(harness.Host.Services, request);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));
        await screen.TouchAsync(await FindAsync(screen, "alpaca-grant-open-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");

        await screen.TouchAsync(screen.Find("alpaca-answer-0")!);

        await UntilAsync(() => harness.Answered(HttpMethod.Post, RoofApiRoutes.AlpacaGrants) == 1 && screen.Shell.Alpaca.Busy is null, "the refusal");
        await OnUiAsync(() =>
        {
            screen.Shell.Alpaca.Message!.Level.Should().Be(KioskNoticeLevel.Danger);
            screen.Shell.Alpaca.Message.Text.Should().Contain(RoofAlpacaCommands.OpenGrantsBlocked);
        });
        Alpaca(harness).Grants().Should().BeEmpty();
        harness.Console.View.AlpacaSteppedAway.Should().BeNull("nothing was granted");
    }

    [TestMethod]
    public async Task Revoke_RevokesAnotherPersonsGrant()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(TimeSpan.Zero);
        await harness.WaitForAsync(view => view.Status?.Alpaca?.OperatorLease is { Held: true, Holder: KioskHarness.Operator }, "the operator lease taken");
        using (var ada = ClientTestSupport.CreateClient(harness.Host))
        {
            ada.Credential = await ada.Auth.SignInAsync(KioskHarness.Admin, TestSecrets.Password);
            await ada.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]);
        }

        await harness.WaitForAsync(view => view.Status?.Alpaca?.Grants.Count == 1, "ada's grant");
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));

        await screen.TouchAsync(await FindAsync(screen, "alpaca-revoke-0"));

        await harness.WaitForAsync(view => view.Status?.Alpaca?.Grants.Count == 0, "the grant revoked");
        harness.Sent(HttpMethod.Delete, RoofApiRoutes.AlpacaGrant(RoofAlpacaCommandKind.Close)).Should().Be(1);
        await UntilAsync(() => screen.Shell.Alpaca.Busy is null, "the answer");
        (await ReadEveryPageAsync(screen, "alpaca-revoked"))["alpaca-message"].Should().Be("The Close grant is revoked.");
        harness.Console.View.AlpacaSteppedAway.Should().BeNull("olga granted nothing");
    }

    [TestMethod]
    public async Task TheQuestion_IsNeverAnsweredByTheNextPerson()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        Refuse(harness, "scope-unknown:Main");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));
        await screen.TouchAsync(await FindAsync(screen, "alpaca-grant-close-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");

        await harness.Console.LockAsync();

        await UntilAsync(() => screen.Shell.Alpaca.Question is null && screen.Shell.Page == KioskPage.Roof, "the question withdrawn");
        await harness.UnlockAsync(KioskHarness.Admin);
        await OnUiAsync(() => screen.Shell.Alpaca.Question.Should().BeNull());
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0);
    }

    [TestMethod]
    public async Task Escape_AnswersTheQuestionWithCancel_InTheApp()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly, options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        await harness.PasswordAsync(KioskHarness.Operator);
        Refuse(harness, "scope-not-clear:Main");
        await harness.WaitForAsync(view => view.Status?.Alpaca?.Alerts.Count == 1, "the alert");
        await using var screen = await KioskScreen.ShowAsync(harness, 960, 600, KioskMetrics.DeskPixelsPerMillimetre);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Alpaca));
        await screen.ClickAsync(await FindAsync(screen, "alpaca-grant-close-0"));
        await UntilAsync(() => screen.Shell.Alpaca.Question is not null, "the question");
        await OnUiAsync(() => screen.Find("alpaca-answer-0")!.Focus().Should().BeTrue());

        await screen.PressAsync(Avalonia.Input.PhysicalKey.Escape);

        await UntilAsync(() => screen.Shell.Alpaca.Question is null, "Esc to cancel the question");
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0);
    }

    [TestMethod]
    [DataRow(false, 1920, 1200, 9.0)]
    [DataRow(false, 1280, 720, 8.2)]
    [DataRow(false, 800, 480, 5.2)]
    [DataRow(true, 1280, 800, KioskMetrics.DeskPixelsPerMillimetre)]
    [DataRow(true, 960, 600, KioskMetrics.DeskPixelsPerMillimetre)]
    public async Task EverythingAtOnce_FitsInBoundedPages_WithTheIdleWarningInTheSummary(bool mac, int width, double height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: CloseOnly,
            options: mac ? new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false } : null);
        await harness.StartLiveAsync();
        if (mac)
        {
            await harness.PasswordAsync(KioskHarness.Operator);
        }
        else
        {
            await harness.UnlockAsync(KioskHarness.Operator);
        }

        await using var screen = await KioskScreen.ShowAsync(harness, width, (int)height, density);
        var alpaca = Everything();
        var idle = RoofAlpacaText.DescribeIdleWarning(alpaca.OperatorLease, KioskHarness.Operator)!;
        await OnUiAsync(() =>
        {
            var view = harness.Console.View with { Status = harness.Console.View.Status! with { Alpaca = alpaca }, AlpacaIdleWarning = idle };
            screen.Shell.Update(view);
            screen.Shell.ShowPage(KioskPage.Alpaca);
            screen.Text("safety-summary").Should().Be(idle, "the person's idle warning is the summary, on every page");
        });

        idle.Should().Be("Your operator lease lapses in 45 s without input; then Alpaca may move the roof. Touch anything to keep it.");
        var shown = await ReadEveryPageAsync(screen, mac ? "mac-alpaca" : "alpaca");
        shown["alpaca-warning-0"].Should().Be(RoofAlpacaCommands.NoBlockingScopeWarning);
        shown["alpaca-hold-text"].Should().Be(RoofAlpacaText.DescribeHold(alpaca.Hold));
        shown["alpaca-clear-hold"].Should().Be(RoofAlpacaText.ClearHoldLabel);
        shown["alpaca-lease"].Should().Be(RoofAlpacaText.DescribeLease(alpaca.OperatorLease));
        shown["alpaca-grant-0-text"].Should().Be(RoofAlpacaText.DescribeGrant(alpaca.Grants[0]));
        shown["alpaca-revoke-0"].Should().Be(RoofAlpacaText.RevokeLabel);
        shown["alpaca-mode"].Should().Be(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.OpenAndClose));
        // Newest first: the refused Open a grant can lift, the refused Close a grant can lift, the refused key, the
        // acknowledged hold refusal.
        shown["alpaca-alert-0-text"].Should().Be(RoofAlpacaText.DescribeAlert(alpaca.Alerts[3]));
        shown["alpaca-grant-open-0"].Should().Be(RoofAlpacaText.GrantOpenLabel);
        shown["alpaca-alert-1-text"].Should().Be(RoofAlpacaText.DescribeAlert(alpaca.Alerts[1]));
        shown["alpaca-grant-close-1"].Should().Be(RoofAlpacaText.GrantCloseLabel);
        shown["alpaca-alert-2-text"].Should().Be(RoofAlpacaText.DescribeAlert(alpaca.Alerts[2]));
        shown["alpaca-alert-3-text"].Should().Be($"{RoofAlpacaText.DescribeAlert(alpaca.Alerts[0])} Acknowledged by olga (PIN at kiosk).");
        shown.Keys.Should().Contain(["alpaca-acknowledge-0", "alpaca-acknowledge-1", "alpaca-acknowledge-2"]);
        shown.Keys.Should().NotContain(["alpaca-grant-close-0", "alpaca-grant-open-1", "alpaca-grant-close-2", "alpaca-grant-close-3", "alpaca-acknowledge-3"],
            "a grant is of the refused command's kind, a refused key cannot be granted, and an acknowledged alert is done with");
        shown.Keys.Should().NotContain(key => key.EndsWith("-unavailable", StringComparison.Ordinal), "an Open grant can be made");
        harness.Sent(HttpMethod.Post, RoofApiRoutes.AlpacaGrants).Should().Be(0);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static RoofAlpacaCommands Alpaca(KioskHarness harness) => harness.Host.Services.GetRequiredService<RoofAlpacaCommands>();

    // A refused Alpaca Close, as the controller raises it for a client the Alpaca server reported (a documentation address).
    private static void Refuse(KioskHarness harness, params string[] reasons)
        => Alpaca(harness).RaiseAlert("Refused", RoofAlpacaCommandKind.Close,
            $"An Alpaca Close was refused: {RoofAlpacaText.DescribeReasons(reasons)}.", reasons, new RoofAlpacaClient(7, 42, "192.0.2.50", "header"));

    // A refused Alpaca Open (#220, phase 4), as the controller raises it.
    private static void RefuseOpen(KioskHarness harness, params string[] reasons)
        => Alpaca(harness).RaiseAlert("Refused", RoofAlpacaCommandKind.Open,
            $"An Alpaca Open was refused: {RoofAlpacaText.DescribeReasons(reasons)}.", reasons, new RoofAlpacaClient(7, 43, "192.0.2.50", "header"));

    // Everything the page shows at once: the hold, the person's lease 45 s from lapsing, a grant, three alerts.
    private static RoofAlpacaStatus Everything()
    {
        var at = KioskHarness.Start;
        return new RoofAlpacaStatus
        {
            Mode = RoofAlpacaCommandMode.OpenAndClose,
            Hold = new RoofStopHoldState { Held = true, SetBy = "olga (PIN at kiosk)", SinceUtc = at },
            OperatorLease = new RoofOperatorLeaseState { Held = true, Holder = KioskHarness.Operator, IdleSecondsRemaining = 45 },
            Grants =
            [
                new RoofOverrideGrant
                {
                    Id = "a1b2c3", Kind = RoofAlpacaCommandKind.Close, ReasonKeys = ["scope-unknown:Guide"], GrantedBy = "ada (signed in)", Ui = "session",
                    GrantedUtc = at, SecondsRemaining = 240
                }
            ],
            Alerts =
            [
                new RoofAlpacaAlert
                {
                    Id = "1", Kind = "Refused", Command = RoofAlpacaCommandKind.Close, ReasonKeys = ["stop-hold"],
                    Message = "An Alpaca Close was refused: the human Stop hold is set (by the controller's start).",
                    FirstUtc = at, LastUtc = at, Acknowledged = true, AcknowledgedBy = "olga (PIN at kiosk)"
                },
                new RoofAlpacaAlert
                {
                    Id = "3", Kind = "Refused", Command = RoofAlpacaCommandKind.Close, ReasonKeys = ["scope-not-clear:Main", "scope-unknown:Guide"],
                    Message = "An Alpaca Close was refused: Telescope 'Main' is not parked; Telescope 'Guide' cannot be read.",
                    ClientId = 7, ClientAddress = "192.0.2.50", Count = 3, FirstUtc = at.AddMinutes(1), LastUtc = at.AddMinutes(3)
                },
                new RoofAlpacaAlert
                {
                    Id = "2", Kind = "NotAuthorised", Command = RoofAlpacaCommandKind.Close,
                    Message = "An Alpaca Close was refused as not authorised: the controller did not accept its key.",
                    ClientId = 12, ClientAddress = "192.0.2.51", FirstUtc = at.AddMinutes(2), LastUtc = at.AddMinutes(2)
                },
                new RoofAlpacaAlert
                {
                    Id = "4", Kind = "Refused", Command = RoofAlpacaCommandKind.Open, ReasonKeys = ["weather-unsafe:Sky"],
                    Message = "An Alpaca Open was refused: Weather monitor 'Sky' reports unsafe weather.",
                    ClientId = 7, ClientAddress = "192.0.2.50", FirstUtc = at.AddMinutes(4), LastUtc = at.AddMinutes(4)
                }
            ],
            Warnings = [RoofAlpacaCommands.NoBlockingScopeWarning],
            OverrideWindowMinutes = 5
        };
    }

    private static Task AnsweredAsync(KioskHarness harness, int heartbeats)
        => ClientTestSupport.WaitUntilAsync(() => harness.Answered(HttpMethod.Post, RoofApiRoutes.AlpacaLease) >= heartbeats, $"heartbeat {heartbeats}");

    // The kiosk's clock moves on to the next heartbeat, and its answer comes.
    private static async Task HeartbeatAsync(KioskHarness harness, int heartbeats)
    {
        await harness.AdvanceAsync(RoofAlpacaText.LeaseHeartbeatInterval);
        await AnsweredAsync(harness, heartbeats);
    }

    // The controller's clock and the kiosk's, a second at a time.
    private static async Task TogetherAsync(KioskHarness harness, TimeSpan by)
    {
        for (var moved = TimeSpan.Zero; moved < by; moved += TimeSpan.FromSeconds(1))
        {
            harness.ServerClock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }

    // The page's control called name, on whichever page of the document it is, shown and laid out.
    private static async Task<Control> FindAsync(KioskScreen screen, string name)
    {
        var pages = await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).PageCount);
        await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).Move(-pages));
        for (var page = 0; page < Math.Max(1, pages); page++)
        {
            var found = await OnUiAsync(() =>
            {
                screen.Window.UpdateLayout();
                return screen.Find(name) is { IsEffectivelyVisible: true } control ? control : null;
            });
            if (found is not null)
            {
                return found;
            }

            await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).Move(1));
        }

        Assert.Fail($"{name} is on no page of the Alpaca page.");
        return null!;
    }

    // Every page of the document in turn, each drawn and saved (render-p1, render-p2, …) and checked as a render is
    // (nothing cut off, overlapping or off the screen, Stop whole): what each named text, banner and button on it says.
    // It ends back on the first page.
    private async Task<IReadOnlyDictionary<string, string?>> ReadEveryPageAsync(KioskScreen screen, string render)
    {
        var shown = new Dictionary<string, string?>();
        await OnUiAsync(() => screen.Window.UpdateLayout());
        var pages = await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).PageCount);
        await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).Move(-pages));
        for (var page = 0; page < Math.Max(1, pages); page++)
        {
            await screen.RenderAsync($"{render}-p{page + 1}", TestContext);
            await OnUiAsync(() =>
            {
                screen.OffScreen(name => name.StartsWith("alpaca-", StringComparison.Ordinal) && name != Pager).Should().BeEmpty();
                foreach (var control in screen.Find("page")!.GetVisualDescendants().OfType<Control>()
                    .Where(control => control.IsEffectivelyVisible && control.Name is { } name && name.StartsWith("alpaca-", StringComparison.Ordinal)))
                {
                    shown[control.Name!] = control switch
                    {
                        TextBlock block => block.Text,
                        Border { Child: TextBlock block } => block.Text,
                        Button button => KioskTheme.GetText(button),
                        _ => null
                    };
                }

                ((KioskPager)screen.Find(Pager)!).Move(1);
            });
        }

        await OnUiAsync(() => ((KioskPager)screen.Find(Pager)!).Move(-pages));
        return shown;
    }
}
