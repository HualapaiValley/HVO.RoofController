using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's screens as Avalonia draws them (its headless platform, rendering with Skia and the kiosk's font), at the
/// Pi Touch Display 2's 1280x720 (8.2 pixels a millimetre) and the original 7-inch touchscreen's 800x480 (5.2). Each
/// screen is saved as a PNG, in <c>HVO_KIOSK_RENDERS_DIR</c> or the test results' kiosk folder, and checked: every
/// button is at least the 12 mm touch target and inside the screen, nothing overlaps, no text is cut off (but for text
/// abbreviated on purpose, <see cref="KioskTheme.Abbreviated"/>, whose whole text is shown elsewhere), and Stop is on
/// screen, enabled, in its colour and the first thing a touch on it reaches, even as the screen blanks. The console behind
/// each screen is the one the kiosk runs, against the controller's real API (<see cref="KioskHarness"/>).
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
            screen.Text("hint").Should().StartWith(KioskWording.Kiosk.SignedOutHint);
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
    public async Task ALongName_IsCutShortOnItsPill_AndTheTitleTheRoleAndTheLeaseStayWhole(int width, int height, double pixelsPerMillimetre)
    {
        // As long as a name may be (64 characters), with no space to wrap at.
        var name = "observatory.night.operator@hualapai-valley.example.org".PadRight(RoofIdentityContract.MaximumNameLength, 'x');
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await RoofClientApiTests.AddUserAsync(harness.Host, name, RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        await harness.UnlockAsync(name);
        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.HoldsLease && view.Status?.Status == RoofControllerStatus.Opening, "the opening roof");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await screen.RenderAsync("long-name", TestContext);

        await OnUiAsync(() =>
        {
            screen.Text("title").Should().Be(KioskHarness.ControllerName);
            screen.Visible("lease").Should().BeTrue();
            var pill = screen.Find("unlocked-by")!;
            pill.Bounds.Width.Should().BeLessThanOrEqualTo(screen.Metrics.Touch * 3, "a long name is cut short, not the header squeezed");
            var pieces = pill.GetVisualDescendants().OfType<TextBlock>().OrderBy(piece => piece.TranslatePoint(default, pill)?.X).ToList();
            pieces.Select(piece => piece.Text).Should().Equal(name, $" ({KioskText.DescribeRole(RoofControllerApiContract.OperatorRole)})");
            pieces[0].TextLayout.TextLines.Should().Contain(line => line.HasCollapsed, "the name ends in an ellipsis");
            pieces[1].TextLayout.TextLines.Should().NotContain(line => line.HasCollapsed, "the role is shown whole");
            var unlocked = KioskWording.Kiosk.SignedIn(name, RoofControllerApiContract.OperatorRole);
            screen.Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.Text?.EndsWith(unlocked, StringComparison.Ordinal) == true)
                .Should().ContainSingle("the whole name is in the notice");
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2, "HualapaiValleyObservatory", false)]
    [DataRow(800, 480, 5.2, "HualapaiValleyObservatory", false)]
    [DataRow(1280, 720, 8.2, "HualapaiValleyObservatoryRollOffRoofController", true)]
    [DataRow(800, 480, 5.2, "HualapaiValleyObservatoryRollOffRoofController", true)]
    [DataRow(1280, 720, 8.2, "Hualapai Valley Observatory roll-off roof controller", true)]
    [DataRow(800, 480, 5.2, "Hualapai Valley Observatory roll-off roof controller", true)]
    public async Task TheControllerName_IsWholeWhileTheBadgesFitBesideIt_AndKeepsHalfTheHeaderWhenNot(
        int width, int height, double pixelsPerMillimetre, string start, bool padded)
    {
        // Padded to as long as a controller name may be (64 characters); the person's name too, with the lease pill.
        var controller = padded ? start.PadRight(64, 'x') : start;
        var name = "observatory.night.operator@hualapai-valley.example.org".PadRight(RoofIdentityContract.MaximumNameLength, 'x');
        await using var harness = await KioskHarness.CreateAsync();
        harness.Report(harness.Current with { ControllerName = controller });
        harness.Roof.Mock.Setup(service => service.Open()).Returns(() =>
        {
            harness.Report(KioskHarness.Opening() with { ControllerName = controller });
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        await harness.StartLiveAsync();
        await RoofClientApiTests.AddUserAsync(harness.Host, name, RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        // Locked, the badges are short: a name that fits beside them is whole.
        await screen.RenderAsync($"controller-name-locked-{controller.Length}-{(controller.Contains(' ') ? "words" : "word")}", TestContext);
        await OnUiAsync(() => TitleShouldFit(screen, controller, whole: !padded));

        await harness.UnlockAsync(name);
        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.HoldsLease && view.Status?.Status == RoofControllerStatus.Opening, "the opening roof");
        await screen.RenderAsync($"controller-name-unlocked-{controller.Length}-{(controller.Contains(' ') ? "words" : "word")}", TestContext);
        await OnUiAsync(() =>
        {
            // Unlocked, the person's pill and the lease take more room: a short name may or may not fit beside them.
            TitleShouldFit(screen, controller, whole: padded ? false : null);
            screen.Visible("lease").Should().BeTrue();
        });
    }

    private static void TitleShouldFit(KioskScreen screen, string controller, bool? whole)
    {
        var title = (TextBlock)screen.Find("title")!;
        var header = (Control)title.GetVisualParent()!;
        title.Text.Should().Be(controller);
        title.TextLayout.TextLines.Should().ContainSingle("the name is on one line, never broken inside a word");
        if (whole == true)
        {
            title.TextLayout.TextLines[0].HasCollapsed.Should().BeFalse("the name fits beside the badges");
        }
        else if (whole == false || title.TextLayout.TextLines[0].HasCollapsed)
        {
            title.TextLayout.TextLines[0].HasCollapsed.Should().BeTrue("a name too long for its share ends in an ellipsis");
            title.Bounds.Width.Should().BeGreaterThanOrEqualTo(header.Bounds.Width / 2 - 1, "the name keeps at least half the header");
        }

        screen.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && block.Text?.Contains(controller, StringComparison.Ordinal) == true && block != title)
            .Should().NotBeEmpty("the status card's Controller row shows the name whole");
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
    public async Task EverySettingsEditor_FitsTheSmallestScreen_WithItsCaptionWhole()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "the settings");
        var keys = await OnUiAsync(() => screen.Shell.Settings.Form!.Groups
            .SelectMany(group => group.Fields)
            .Where(field => field.CanWrite && !field.Setting.Secret)
            .Select(field => field.Key)
            .ToList());
        keys.Should().HaveCountGreaterThan(20, "the admin may change most settings here");

        foreach (var key in keys)
        {
            await OnUiAsync(() =>
            {
                screen.Shell.Settings.SelectField(key);
                screen.Shell.Settings.BeginEdit();
            });
            await screen.CheckAsync($"editor-{key}");
            await OnUiAsync(() =>
            {
                screen.Shell.Settings.Editing.Should().NotBeNull(key);
                screen.OffScreen(name => name.StartsWith("editor-", StringComparison.Ordinal)).Should().BeEmpty($"{key}'s editor is on the screen without scrolling");
                screen.Shell.Settings.CancelEdit();
            });
        }
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ALongCameraName_ShowsItsEnd_WhereTheTypingIs(int width, int height, double pixelsPerMillimetre)
    {
        // As long as a camera name may be (64 characters).
        var camera = "Pier camera number one on the north side of the observatory roof"[..RoofControllerUiOptions.MaximumCameraNameLength];
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "the settings");
        await OnUiAsync(() =>
        {
            screen.Shell.Settings.SelectField(DefaultCamera);
            screen.Shell.Settings.BeginEdit();
            screen.Shell.Settings.ClearText();
            screen.Shell.Settings.Type(camera);
        });

        await screen.RenderAsync("settings-long-camera", TestContext);

        await OnUiAsync(() =>
        {
            screen.Text("editor-value").Should().Be(camera);
            var value = (TextBlock)screen.Find("editor-value")!;
            value.TextTrimming.Should().Be(TextTrimming.LeadingCharacterEllipsis, "the end of the value, where the typing is, is shown");
            value.TextLayout.TextLines.Should().Contain(line => line.HasCollapsed, "the value is too long for its box");
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

    [TestMethod]
    public async Task ATouchOnStop_AsTheScreenBlanks_ButBeforeItIsDrawnBlack_StopsTheRoof()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: new Dictionary<string, string?> { [KioskTimeout] = "00:01:00" });
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(1), "the controller's screen timeout");
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await screen.DrawAsync();

        // The console blanks the screen on its own clock, off the UI thread, as the touch arrives: the touch reaches the
        // shell before the view has drawn the screen black.
        (bool Blank, bool Drawn)? asTheTouchArrived = null;
        void BlankAsTheTouchArrives(object? sender, PointerPressedEventArgs e)
        {
            // A thread of its own: a task waited for here could be run on the UI thread, which draws what it changes at once.
            var clock = new Thread(() => harness.Clock.Advance(TimeSpan.FromMinutes(1)));
            clock.Start();
            clock.Join();
            asTheTouchArrived = (harness.Console.View.IsBlank, screen.Find("blank")!.IsVisible);
        }

        await OnUiAsync(() => screen.Window.AddHandler(InputElement.PointerPressedEvent, BlankAsTheTouchArrives, RoutingStrategies.Tunnel, handledEventsToo: true));
        await screen.TouchAsync(screen.Shell.Stop);

        asTheTouchArrived.Should().Be((true, false), "the console had blanked the screen, and the view had not drawn it, when the touch reached the shell");
        await harness.WaitForAsync(view => view.StopOutcome == RoofStopOutcome.Acknowledged, "the Stop the touch sends");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1, "a touch on Stop the screen still showed stops the roof");
        harness.Console.View.IsBlank.Should().BeFalse("the touch woke the screen too");
    }
}
