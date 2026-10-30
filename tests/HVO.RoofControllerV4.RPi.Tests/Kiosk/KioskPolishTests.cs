using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// #208's kiosk polish, drawn headless against the controller's real API and the emulated roof: Stop's and Open's and
/// Close's state colours, the compact PIN pad and its people step, page controls only where contents overflow, About's
/// labelled cards and the Terminal page's visible exit.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KioskPolishTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task Stop_IsDimWhileIdle_BrightWhileTheRoofMoves_AndAlwaysEnabled(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);

        // RenderAsync also checks the drawn Stop is in the colour of its state.
        await screen.RenderAsync("state-idle", TestContext);
        await OnUiAsync(() =>
        {
            ShouldBeIdle(screen.Shell);
            var roof = screen.Shell.RoofPage;
            roof.OpenState.Should().Be(RoofActionState.Available, "the closed roof can be opened");
            Colour(roof.Open, "ButtonBackground").Should().Be(Color.Parse(RoofUiPalette.MotionAvailable));
            Colour(roof.Open, "ButtonForeground").Should().Be(Color.Parse(RoofUiPalette.MotionAvailableText));
            roof.CloseState.Should().Be(RoofActionState.Disabled, "the roof is already closed");
            roof.Close.IsEffectivelyEnabled.Should().BeFalse();
            Colour(roof.Close, "ButtonBackgroundDisabled").Should().Be(Color.Parse(RoofUiPalette.MotionDisabled), "disabled is neutral, not a faded green");
            Colour(roof.Close, "ButtonForegroundDisabled").Should().Be(Color.Parse(RoofUiPalette.MotionDisabledText));
        });

        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.Status?.Status == RoofControllerStatus.Opening, "the opening roof");
        await screen.RenderAsync("state-opening", TestContext);
        await OnUiAsync(() =>
        {
            ShouldBeActive(screen.Shell);
            var roof = screen.Shell.RoofPage;
            roof.OpenState.Should().Be(RoofActionState.Running);
            KioskTheme.GetText(roof.Open).Should().Contain("Opening", "running is said in words too");
            roof.Open.IsEffectivelyEnabled.Should().BeFalse("the roof is already opening");
            Colour(roof.Open, "ButtonBackgroundDisabled").Should().Be(Color.Parse(RoofUiPalette.MotionRunning), "a running button keeps its bright green");
            roof.CloseState.Should().Be(RoofActionState.Disabled);
        });

        harness.Push(KioskHarness.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing));
        await harness.WaitForAsync(view => view.Status?.Status == RoofControllerStatus.Closing, "the closing roof");
        await screen.RenderAsync("state-closing", TestContext);
        await OnUiAsync(() =>
        {
            ShouldBeActive(screen.Shell);
            screen.Shell.RoofPage.CloseState.Should().Be(RoofActionState.Running);
            KioskTheme.GetText(screen.Shell.RoofPage.Close).Should().Contain("Closing");
            screen.Shell.RoofPage.OpenState.Should().Be(RoofActionState.Disabled);
            // The feed drops while the roof was last seen closing: Stop stays bright, nothing is shown as running.
            screen.Shell.Update(harness.Console.View with { StaleSince = KioskHarness.Start, IsUnreachable = true, OpenBlock = "Live status required.", CloseBlock = "Live status required." });
            ShouldBeActive(screen.Shell);
            screen.Shell.RoofPage.CloseState.Should().Be(RoofActionState.Disabled, "a stale status is never shown as running");
        });
        await screen.RenderAsync("state-closing-stale", TestContext);

        await screen.TouchAsync(screen.Shell.Stop);
        await harness.WaitForAsync(view => view.StopOutcome == RoofStopOutcome.Acknowledged, "the Stop sent while bright");
        harness.Push(KioskHarness.Snapshot(RoofControllerStatus.Stopped));
        await harness.WaitForAsync(view => view.Status?.Status == RoofControllerStatus.Stopped, "the stopped roof");
        await screen.RenderAsync("state-stopped", TestContext);
        await OnUiAsync(() => ShouldBeIdle(screen.Shell));
    }

    [TestMethod]
    public async Task Stop_IsIdleAndEnabled_WhileLockedAndBeforeAnyStatus()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await OnUiAsync(() =>
        {
            ShouldBeIdle(screen.Shell);
            screen.Shell.Update(KioskView.NotStarted);
            ShouldBeIdle(screen.Shell);
            // A last known moving roof keeps Stop bright even before the kiosk is unlocked.
            screen.Shell.Update(harness.Console.View with { Status = KioskHarness.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) });
            ShouldBeActive(screen.Shell);
        });
        await screen.RenderAsync("state-locked-moving", TestContext);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ThePinPad_IsCompactAndCentred_AndCancelOnThePeopleStepReturnsToIt(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad.Users.Count == 2, "the people with a PIN");
        await screen.RenderAsync("polish-people", TestContext);
        await OnUiAsync(() =>
        {
            screen.Visible($"pin-user-{KioskHarness.Operator}").Should().BeTrue();
            ButtonText(screen, $"pin-user-{KioskHarness.Operator}").Should().Contain(KioskHarness.Operator).And.Contain("Operator · Controller account");
            ButtonText(screen, $"pin-user-{KioskHarness.Admin}").Should().Contain("Admin · Controller account");
            screen.Visible("pin-people-previous").Should().BeFalse("two people fit: no inert page controls");
            screen.Visible("pin-people-next").Should().BeFalse();
            screen.Visible("pin-people-cancel").Should().BeTrue();
        });

        // Cancel on the people step goes to the PIN step, not the roof.
        await screen.ClickAsync(screen.Find("pin-people-cancel")!);
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Unlock);
            screen.Visible("pin-digit-1").Should().BeTrue();
            screen.Shell.PinPad.Selected.Should().BeNull();
            screen.Shell.PinPad.CanUnlock.Should().BeFalse("no person is chosen yet");
        });
        await screen.RenderAsync("polish-pin-no-person", TestContext);

        // Choose a person, open the people step again, cancel: back to the PIN step for the same person.
        await screen.ClickAsync(screen.Find("pin-person")!);
        await screen.ClickAsync(screen.Find($"pin-user-{KioskHarness.Operator}")!);
        await OnUiAsync(() => screen.Text("pin-prompt").Should().Contain(KioskHarness.Operator));
        await screen.ClickAsync(screen.Find("pin-person")!);
        await OnUiAsync(() => screen.Visible("pin-people-cancel").Should().BeTrue());
        await screen.ClickAsync(screen.Find("pin-people-cancel")!);
        await OnUiAsync(() =>
        {
            screen.Shell.PinPad.Selected.Should().Be(KioskHarness.Operator, "Cancel keeps the person chosen before");
            foreach (var digit in "123") screen.Shell.PinPad.Press(digit);
        });
        await screen.RenderAsync("polish-pin", TestContext);
        await OnUiAsync(() =>
        {
            screen.OffScreen(name => name.StartsWith("pin-", StringComparison.Ordinal)).Should().BeEmpty();
            var keys = new[] { "pin-digit-1", "pin-digit-5", "pin-digit-9", "pin-backspace", "pin-clear" }.Select(name => screen.Find(name)!).ToArray();
            keys.Select(key => key.Bounds.Size).Distinct().Should().ContainSingle("the keys have one fixed size");
            keys[0].Bounds.Width.Should().BeGreaterThanOrEqualTo(screen.Metrics.Touch).And.BeLessThan(screen.Metrics.Touch * 2, "compact keys");
            var entry = (Control)screen.Find("pin-entry")!.GetVisualParent()!;
            var page = (Control)screen.Find("page")!;
            var entryCentre = entry.TranslatePoint(new Point(entry.Bounds.Width / 2, entry.Bounds.Height / 2), screen.Window)!.Value;
            var pageCentre = page.TranslatePoint(new Point(page.Bounds.Width / 2, page.Bounds.Height / 2), screen.Window)!.Value;
            Math.Abs(entryCentre.X - pageCentre.X).Should().BeLessThan(2, "the keypad is centred");
            entry.Bounds.Width.Should().BeLessThan(page.Bounds.Width * 0.95, "the keypad does not stretch across the page");
        });

        // Cancel on the PIN step leaves the page.
        await screen.ClickAsync(screen.Find("pin-cancel")!);
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Roof);
            screen.Shell.PinPad.Selected.Should().BeNull();
        });
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public void ThePeopleStep_DescribesRoleLoginTypeAndPendingSetup()
    {
        KioskPinPage.DescribePerson(new RoofPinUserResponse("ada", RoofControllerApiContract.AdminRole)).Should().Be("Admin · Controller account");
        KioskPinPage.DescribePerson(new RoofPinUserResponse("olga", RoofControllerApiContract.OperatorRole) { Provider = "virtual", PrincipalId = "p1" })
            .Should().Be("Operator · Controller account");
        KioskPinPage.DescribePerson(new RoofPinUserResponse("roy", RoofControllerApiContract.OperatorRole) { Provider = "linux", PrincipalId = "p2", SetupPending = true })
            .Should().Be("Operator · Linux account · first-time setup");
        RoofIdentityText.DescribeLoginType("ldap").Should().Be("ldap account");
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task PageControls_AreShownOnlyWhenTheContentsOverflow(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Notices));
        await screen.DrawAsync();
        await OnUiAsync(() =>
        {
            var pager = (KioskPager)screen.Find("recent-notices")!;
            pager.IsPaged.Should().BeFalse();
            screen.Visible("recent-notices-previous").Should().BeFalse("nothing to page: no Previous");
            screen.Visible("recent-notices-next").Should().BeFalse();
            screen.Shell.Update(harness.Console.View with
            {
                Notices = Enumerable.Range(0, 20).Select(i => new KioskNotice($"Notice {i}: the emulated roof reported a change.", KioskNoticeLevel.Info, KioskHarness.Start)).ToArray()
            });
        });
        await screen.DrawAsync();
        await OnUiAsync(() =>
        {
            var pager = (KioskPager)screen.Find("recent-notices")!;
            pager.IsPaged.Should().BeTrue();
            screen.Visible("recent-notices-next").Should().BeTrue("overflowing contents can be paged");
            screen.OffScreen(name => name.StartsWith("recent-notices", StringComparison.Ordinal)).Should().BeEmpty();
        });
        await screen.RenderAsync("polish-notices-paged", TestContext);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task About_ShowsLabelledCards_WithFallbacks_AndNoAdminSession(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.About));
        await screen.RenderAsync("polish-about", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text("about-client-version").Should().Be(HVO.RoofControllerV4.Common.RoofProductVersion.Describe(typeof(KioskSystemPanel).Assembly));
            screen.Text("about-controller-state").Should().Be("Live");
            screen.Text("about-controller-name").Should().Be(KioskHarness.ControllerName);
            screen.Text("about-controller-version").Should().Be(KioskAboutInfo.NotAvailable, "the PIN kiosk gets no version from the controller yet");
            var info = screen.Shell.AboutPage.Info;
            info.Host.Rows.Select(row => row.Label).Should().Equal("CPU", "Memory", "Disk", "IP addresses");
            info.Host.Rows.Should().OnlyContain(row => !row.IsKnown && row.Value == KioskAboutInfo.NotAvailable);
            info.Host.Note.Should().Contain("administrator");
            if (width >= 1280)
            {
                var pager = (KioskPager)screen.Find("about-pages")!;
                pager.IsPaged.Should().BeFalse($"About fits one page at {width}x{height}");
            }
        });

        await OnUiAsync(() => screen.Shell.Update(harness.Console.View with { IsUnreachable = true, StaleSince = KioskHarness.Start }));
        await screen.RenderAsync("polish-about-unreachable", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text("about-controller-state").Should().Be("Unreachable");
            screen.Text("about-controller-version").Should().Be(KioskAboutInfo.Unreachable);
            screen.Shell.AboutPage.Info.Host.Rows.Should().OnlyContain(row => row.Value == KioskAboutInfo.Unreachable);
        });

        await OnUiAsync(() => screen.Shell.Update(harness.Console.View with { FeedRefused = true, IsUnreachable = false }));
        await OnUiAsync(() =>
        {
            screen.Text("about-controller-state").Should().Contain("Refused");
            screen.Text("about-controller-version").Should().Be(KioskAboutInfo.NotConfigured);
        });
    }

    [TestMethod]
    public void AboutInfo_ShowsSuppliedControllerFacts_OnlyWhileLive()
    {
        var live = new KioskView
        {
            IsStarted = true,
            UnlockedBy = "ada",
            Role = RoofControllerApiContract.AdminRole,
            Status = KioskHarness.Snapshot(),
            FeedLabel = "live"
        };
        var facts = new KioskControllerFacts { Version = "9.8.7", Uptime = "1h 2m", Cpu = "3.0 %", Memory = "64.0 MiB", Disk = "40 % used", Addresses = "192.0.2.10" };
        var self = ("This app", "1.2.3");
        var shown = KioskAboutInfo.Describe(live, KioskWording.Desktop, self, facts);
        shown.Controller.Rows.Single(row => row.Name == "about-controller-version").Value.Should().Be("9.8.7");
        shown.Host.Rows.Select(row => row.Value).Should().Equal("3.0 %", "64.0 MiB", "40 % used", "192.0.2.10");
        shown.Host.Note.Should().BeNull("every slot is filled");

        var partial = KioskAboutInfo.Describe(live, KioskWording.Desktop, self, facts with { Disk = null, Addresses = null });
        partial.Host.Rows.Where(row => !row.IsKnown).Select(row => row.Label).Should().Equal("Disk", "IP addresses");
        partial.Host.Note.Should().Contain("does not report disk use or addresses");

        var stale = KioskAboutInfo.Describe(live with { StaleSince = KioskHarness.Start }, KioskWording.Desktop, self, facts);
        stale.Controller.Rows.Single(row => row.Name == "about-controller-version").Value.Should().Be(KioskAboutInfo.Unreachable, "a value from before the feed went stale is not shown as current");
        stale.Controller.Rows.Single(row => row.Name == "about-controller-state").Value.Should().StartWith("Stale");

        var notStarted = KioskAboutInfo.Describe(KioskView.NotStarted, KioskWording.Kiosk, self, facts);
        notStarted.Controller.Rows.Single(row => row.Name == "about-controller-state").Value.Should().Be("Connecting…");
        notStarted.Host.Rows.Should().OnlyContain(row => row.Value == KioskAboutInfo.NotAvailable);
        notStarted.Device.Note.Should().Be(KioskAboutInfo.PinPolicy);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task Terminal_ExitIsOnScreen_WithNoInertPageControls(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var exit = new KioskTerminalExit(harness.Console, () => Task.FromResult(false));
        await using var screen = await KioskScreen.ShowAsync(harness.Console, width, height, density, exit);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Terminal));
        await screen.RenderAsync("polish-terminal", TestContext);
        await OnUiAsync(() =>
        {
            screen.Visible("terminal-exit").Should().BeTrue();
            screen.OffScreen(name => name.StartsWith("terminal-", StringComparison.Ordinal)).Should().BeEmpty("Exit UI to terminal is fully on screen");
            var pager = (KioskPager)screen.Find("terminal-pages")!;
            if (!pager.IsPaged) screen.Visible("terminal-pages-next").Should().BeFalse("no inert page controls");
            if (width >= 1280) pager.IsPaged.Should().BeFalse($"the explanation fits one page at {width}x{height}");
        });
        await screen.ClickAsync(screen.Find("terminal-exit")!);
        await OnUiAsync(() =>
        {
            exit.IsConfirming.Should().BeTrue();
            screen.Visible("terminal-cancel").Should().BeTrue();
            screen.OffScreen(name => name.StartsWith("terminal-", StringComparison.Ordinal)).Should().BeEmpty("the confirmation and Cancel are fully on screen");
        });
        await screen.RenderAsync("polish-terminal-confirm", TestContext);
        await screen.ClickAsync(screen.Find("terminal-cancel")!);
        await OnUiAsync(() => exit.IsConfirming.Should().BeFalse());
    }

    private static void ShouldBeIdle(KioskShell shell)
    {
        shell.StopActive.Should().BeFalse("there is nothing to stop");
        shell.Stop.IsEffectivelyEnabled.Should().BeTrue("Stop is never disabled");
        Colour(shell.Stop, "ButtonBackground").Should().Be(Color.Parse(RoofUiPalette.StopButton));
        Colour(shell.Stop, "ButtonBackgroundPressed").Should().Be(Color.Parse(RoofUiPalette.StopButton));
    }

    private static void ShouldBeActive(KioskShell shell)
    {
        shell.StopActive.Should().BeTrue("the roof is moving");
        shell.Stop.IsEffectivelyEnabled.Should().BeTrue("Stop is never disabled");
        Colour(shell.Stop, "ButtonBackground").Should().Be(Color.Parse(RoofUiPalette.StopButtonActive));
        Colour(shell.Stop, "ButtonForeground").Should().Be(Color.Parse(RoofUiPalette.StopButtonActiveText));
        Colour(shell.Stop, "ButtonBackgroundFocused").Should().Be(Color.Parse(RoofUiPalette.StopButtonActive), "focus keeps the red");
    }

    private static Color Colour(Button button, string resource) => ((ISolidColorBrush)button.Resources[resource]!).Color;

    private static string ButtonText(KioskScreen screen, string name)
        => string.Join(" ", screen.Find(name)!.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
}
