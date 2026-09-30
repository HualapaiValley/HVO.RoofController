using System.Text.Json;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's console against the controller's API, with the kiosk's device key: locked, it shows the status and offers
/// only Stop; a PIN unlocks Open, Close and Clear fault for the person's role; it locks itself when idle and blanks the
/// screen; Stop is sent whatever the kiosk's state, and says whether it arrived.
/// </summary>
[TestClass]
public sealed class KioskConsoleTests
{
    /// <summary>How often the kiosk renews the lease of <see cref="KioskHarness.Opening"/>.</summary>
    private static readonly TimeSpan Renewal = TimeSpan.FromSeconds(2);

    private const string LeaseHeld =
        "Open accepted. The kiosk renews the operator lease while the roof moves; if it is locked or cannot reach the controller, the roof stops when the lease runs out. Stop roof stops it now.";

    // ---- Locked --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Started_ShowsTheLiveStatus_Locked_AndOffersOnlyStop()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Console.View.Should().BeSameAs(KioskView.NotStarted);

        await harness.StartLiveAsync();

        var view = harness.Console.View;
        view.IsStarted.Should().BeTrue();
        view.IsUnlocked.Should().BeFalse();
        view.UnlockedBy.Should().BeNull();
        view.Status!.Status.Should().Be(RoofControllerStatus.Closed);
        view.FeedLabel.Should().Be("live");
        view.FeedBanner.Should().BeNull();
        view.IsStale.Should().BeFalse();
        view.OpenBlock.Should().Be(KioskText.Locked);
        view.CloseBlock.Should().Be(KioskText.Locked);
        view.ClearFaultBlock.Should().Be(KioskText.Locked);
        view.StopMessage.Should().Be(RoofStopText.AlwaysAvailable);
        view.StopOutcome.Should().Be(RoofStopOutcome.None);
        view.IsBlank.Should().BeFalse();
        view.Notices.Should().BeEmpty();
        harness.Credential.PinSession.Should().BeNull();
    }

    [TestMethod]
    public async Task Locked_NoMotionIsSent_AndTheNoticeSaysWhy()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();
        await harness.Console.CloseAsync();
        await harness.Console.ClearFaultAsync();

        harness.Console.View.Notices.Select(notice => notice.Text).Should().AllBe($"Not sent: {KioskText.Locked}.");
        harness.Console.View.Notices.Should().HaveCount(3).And.OnlyContain(notice => notice.Level == KioskNoticeLevel.Warning);
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
        harness.Calls(nameof(IRoofControllerServiceV4.Close)).Should().Be(0);
        harness.Calls(nameof(IRoofControllerServiceV4.ClearFault)).Should().Be(0);
    }

    [TestMethod]
    public async Task BeforeItStarts_NothingIsOffered_OrSent()
    {
        await using var harness = await KioskHarness.CreateAsync();

        await harness.Console.OpenAsync();

        harness.Console.View.IsStarted.Should().BeFalse();
        harness.Console.View.OpenBlock.Should().Be(KioskText.Starting);
        harness.Console.View.Notices.Should().ContainSingle().Which.Text.Should().Be($"Not sent: {KioskText.Starting}.");
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
        KioskView.NotStarted.StopMessage.Should().Be(RoofStopText.AlwaysAvailable, "Stop is offered before anything else");
    }

    [TestMethod]
    public async Task Stop_WhileLocked_IsSent_AndSaysWhatTheControllerAnswered()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();

        await harness.Console.StopAsync();

        var view = harness.Console.View;
        view.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged);
        view.StopMessage.Should().Be(RoofStopText.AcknowledgedVerified);
        view.StopInFlight.Should().BeFalse();
        view.IsUnlocked.Should().BeFalse("Stop needs no PIN, and does not unlock the kiosk");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
    }

    // ---- Unlocking -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnOperator_UnlocksWithTheirPin_AndIsOfferedWhatTheRoofIsNotAlreadyDoing()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();

        (await harness.Console.UnlockAsync(KioskHarness.Operator, TestSecrets.Pin)).Should().BeNull();

        var view = harness.Console.View;
        view.UnlockedBy.Should().Be(KioskHarness.Operator);
        view.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        view.IsOperator.Should().BeTrue();
        view.IsAdmin.Should().BeFalse();
        view.OpenBlock.Should().BeNull();
        view.CloseBlock.Should().Be(RoofCommandRules.AlreadyClosed);
        view.ClearFaultBlock.Should().Be(RoofCommandRules.NoFault);
        view.Notices.Should().ContainSingle().Which.Should().Be(
            new KioskNotice("Unlocked by olga (Operator).", KioskNoticeLevel.Info, KioskHarness.Start));
        harness.Credential.PinSession!.Name.Should().Be(KioskHarness.Operator);
        (await harness.Client.Auth.GetCallerAsync()).Name.Should().Be(KioskHarness.Operator, "the kiosk's requests are now the person's");
    }

    [TestMethod]
    public async Task AWrongPin_IsRefused_AndTheKioskStaysLocked()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();

        var refusal = await harness.Console.UnlockAsync(KioskHarness.Operator, TestSecrets.OtherPin);

        refusal.Should().NotBeNullOrWhiteSpace();
        refusal.Should().NotContain(TestSecrets.OtherPin);
        harness.Console.View.IsUnlocked.Should().BeFalse();
        harness.Console.View.OpenBlock.Should().Be(KioskText.Locked);
        harness.Credential.PinSession.Should().BeNull();
        harness.Logs.Entries.Should().NotContain(entry => entry.Message.Contains(TestSecrets.OtherPin));
    }

    [TestMethod]
    public async Task UnlockingAgain_WhileUnlocked_IsRefused()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        (await harness.Console.UnlockAsync(KioskHarness.Admin, TestSecrets.Pin)).Should().Be("The kiosk is already unlocked.");

        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);
    }

    [TestMethod]
    public async Task Lock_EndsThePinSessionAtTheController_AndOffersOnlyStopAgain()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var session = harness.Credential.PinSession!;

        await harness.Console.LockAsync();

        var view = harness.Console.View;
        view.IsUnlocked.Should().BeFalse();
        view.OpenBlock.Should().Be(KioskText.Locked);
        view.Notices[0].Should().Be(new KioskNotice(KioskText.LockedNotice, KioskNoticeLevel.Info, KioskHarness.Start));
        harness.Credential.PinSession.Should().BeNull();
        (await harness.Client.Auth.GetCallerAsync()).IsKiosk.Should().BeTrue("the kiosk is back to its device key");
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        (await admin.Identity.GetSessionsAsync()).Should().NotContain(candidate => candidate.Id == session.SessionId, "locking ended the session");

        (await harness.Console.UnlockAsync(KioskHarness.Operator, TestSecrets.Pin)).Should().BeNull("the next person unlocks it");
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);
    }

    [TestMethod]
    public async Task TheControllerEndingThePinSession_LocksTheKiosk_AndSaysSo()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.EndSessionAsync(harness.Credential.PinSession!.SessionId);

        await harness.Console.OpenAsync();

        await harness.WaitForAsync(view => !view.IsUnlocked, "the kiosk to lock");
        harness.Console.View.Notices.Select(notice => notice.Text).Should().Contain(KioskText.SessionEnded);
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0, "the refused Open is reported, not repeated with the device key");
    }

    // ---- Idle lock and the screen --------------------------------------------------------------------------------------

    [TestMethod]
    public async Task WithoutATouch_TheKioskLocksAfterTheIdleLock_AndATouchPutsItOff()
    {
        var options = new KioskConsoleOptions { IdleLock = TimeSpan.FromSeconds(30) };
        await using var harness = await KioskHarness.CreateAsync(options: options);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(20));
        harness.Console.Touch().Should().BeFalse("the screen was on: the touch reaches the control under it");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(20));
        harness.Console.View.IsUnlocked.Should().BeTrue("the touch 20 s ago keeps it unlocked");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));

        await harness.WaitForAsync(view => !view.IsUnlocked, "the idle lock");
        harness.Console.View.Notices[0].Text.Should().Be(KioskText.IdleLocked(options.IdleLock)).And.Be("Locked after 30 s without a touch.");
        await ClientTestSupport.WaitUntilAsync(() => harness.Credential.PinSession is null, "the PIN session to end");
    }

    [TestMethod]
    public async Task TouchesKeepThePinSessionOpen_AndWithoutRequestsTheKioskLocksBeforeTheControllerEndsIt()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:Identity:PinSessionIdleTimeout"] = "00:01:00" },
            options: new KioskConsoleOptions { IdleLock = TimeSpan.FromMinutes(10) });
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Credential.PinSession!.IdleTimeoutSeconds.Should().Be(60);

        // A touch every 10 s for two minutes: each quarter of the session's idle timeout, one touch keeps it open.
        for (var i = 0; i < 12; i++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
            harness.Console.Touch();
            await Task.Delay(20);
        }

        harness.Console.View.IsUnlocked.Should().BeTrue("the touches kept the session open");

        // No touch: no request keeps the session open, and the kiosk locks before the controller refuses it.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(61));

        await harness.WaitForAsync(view => !view.IsUnlocked, "the session idle lock");
        harness.Console.View.Notices[0].Text.Should().Be(KioskText.SessionIdleLocked(TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public async Task TheScreenBlanksAfterTheControllersTimeout_AndTheTouchThatWakesItIsSwallowed()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerUi:KioskScreenTimeout"] = "00:01:00" });
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(1), "the controller's screen timeout");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(59));
        harness.Console.View.IsBlank.Should().BeFalse();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.WaitForAsync(view => view.IsBlank, "the blank screen");

        harness.Console.Touch().Should().BeTrue("the touch only wakes the screen");
        harness.Console.View.IsBlank.Should().BeFalse();
        harness.Console.Touch().Should().BeFalse("the screen is on again: the next touch reaches the control under it");
    }

    [TestMethod]
    public async Task TheScreenWakes_ForStop_AndWhenTheRoofStartsOrStopsMoving()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerUi:KioskScreenTimeout"] = "00:00:30" });
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromSeconds(30), "the controller's screen timeout");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForAsync(view => view.IsBlank, "the blank screen");

        harness.Push(KioskHarness.Opening(leaseSeconds: null));
        await harness.WaitForAsync(view => !view.IsBlank, "the screen to wake as the roof starts moving");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(40), step: TimeSpan.FromSeconds(1));
        harness.Push(KioskHarness.Opening(leaseSeconds: null));
        harness.Console.View.IsBlank.Should().BeFalse("the screen stays on while the roof moves");

        harness.Push(KioskHarness.Snapshot(RoofControllerStatus.Open));
        await harness.WaitForAsync(view => view.Status!.Status == RoofControllerStatus.Open, "the roof to stop");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForAsync(view => view.IsBlank, "the blank screen again");

        await harness.Console.StopAsync();

        harness.Console.View.IsBlank.Should().BeFalse("Stop wakes the screen to show its answer");
        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged);
    }

    [TestMethod]
    public async Task AZeroScreenTimeout_KeepsTheScreenOn()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerUi:KioskScreenTimeout"] = "00:00:00" });
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.Zero, "the controller's screen timeout");

        await harness.AdvanceAsync(TimeSpan.FromHours(1), step: TimeSpan.FromMinutes(1));

        harness.Console.View.IsBlank.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("120", 120.0)]
    [DataRow("0", 0.0)]
    [DataRow("99999", 14400.0)]
    [DataRow("-1", null)]
    [DataRow("\"soon\"", null)]
    [DataRow(null, null)]
    public void ReadScreenTimeout_TakesTheControllersSeconds_UpToTheMost(string? json, double? seconds)
    {
        var settings = new RoofSettingsResponse(
            1, null, null, false, null, null, [],
            json is null
                ? []
                : [new RoofSettingState(KioskConsole.ScreenTimeoutKey, "ui", JsonDocument.Parse(json).RootElement.Clone(), true, "file", true, null, false, null)]);

        KioskConsole.ReadScreenTimeout(settings).Should().Be(seconds is { } value ? TimeSpan.FromSeconds(value) : null);
    }

    // ---- Motion and the lease -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_HoldsTheLease_RenewsItWhileTheRoofMoves_AndLetsGoWhenItStops()
    {
        await using var harness = await KioskHarness.CreateAsync(options: new KioskConsoleOptions { IdleLock = TimeSpan.FromSeconds(10) });
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.HoldsLease.Should().BeTrue();
        view.Busy.Should().BeNull();
        view.Status!.Status.Should().Be(RoofControllerStatus.Opening);
        view.OpenBlock.Should().Be(RoofCommandRules.Moving);
        view.Notices[0].Should().Be(new KioskNotice(LeaseHeld, KioskNoticeLevel.Info, KioskHarness.Start));

        // Longer than the idle lock: the kiosk does not lock while it holds a motion's lease.
        for (var renewal = 1; renewal <= 8; renewal++)
        {
            await RenewedAsync(harness, renewal);
        }

        harness.Console.View.IsUnlocked.Should().BeTrue("the kiosk does not lock while the roof it set moving moves");
        harness.Console.View.HoldsLease.Should().BeTrue();

        harness.Push(KioskHarness.Snapshot(RoofControllerStatus.Open));

        await harness.WaitForAsync(view => !view.HoldsLease, "the lease to be let go");
        await NoRenewalAsync(harness, 8);
        await harness.WaitForAsync(view => !view.IsUnlocked, "the idle lock, counted from the end of the motion");
    }

    [TestMethod]
    public async Task Stop_LetsGoOfTheLease()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();
        harness.Console.View.HoldsLease.Should().BeTrue();

        await harness.Console.StopAsync();

        harness.Console.View.HoldsLease.Should().BeFalse();
        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged);
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
        await NoRenewalAsync(harness, 0);
    }

    [TestMethod]
    public async Task LockingWhileTheRoofMoves_LetsGoOfTheLease_AndSaysTheRoofStopsWhenItRunsOut()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();

        await harness.Console.LockAsync();

        var view = harness.Console.View;
        view.HoldsLease.Should().BeFalse();
        view.Notices.Take(2).Should().Equal(
            new KioskNotice(KioskText.LockedNotice, KioskNoticeLevel.Info, KioskHarness.Start),
            new KioskNotice(KioskText.LeaseDroppedOnLock, KioskNoticeLevel.Warning, KioskHarness.Start));
        await NoRenewalAsync(harness, 0);
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("locked while it held the operator lease"));
    }

    [TestMethod]
    public async Task ARenewalThatFails_IsTriedAgainSoon_AndTheKioskWarns()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();
        harness.Roof.Mock.Setup(service => service.RenewLease()).Returns(Result<RoofStatusResponse>.Failure(new InvalidOperationException("The bus is busy.")));

        harness.Clock.Advance(Renewal);

        await harness.WaitForAsync(view => view.Notices[0].Text.StartsWith("The lease could not be renewed: ", StringComparison.Ordinal), "the warning");
        var warning = harness.Console.View.Notices[0];
        warning.Level.Should().Be(KioskNoticeLevel.Danger);
        warning.Text.Should().EndWith(" If the controller is running, it stops the roof when the lease runs out.");
        harness.Console.View.HoldsLease.Should().BeTrue();

        harness.Clock.Advance(KioskConsole.LeaseRetryDelay);

        await ClientTestSupport.WaitUntilAsync(() => Renewals(harness) == 2, "the second try");
    }

    [TestMethod]
    public async Task ARenewalRefused_AsNoLease_LetsGo_WithoutAWarning()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();
        var notices = harness.Console.View.Notices.Count;
        harness.Roof.Mock.Setup(service => service.RenewLease())
            .Returns(Result<RoofStatusResponse>.Failure(new RoofControllerException(RoofControllerErrorCode.LeaseNotActive, "No motion lease is active.")));

        harness.Clock.Advance(Renewal);

        await harness.WaitForAsync(view => !view.HoldsLease, "the lease to be let go");
        harness.Console.View.Notices.Should().HaveCount(notices, "another client's Stop, or the roof stopping, ends a lease: nothing went wrong");
        await NoRenewalAsync(harness, 1);
    }

    [TestMethod]
    public async Task ClearFault_IsOfferedForALatchedFault_AndSaysWhatTheControllerAnswered()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Report(KioskHarness.Snapshot(RoofControllerStatus.Error, faultLatched: true));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Console.View.OpenBlock.Should().Be(RoofCommandRules.FaultLatched);
        harness.Console.View.ClearFaultBlock.Should().BeNull();

        await harness.Console.ClearFaultAsync();

        var view = harness.Console.View;
        view.Notices[0].Text.Should().StartWith("Clear fault accepted. Fault: ");
        view.Status!.IsFaultLatched.Should().BeFalse();
        view.ClearFaultBlock.Should().Be(RoofCommandRules.NoFault);
        harness.Calls(nameof(IRoofControllerServiceV4.ClearFault)).Should().Be(1);
    }

    [TestMethod]
    public async Task ARefusedOpen_SaysWhy_AndShowsTheStatusThatCameWithIt()
    {
        await using var harness = await KioskHarness.CreateAsync();
        var latched = KioskHarness.Snapshot(faultLatched: true) with { StatusVersion = 50 };
        harness.Roof.Mock.Setup(service => service.Open())
            .Returns(Result<RoofControllerStatus>.Failure(new RoofControllerException(RoofControllerErrorCode.FaultLatched, "Relay 2 read back high.", latched)));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.Notices[0].Level.Should().Be(KioskNoticeLevel.Danger);
        view.Notices[0].Text.Should().StartWith("A fault is latched.").And.EndWith(" Relay 2 read back high.");
        view.Status!.IsFaultLatched.Should().BeTrue();
        view.OpenBlock.Should().Be(RoofCommandRules.FaultLatched);
        view.HoldsLease.Should().BeFalse();
        view.Busy.Should().BeNull();
    }

    [TestMethod]
    public async Task AnOpenAcceptedAfterStopWasSent_IsStoppedAgain()
    {
        await using var harness = await KioskHarness.CreateAsync();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        harness.OpensTheRoof(whileOpening: () =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        var open = harness.Console.OpenAsync();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        harness.Console.View.Busy.Should().Be("Sending Open…");
        harness.Console.View.OpenBlock.Should().Be(RoofCommandRules.CommandInFlight);
        var stop = harness.Console.StopAsync();
        await ClientTestSupport.WaitUntilAsync(() => harness.Calls(nameof(IRoofControllerServiceV4.Stop)) == 1, "the first Stop");
        release.Release();
        await open;
        await stop;

        await ClientTestSupport.WaitUntilAsync(() => harness.Calls(nameof(IRoofControllerServiceV4.Stop)) == 2, "Stop sent again");
        harness.Console.View.HoldsLease.Should().BeFalse();
        harness.Console.View.Notices.Select(notice => notice.Text).Should().Contain("Open was accepted after Stop was sent, so Stop is sent again.");
        await NoRenewalAsync(harness, 0);
    }

    [TestMethod]
    public async Task AnUnansweredOpen_SaysTheRoofMayBeMoving_AndHowToStopIt()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        harness.CutAnswer = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Open", StringComparison.OrdinalIgnoreCase);
        var unanswered = RoofCommandRules.DescribeUnanswered(
            new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), "Open", $"press {RoofStopText.ButtonLabel}");
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.Console.OpenAsync();

        harness.Console.View.Notices[0].Should().Be(new KioskNotice(unanswered, KioskNoticeLevel.Danger, KioskHarness.Start));
        harness.Console.View.HoldsLease.Should().BeFalse("there is no lease to renew without an answer");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(0, "no Stop crossed it: the notice says how to stop the roof");
    }

    // ---- A controller that cannot be reached -----------------------------------------------------------------------------

    [TestMethod]
    public async Task AnUnreachableController_IsSaidSo_AndStopIsStillTried_AndSaysItFailed()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Reachable = false;

        harness.Console.Start();

        await harness.WaitForAsync(view => view.IsUnreachable, "the unreachable controller");
        var view = harness.Console.View;
        view.FeedLabel.Should().Be("unreachable");
        view.FeedBanner.Should().Be(KioskText.DescribeUnreachable(null));
        view.Status.Should().BeNull();

        await harness.Console.StopAsync();

        view = harness.Console.View;
        view.StopOutcome.Should().Be(RoofStopOutcome.Failed);
        view.StopMessage.Should().StartWith("Stop failed: ").And.EndWith(RoofStopText.UseRoofStop);
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(0);

        harness.Reachable = true;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1), step: TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(view => view.FeedLabel == "live", "the live status once the controller is back");
        harness.Console.View.FeedBanner.Should().BeNull();
    }

    [TestMethod]
    public async Task AStopWithNoAnswer_SaysItIsUnverified_NotThatTheRoofStopped()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        harness.CutAnswer = request => request.RequestUri!.AbsolutePath.EndsWith("/Stop", StringComparison.OrdinalIgnoreCase);

        await harness.Console.StopAsync();

        var view = harness.Console.View;
        view.StopOutcome.Should().NotBe(RoofStopOutcome.Acknowledged);
        view.StopMessage.Should().NotBe(RoofStopText.AcknowledgedVerified).And.NotBe(RoofStopText.Acknowledged);
        view.StopMessage.Should().Contain("roof");
    }

    [TestMethod]
    public async Task AQuietFeed_IsStale_AndOffersNoMotion_UntilTheNextStatus()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        harness.Clock.Advance(ClientTestSupport.FastFeed.StaleAfter);

        await harness.WaitForAsync(view => view.FeedLabel == "STALE", "the stale status");
        var since = KioskHarness.Start + ClientTestSupport.FastFeed.StaleAfter;
        var stale = harness.Console.View;
        stale.StaleSince.Should().Be(since);
        stale.FeedBanner.Should().Be($"STALE: no status since {RoofStatusText.Time(since)}. Showing the last known state; Stop still works.");
        stale.OpenBlock.Should().Be(RoofCommandRules.Stale);

        await harness.Console.OpenAsync();
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);

        harness.Push(KioskHarness.Snapshot());

        await harness.WaitForAsync(view => view.FeedLabel == "live", "the live status again");
        harness.Console.View.OpenBlock.Should().BeNull();
    }

    [TestMethod]
    public async Task ARefusedDeviceKey_IsSaidSo_AndStopSaysTheKeyWasRefused()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Key"] = "test-other-kiosk-key-not-a-real-secret-07"
        });

        harness.Console.Start();

        await harness.WaitForAsync(view => view.FeedRefused, "the refused key");
        harness.Console.View.FeedLabel.Should().Be("refused");
        harness.Console.View.FeedBanner.Should().Be(KioskText.DescribeKeyRefused(null));

        await harness.Console.StopAsync();

        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.Failed);
        harness.Console.View.StopMessage.Should().Be(RoofStopText.KeyRefused);
    }

    // ---- Closing ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Closing_LetsGoOfTheLease_AndTheKioskChangesNoMore()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();
        var changes = 0;
        harness.Console.Changed += () => Interlocked.Increment(ref changes);

        await harness.Console.DisposeAsync();
        await harness.Console.DisposeAsync();

        await NoRenewalAsync(harness, 0);
        harness.Push(KioskHarness.Snapshot());
        harness.Console.Touch().Should().BeFalse();
        (await harness.Console.UnlockAsync(KioskHarness.Admin, TestSecrets.Pin)).Should().Be("The kiosk is closing.");
        await Task.Delay(100);
        changes.Should().Be(0);
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("closed while it held the operator lease"));
    }

    [TestMethod]
    public async Task Notices_KeepTheNewest()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();

        for (var i = 0; i < KioskConsole.NoticeLimit + 2; i++)
        {
            await harness.Console.OpenAsync();
        }

        harness.Console.View.Notices.Should().HaveCount(KioskConsole.NoticeLimit);
    }

    [TestMethod]
    public async Task AConsole_NeedsAKioskCredential()
    {
        await using var harness = await KioskHarness.CreateAsync();
        using var client = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        FluentActions.Invoking(() => new KioskConsole(client)).Should().Throw<ArgumentException>().WithMessage("The kiosk needs a kiosk credential*");
    }

    private static int Renewals(KioskHarness harness) => harness.Calls(nameof(IRoofControllerServiceV4.RenewLease));

    // Moves to the next renewal, then waits until the kiosk has its answer (so the one after is scheduled).
    private static async Task RenewedAsync(KioskHarness harness, int renewals)
    {
        var before = harness.Console.View.Status!.StatusVersion;
        harness.Clock.Advance(Renewal);
        await ClientTestSupport.WaitUntilAsync(() => Renewals(harness) == renewals, $"renewal {renewals}");
        await harness.WaitForAsync(view => view.Status!.StatusVersion > before, $"the answer to renewal {renewals}");
    }

    private static async Task NoRenewalAsync(KioskHarness harness, int renewals)
    {
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Renewals(harness).Should().Be(renewals, "the kiosk no longer renews the lease");
    }
}
