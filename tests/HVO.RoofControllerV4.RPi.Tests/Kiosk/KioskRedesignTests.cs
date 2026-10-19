using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>Rendered and input-driven acceptance for the bounded kiosk and password desktop design.</summary>
[TestClass, DoNotParallelize]
public sealed class KioskRedesignTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1920, 1200, 8.2)]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task EveryKioskPage_IsBounded_WithLongSimultaneousWarningsAndPrivatePin(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        harness.Console.View.Role.Should().Be(RoofControllerApiContract.OperatorRole, "an administrator's PIN grants only Operator");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await screen.RenderAsync("native-roof", TestContext);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Readings));
        await DrawAllPages(screen, "native-readings");
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Notices));
        await DrawAllPages(screen, "native-notices");
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));
        var explanation = "The controller reports a latched drive fault. Open and Close are refused until the cause has been corrected and an operator clears the fault. ";
        var view = harness.Console.View with
        {
            Status = harness.Console.View.Status! with { IsFaultLatched = true, InputsHealthy = false, RelayRegisterReadsHealthy = false },
            ModeWarnings = ["Emulated HAT: this is controlled test data.", "Physical limit switches ignored in this simulated fixture."],
            FeedBanner = "STALE: " + string.Concat(Enumerable.Repeat(explanation, 8)),
            StaleSince = KioskHarness.Start,
            IsUnreachable = true,
            OpenBlock = "Live status required.", CloseBlock = "Live status required.",
            Notices = Enumerable.Range(0, 12).Select(i => new KioskNotice($"Notice {i}. {explanation}", KioskNoticeLevel.Warning, KioskHarness.Start)).ToArray()
        };
        await OnUiAsync(() => screen.Shell.Update(view));
        foreach (var page in new[] { KioskPage.Roof, KioskPage.Readings, KioskPage.Notices, KioskPage.About })
        {
            await OnUiAsync(() => screen.Shell.ShowPage(page));
            // Keep the controlled long view while changing pages; production ShowPage never changes the API session.
            await DrawAllPages(screen, $"redesign-{page}");
            await OnUiAsync(() =>
            {
                var pager = screen.Window.GetVisualDescendants().OfType<KioskPager>().SingleOrDefault(p => p.IsEffectivelyVisible);
                if (pager is null) return;
                var current = pager.CurrentPage;
                screen.Shell.Update(view with { Status = view.Status! with { StatusVersion = view.Status!.StatusVersion + 1 } });
                screen.Window.UpdateLayout();
                pager.CurrentPage.Should().Be(current, "live refresh does not take the reader back to page one");
            });
        }
        await OnUiAsync(() =>
        {
            screen.Visible("nav-settings").Should().BeFalse();
            screen.Visible("nav-system").Should().BeFalse();
            screen.Window.GetVisualDescendants().OfType<ScrollViewer>().Should().BeEmpty("kiosk documents use measured pages");
        });
        await harness.Console.LockAsync();
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad.Users.Count == 2, "PIN people");
        await DrawAllPages(screen, "redesign-people");
        await OnUiAsync(() =>
        {
            screen.Shell.PinPad.Select(KioskHarness.Admin);
            foreach (var digit in "123") screen.Shell.PinPad.Press(digit);
        });
        await OnUiAsync(() => screen.Shell.Update(harness.Console.View with
        {
            Status = view.Status, ModeWarnings = view.ModeWarnings, FeedBanner = view.FeedBanner,
            StaleSince = view.StaleSince, IsUnreachable = true
        }));
        await screen.RenderAsync("redesign-pin", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text("pin-masked").Should().Be("●●●");
            screen.OffScreen(name => name.StartsWith("pin-", StringComparison.Ordinal)).Should().BeEmpty();
        });
        await screen.TouchAsync(screen.Shell.Stop);
        await harness.WaitForAsync(v => v.StopOutcome == RoofStopOutcome.Acknowledged, "viewer-key Stop over private PIN entry");
        harness.Calls("Open").Should().Be(0);
        harness.Calls("Close").Should().Be(0);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task EmptyOfflineAndManyPeople_HaveBoundedRecoveryAndSelection(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync(populateUsers: false);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad is { Loading: false, Error: not null }, "empty PIN list");
        var empty = await DrawAllPages(screen, "redesign-pin-empty");
        empty.Should().Contain("No one has a PIN");
        for (var i = 0; i < 15; i++)
            await RoofClientApiTests.AddUserAsync(harness.Host, $"fixture-operator-{i:00}", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        await UiTask(() => screen.Shell.PinPad.LoadUsersAsync());
        var people = await DrawAllPages(screen, "redesign-pin-many");
        for (var i = 0; i < 15; i++) people.Should().Contain($"fixture-operator-{i:00}");
        await OnUiAsync(() =>
        {
            var pager = screen.Window.GetVisualDescendants().OfType<KioskPager>().Single(p => p.IsEffectivelyVisible);
            pager.CurrentPage.Should().Be(pager.PageCount - 1);
            var last = pager.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "pin-user-fixture-operator-14");
            last.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        await screen.RenderAsync("redesign-pin-selected-last", TestContext);
        await OnUiAsync(() => screen.Text("pin-prompt").Should().Contain("fixture-operator-14"));
        harness.Reachable = false;
        await UiTask(() => screen.Shell.PinPad.LoadUsersAsync());
        var offline = await DrawAllPages(screen, "redesign-pin-offline");
        offline.Should().Contain("could not be read");
        await screen.TouchAsync(screen.Shell.Stop);
        await harness.WaitForAsync(v => v.StopOutcome == RoofStopOutcome.Failed, "offline Stop answer");
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task Stop_RetainsRedFocus_AndEveryActualMessageIncludingUnverifiedAcknowledgement(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        var cases = new[]
        {
            (RoofStopOutcome.Sent, RoofStopText.Sending),
            (RoofStopOutcome.Acknowledged, RoofStopText.AcknowledgedVerified),
            (RoofStopOutcome.Acknowledged, RoofStopText.Acknowledged),
            (RoofStopOutcome.RelayUnverified, RoofStopText.AcknowledgedUnverified),
            (RoofStopOutcome.RelayUnverified, RoofStopText.SentUnverified),
            (RoofStopOutcome.Failed, RoofStopText.KeyRefused),
            (RoofStopOutcome.Failed, RoofStopText.Failed(string.Concat(Enumerable.Repeat("The emulated connection did not answer. ", 25))))
        };
        for (var i = 0; i < cases.Length; i++)
        {
            var (outcome, message) = cases[i];
            await OnUiAsync(() =>
            {
                screen.Shell.ShowPage(KioskPage.Roof);
                screen.Shell.Update(harness.Console.View with { StopOutcome = outcome, StopMessage = message });
                screen.Shell.Stop.Focus();
            });
            await screen.RenderAsync($"redesign-stop-{i}", TestContext);
            await OnUiAsync(() =>
            {
                screen.Shell.Stop.IsFocused.Should().BeTrue();
                ((ISolidColorBrush)screen.Shell.Stop.Resources["ButtonBackground"]!).Color.Should().Be(Color.Parse(RoofUiPalette.DangerBackground));
                ((ISolidColorBrush)screen.Shell.Stop.Resources["ButtonBackgroundFocused"]!).Color.Should().Be(Color.Parse(RoofUiPalette.DangerBackground));
                screen.OffScreen(name => name is "stop" or "stop-message" or "stop-details").Should().BeEmpty();
            });
            var shown = await OnUiAsync(() => screen.Text("stop-message")!);
            if (shown != message)
            {
                await screen.ClickAsync(screen.Find("stop-details")!);
                var whole = await DrawAllPages(screen, $"redesign-stop-detail-{i}");
                Normalise(whole.Replace(" (continued)", string.Empty, StringComparison.Ordinal)).Should().Contain(Normalise(message));
            }
            else shown.Should().Be(message);
            if (message == RoofStopText.Acknowledged)
                shown.Should().NotContain("verified", "the enum alone never proves register verification");
        }
    }

    [TestMethod]
    [DataRow(960, 600)]
    [DataRow(1280, 800)]
    public async Task Desktop_AllGroupsFieldsEditorsAndQuestions_KeepStopAndRoleBoundaries(int width, int height)
    {
        await using var harness = await KioskHarness.CreateAsync(options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        await harness.PasswordAsync(KioskHarness.Admin);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, KioskMetrics.DeskPixelsPerMillimetre);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "desktop settings");
        var groups = await OnUiAsync(() => screen.Shell.Settings.Form!.Groups.Select(g => g.Name).ToArray());
        foreach (var group in groups)
        {
            await OnUiAsync(() => screen.Shell.Settings.SelectGroup(group));
            await screen.CheckAsync($"desktop-group-{group}");
            var fields = await OnUiAsync(() => screen.Shell.Settings.Group!.Fields.Select(f => f.Key).ToArray());
            foreach (var field in fields)
            {
                await OnUiAsync(() => screen.Shell.Settings.SelectField(field));
                await screen.CheckAsync($"desktop-field-{field}");
                await OnUiAsync(() => screen.Shell.Settings.BeginEdit());
                await screen.CheckAsync($"desktop-editor-{field}");
                await OnUiAsync(() =>
                {
                    if (screen.Shell.Settings.Editing is { } editable)
                    {
                        editable.Setting.Secret.Should().BeFalse();
                        screen.OffScreen(name => name.StartsWith("editor-", StringComparison.Ordinal)).Should().BeEmpty("desktop editor actions fit above the fold");
                    }
                    screen.Shell.Settings.CancelEdit();
                });
            }
        }
        await OnUiAsync(() =>
        {
            screen.Shell.Settings.SelectField("RoofControllerOptionsV4:AtSpeedConfirmationTimeout");
            screen.Shell.Settings.BeginEdit();
            screen.Shell.Settings.ClearText();
        });
        await UiTask(() => screen.Shell.Settings.ReviewAsync());
        await screen.RenderAsync("redesign-desktop-safety-question", TestContext);
        await OnUiAsync(() => screen.Shell.Settings.Question!.Title.Should().Be("Safety-critical change"));
        await screen.ClickAsync(screen.Shell.Stop);
        await harness.WaitForAsync(v => v.StopOutcome == RoofStopOutcome.Acknowledged, "Stop over the safety question");
        harness.Roof.Applied.Should().BeEmpty("Stop does not accept the settings confirmation");
        await screen.PressAsync(PhysicalKey.Escape);
        await OnUiAsync(() => screen.Shell.Settings.Question.Should().BeNull("Escape cancels while Stop has focus"));
        // A real pending hand edit drives both nested questions; no host settings are touched.
        Directory.CreateDirectory(Path.GetDirectoryName(harness.SettingsPath)!);
        await File.WriteAllTextAsync(harness.SettingsPath, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        await UiTask(() => screen.Shell.Settings.LoadAsync());
        await OnUiAsync(() => screen.Shell.Settings.ReviewHandEdit());
        await screen.RenderAsync("redesign-desktop-hand-edit", TestContext);
        await UiTask(() => screen.Shell.Settings.Question!.Answers[1].Act());
        await screen.RenderAsync("redesign-desktop-discard", TestContext);
        await OnUiAsync(() => screen.Shell.Settings.Question!.Title.Should().Be("Discard the hand edit"));
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.System));
        await UntilAsync(() => screen.Shell.System is { Busy: null, Health: not null }, "desktop system");
        await UiTask(() => screen.Shell.System.AskRestartAsync());
        await screen.MaskAsync(Environment.MachineName);
        await screen.RenderAsync("redesign-desktop-restart", TestContext);
        await OnUiAsync(() => screen.Shell.System.Question!.Lines.Should().Contain(RoofSystemText.RestartLoadsHandEdit));
        await harness.Console.LockAsync();
        await OnUiAsync(() =>
        {
            screen.Shell.Settings.Question.Should().BeNull();
            screen.Shell.System.Question.Should().BeNull();
            screen.Visible("nav-settings").Should().BeFalse();
            screen.Shell.Stop.IsEnabled.Should().BeTrue();
        });
        harness.Calls("Open").Should().Be(0);
        harness.Calls("Close").Should().Be(0);
    }

    [TestMethod]
    [DataRow("1200x1920", 90, 9.0, 1920, 1200)]
    [DataRow("720x1280", 270, 8.2, 1280, 720)]
    [DataRow("800x480", 0, 5.2, 800, 480)]
    public void ConfiguredModeRotationAndDensity_AreIndependent(string mode, int rotation, double density, int width, int height)
    {
        var options = new HVO.RoofControllerV4.Kiosk.KioskOptions { VideoMode = mode, Rotation = rotation, PixelsPerMillimetre = density, DeviceKeyFile = "fixture-key-file" };
        options.Validate().Should().BeEmpty();
        var drm = options.DrmOptions();
        var raw = drm.VideoMode!.Value;
        var effective = rotation is 90 or 270 ? new PixelSize(raw.Height, raw.Width) : raw;
        effective.Should().Be(new PixelSize(width, height));
        drm.Orientation.Should().Be(HVO.RoofControllerV4.Kiosk.KioskDisplay.Orientation(rotation));
        new KioskMetrics(density).Touch.Should().BeGreaterThanOrEqualTo(12 * density);
    }

    private static string Normalise(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static async Task UiTask(Func<Task> action)
    {
        var pending = await OnUiAsync(action);
        await UntilAsync(() => pending.IsCompleted, "native action");
        await pending;
    }

    private async Task<string> DrawAllPages(KioskScreen screen, string name)
    {
        await screen.DrawAsync();
        var pager = await OnUiAsync(() => screen.Window.GetVisualDescendants().OfType<KioskPager>().SingleOrDefault(p => p.IsEffectivelyVisible));
        if (pager is not null) await OnUiAsync(() => pager.Move(-pager.CurrentPage));
        var text = new List<string>();
        var count = await OnUiAsync(() => pager?.PageCount ?? 1);
        for (var i = 0; i < count; i++)
        {
            await screen.RenderAsync($"{name}-{i}", TestContext);
            await OnUiAsync(() =>
            {
                screen.OffScreen(_ => true).Should().BeEmpty("every visible named native control fits without page scrolling");
                var scope = pager is null ? screen.Window : screen.Find($"{pager.Name}-body")!;
                text.AddRange(scope.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!));
            });
            if (pager is not null && i + 1 < count)
            {
                var next = await OnUiAsync(() => pager.GetVisualDescendants().OfType<Button>().Single(b => b.Name == $"{pager.Name}-next"));
                await screen.TouchAsync(next);
            }
        }
        return string.Join(" ", text);
    }
}
