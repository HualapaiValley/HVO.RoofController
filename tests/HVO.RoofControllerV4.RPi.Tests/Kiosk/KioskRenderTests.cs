using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's screens as Avalonia draws them (its headless platform, rendering with Skia and the kiosk's font), at the
/// Pi Touch Display 2's 1280x720 (8.2 pixels a millimetre) and the original 7-inch touchscreen's 800x480 (5.2). Each
/// screen is saved as a PNG, in <c>HVO_KIOSK_RENDERS_DIR</c> or the test results' kiosk folder, and checked: every
/// button is at least the 12 mm touch target and inside the screen, no text is cut off, and Stop is on screen, enabled,
/// in its colour and the first thing a touch on it reaches. The console behind each screen is the one the kiosk runs, against the
/// controller's real API (<see cref="KioskHarness"/>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KioskRenderTests
{
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task Locked_ShowsTheStatus_AndStop_AndNothingThatMovesTheRoof(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("locked", TestContext);

        await OnUiAsync(() =>
        {
            screen.Shell.RoofPage.Open.IsEffectivelyVisible.Should().BeFalse("Open needs an operator's PIN");
            screen.Shell.RoofPage.Close.IsEffectivelyVisible.Should().BeFalse();
            screen.Visible("nav-settings").Should().BeFalse();
            screen.Text("nav-lock").Should().Be("Unlock");
            screen.Text("hint").Should().StartWith(KioskRoofPage.LockedHint);
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AnOperator_IsOfferedOpen_AndCloseIsShownButNotOffered_ForAClosedRoof(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("operator", TestContext);

        await OnUiAsync(() =>
        {
            var roof = screen.Shell.RoofPage;
            roof.Open.IsEffectivelyVisible.Should().BeTrue();
            roof.Open.IsEffectivelyEnabled.Should().BeTrue();
            roof.Close.IsEffectivelyVisible.Should().BeTrue();
            roof.Close.IsEffectivelyEnabled.Should().BeFalse("the roof is closed");
            screen.Text("nav-lock").Should().Be("Lock");
            screen.Visible("nav-settings").Should().BeTrue();
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AnOpeningRoof_ShowsTheLeaseTheKioskRenews(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.HoldsLease && view.Status?.Status == RoofControllerStatus.Opening, "the opening roof");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("opening", TestContext);

        await OnUiAsync(() =>
        {
            screen.Visible("lease").Should().BeTrue();
            screen.Text("position").Should().Be(RoofText.DescribePosition(RoofControllerStatus.Opening));
            screen.Shell.RoofPage.Open.IsEffectivelyEnabled.Should().BeFalse("the roof is already opening");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ALatchedFault_IsABanner_AndClearFaultIsOffered(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Report(KioskHarness.Snapshot(RoofControllerStatus.Error, faultLatched: true));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("fault", TestContext);

        await OnUiAsync(() =>
        {
            screen.Visible("fault-banner").Should().BeTrue();
            screen.Shell.RoofPage.ClearFault.IsEffectivelyEnabled.Should().BeTrue();
            screen.Shell.RoofPage.Open.IsEffectivelyEnabled.Should().BeFalse();
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AStaleStatus_IsMarked_AndOffersNoMotion(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        harness.Clock.Advance(ClientTestSupport.FastFeed.StaleAfter);
        await harness.WaitForAsync(view => view.FeedLabel == "STALE", "the stale status");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("stale", TestContext);

        await OnUiAsync(() =>
        {
            screen.Text("feed-banner").Should().StartWith("STALE: ");
            screen.Text("status-time").Should().StartWith("Last known state");
            screen.Shell.RoofPage.Open.IsEffectivelyEnabled.Should().BeFalse();
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AnUnreachableController_IsSaidSo_AndStopSaysItFailed(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Reachable = false;
        harness.Console.Start();
        await harness.WaitForAsync(view => view.IsUnreachable, "the unreachable controller");
        await harness.Console.StopAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("unreachable", TestContext);

        await OnUiAsync(() =>
        {
            screen.Text("feed-banner").Should().Be(KioskText.DescribeUnreachable(null));
            screen.Text("stop-message").Should().StartWith("Stop failed: ");
            screen.Text("position").Should().Be("Unknown");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ThePinPad_ListsThePeople_AndShowsADotPerDigit(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad.Users.Count == 2, "the people with a PIN");
        await OnUiAsync(() =>
        {
            screen.Shell.PinPad.Select(KioskHarness.Operator);
            foreach (var digit in "123")
            {
                screen.Shell.PinPad.Press(digit);
            }
        });

        await screen.RenderAsync("pin", TestContext);

        await OnUiAsync(() =>
        {
            screen.Visible($"pin-user-{KioskHarness.Operator}").Should().BeTrue();
            screen.Visible($"pin-user-{KioskHarness.Admin}").Should().BeTrue();
            screen.Text("pin-masked").Should().Be("●●●");
            screen.OffScreen(name => name.StartsWith("pin-", StringComparison.Ordinal) && !name.StartsWith("pin-user-", StringComparison.Ordinal))
                .Should().BeEmpty("the PIN, its keys, Unlock and Cancel are on the screen without scrolling (the list of people may scroll)");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task AnAdmin_SeesTheSystem_AndRestart(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.System));
        await UntilAsync(() => screen.Shell.System is { Health: not null, Busy: null, Information.Count: > 0 }, "the controller's health and host");
        (await screen.MaskAsync(Environment.MachineName)).Should().Be(1, "the host is shown once");

        await screen.RenderAsync("admin-system", TestContext);

        await OnUiAsync(() => screen.Visible("system-restart").Should().BeTrue());
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task TheSettings_TheirKeypad_AndTheirKeyboard(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "the settings");
        await screen.RenderAsync("settings", TestContext);
        await OnUiAsync(() => screen.Visible("settings-hand-edit").Should().BeTrue());

        await OnUiAsync(() =>
        {
            screen.Shell.Settings.SelectField(KioskTimeout);
            screen.Shell.Settings.BeginEdit();
            screen.Shell.Settings.ClearText();
            screen.Shell.Settings.Type("120");
        });
        await screen.RenderAsync("settings-keypad", TestContext);
        await OnUiAsync(() =>
        {
            screen.Visible("editor-key-ms").Should().BeTrue("a duration takes a unit");
            screen.Text("editor-value").Should().Be("120");
            screen.OffScreen(name => name.StartsWith("editor-", StringComparison.Ordinal)).Should().BeEmpty("the value, Save, Cancel and every key are on the screen without scrolling");
        });

        await OnUiAsync(() =>
        {
            screen.Shell.Settings.CancelEdit();
            screen.Shell.Settings.SelectField(DefaultCamera);
            screen.Shell.Settings.BeginEdit();
            screen.Shell.Settings.ClearText();
            screen.Shell.Settings.ToggleShift();
            screen.Shell.Settings.Type("p");
            screen.Shell.Settings.Type("ier");
        });
        await screen.RenderAsync("settings-keyboard", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text("editor-value").Should().Be("Pier");
            screen.OffScreen(name => name.StartsWith("editor-", StringComparison.Ordinal)).Should().BeEmpty("the value, Save, Cancel and every key are on the screen without scrolling");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ABlankScreen_IsBlack_AndTheTouchThatWakesIt_DoesNotPressStop(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: new Dictionary<string, string?> { [KioskTimeout] = "00:01:00" });
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(1), "the controller's screen timeout");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        await harness.WaitForAsync(view => view.IsBlank, "the blank screen");

        var blank = await screen.RenderAsync("blank", TestContext, checkStop: false);

        blank.Should().Be(0, "a blank screen is black: no pixel of it is lit");

        await screen.TouchAsync(screen.Shell.Stop);
        harness.Console.View.IsBlank.Should().BeFalse("the touch wakes the screen");
        harness.Console.View.StopInFlight.Should().BeFalse("the touch that wakes the screen goes no further");
        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.None);
        await screen.RenderAsync("woken", TestContext);

        await screen.TouchAsync(screen.Shell.Stop);
        await harness.WaitForAsync(view => view.StopOutcome == RoofStopOutcome.Acknowledged, "the Stop the next touch sends");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
    }
}
