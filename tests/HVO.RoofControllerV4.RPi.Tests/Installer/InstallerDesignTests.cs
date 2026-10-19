using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Wizard;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Every conditional form family on fake machines, at both supported cell sizes and in every protocol.</summary>
[TestClass]
[DoNotParallelize]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerDesignTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("truecolor,unicode", 80, 24)]
    [DataRow("ansi16,unicode", 80, 24)]
    [DataRow("nocolor,unicode", 80, 24)]
    [DataRow("ansi16,ascii", 80, 24)]
    [DataRow("truecolor,unicode", 120, 32)]
    [DataRow("ansi16,unicode", 120, 32)]
    [DataRow("nocolor,unicode", 120, 32)]
    [DataRow("nocolor,ascii", 120, 32)]
    public async Task AllConditionalForms_AreBoundedAndKeepCancellationDistinct(string mode, int width, int height)
    {
        foreach (var profile in new[] { "controller-kiosk", "rig", "mac-clients" })
        {
            using var machine = profile == "mac-clients"
                ? new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "fixture-mac", userName: "fixture")
                : profile == "rig" ? new FakeMachine().WithPi(i2c: false) : InstallerWizardTests.AdoptablePi();
            machine.Environment["HVO_TERMINAL"] = mode;
            using var driver = await WizardDriver.StartAsync(machine, width: width, height: height);
            var tag = $"176-{profile}-{mode.Replace(',', '-')}-{width}x{height}";
            Capture("machine");
            driver.Wizard.ShowPage(1);
            Capture("roles");
            driver.Session.Answers = new InstallAnswers
            {
                Roles = profile == "controller-kiosk" ? [InstallRole.Controller, InstallRole.Kiosk]
                    : profile == "rig" ? [InstallRole.Rig] : [InstallRole.Cli, InstallRole.MacApp],
                Client = profile == "mac-clients" ? FakeMachine.ClientAnswers : null,
                MacApp = profile == "mac-clients" ? new MacAppSettings { Admin = "fixture-admin" } : null
            }.Normalised();
            foreach (var index in new[] { 2, 3, 4, 5 })
            {
                if (!driver.Wizard.Pages[index].Applies) continue;
                driver.Wizard.ShowPage(index);
                driver.WaitIdle("conditional form");
                Capture($"step{index}");
                // Tab must reveal every ordinary input, including fields after long trust/safety explanations.
                foreach (var field in Descendants(driver.Page).OfType<TextField>().Where(field => field.Visible && field.Enabled))
                {
                    field.SetFocus();
                    _ = driver.Screen;
                    var at = field.FrameToScreen();
                    at.Y.Should().BeGreaterThan(1).And.BeLessThan(driver.Wizard.BackButton.FrameToScreen().Y);
                }
            }
            if (driver.PageOf<ReviewPage>().Plan is not null && driver.PageOf<PasswordsPage>().Applies)
            {
                driver.Wizard.ShowPage(6);
                var fields = driver.PageOf<PasswordsPage>().Fields;
                foreach (var (_, typed, again) in fields)
                {
                    typed.Secret.Should().BeTrue();
                    typed.Text = string.Empty;
                    if (again is not null) { again.Secret.Should().BeTrue(); again.Text = "different-private-fixture"; }
                }
                driver.Screen.Should().NotContain("invalid-private-fixture").And.NotContain("different-private-fixture");
                driver.Press(Key.Enter);
                driver.Wizard.PageIndex.Should().Be(6, "invalid private input must never begin apply");
                driver.Wizard.Message.Should().NotBeEmpty();
                Capture("private-error");
            }
            driver.Wizard.Failure = "[ERROR] Controlled step failure. No later step was changed. Correct the problem and retry.";
            driver.Wizard.Result = InstallerExitCode.Failed;
            driver.Wizard.ShowPage(8);
            Capture("failure-recovery");
            driver.Screen.Should().NotContain("Stop roof", "the installer is not a motion interface");
            driver.Press(Key.F10);
            driver.Closed.Should().BeTrue();
            machine.Unexpected.Should().BeEmpty();

            void Capture(string step)
            {
                driver.Screen.Should().Contain("F10");
                if (mode.EndsWith("ascii", StringComparison.Ordinal))
                    driver.Screen.Should().Contain("+--").And.NotContainAny("┌", "─", "│", "╭", "╰", "⟦", "⟧", "☑", "☐", "◉", "○", "▲", "▼", "░", "▒", "▓", "█", "•", "∙");
                if (mode.StartsWith("nocolor", StringComparison.Ordinal))
                    driver.DrawnAttributes().Should().OnlyContain(a => a.Foreground == Color.None && a.Background == Color.None);
                driver.Render(TestContext, tag + "-" + step);
            }
        }
    }

    [TestMethod]
    [DataRow("truecolor,unicode", 80, 24)]
    [DataRow("ansi16,unicode", 80, 24)]
    [DataRow("nocolor,unicode", 80, 24)]
    [DataRow("ansi16,ascii", 80, 24)]
    [DataRow("truecolor,unicode", 120, 32)]
    [DataRow("ansi16,unicode", 120, 32)]
    [DataRow("nocolor,unicode", 120, 32)]
    [DataRow("ansi16,ascii", 120, 32)]
    public async Task ClientCaAction_IsFullyVisibleFocusableAndHitTestable(string mode, int width, int height)
    {
        using var machine = new FakeMachine(InstallerOs.Linux, Architecture.X64, root: false);
        machine.Environment["HVO_TERMINAL"] = mode;
        using var driver = await WizardDriver.StartAsync(machine, width: width, height: height);
        driver.Session.Answers = new InstallAnswers { Roles = [InstallRole.Cli], Client = FakeMachine.ClientAnswers };
        driver.Wizard.ShowPage(4);
        driver.WaitIdle("client form");
        var page = driver.PageOf<ClientPage>();
        page.Address!.SetFocus();
        driver.Press(Key.Tab);
        var fetch = page.FetchButton!;
        fetch.HasFocus.Should().BeTrue("Tab from the address reaches its action");
        driver.Screen.Should().Contain("[ Fetch its CA ]");
        foreach (var label in page.Trust!.Labels!) driver.Screen.Should().Contain(label, "every trust choice is readable at the minimum width");
        var bounds = fetch.FrameToScreen();
        var pageBounds = page.FrameToScreen();
        bounds.X.Should().BeGreaterThanOrEqualTo(pageBounds.X);
        bounds.Right.Should().BeLessThanOrEqualTo(pageBounds.X + page.Viewport.Width);
        bounds.Y.Should().BeGreaterThanOrEqualTo(pageBounds.Y);
        bounds.Bottom.Should().BeLessThanOrEqualTo(driver.Wizard.BackButton.FrameToScreen().Y);
        driver.Wizard.Window.GetViewsUnderLocation(bounds.Location, Terminal.Gui.ViewBase.ViewportSettingsFlags.None).Should().Contain(fetch);
        var last = new System.Drawing.Point(bounds.Right - 1, bounds.Y);
        driver.Wizard.Window.GetViewsUnderLocation(last, Terminal.Gui.ViewBase.ViewportSettingsFlags.None).Should().Contain(fetch);
        driver.Press(Key.Enter);
        driver.WaitIdle("fake CA fetch", () => machine.CaFetches.Count == 1);
        page.Matches!.Enabled.Should().BeTrue();
        page.Matches.Value.Should().Be(CheckState.UnChecked);
        machine.Unexpected.Should().BeEmpty();
        driver.Render(TestContext, $"176-ca-action-{mode.Replace(',', '-')}-{width}x{height}");
    }

    [TestMethod]
    [DataRow("truecolor,unicode", 80, 24)]
    [DataRow("ansi16,unicode", 80, 24)]
    [DataRow("nocolor,unicode", 80, 24)]
    [DataRow("ansi16,ascii", 80, 24)]
    [DataRow("truecolor,unicode", 120, 32)]
    [DataRow("ansi16,unicode", 120, 32)]
    [DataRow("nocolor,unicode", 120, 32)]
    [DataRow("ansi16,ascii", 120, 32)]
    public async Task LongFirstAdminValidation_LeavesActionsVisible_AndCompleteCorrectionReachable(string mode, int width, int height)
    {
        using var machine = InstallerWizardTests.AdoptablePi();
        machine.Environment["HVO_TERMINAL"] = mode;
        using var driver = await WizardDriver.StartAsync(machine, width: width, height: height);
        driver.Session.Answers = new InstallAnswers { Roles = [InstallRole.Controller] };
        driver.Wizard.ShowPage(3);
        var page = driver.PageOf<ControllerPage>();
        page.AdminName!.Text = new string('x', 2000) + "!";
        driver.Wizard.NextButton.SetFocus();
        driver.Press(Key.Enter);
        driver.Wizard.Page.Should().BeSameAs(page);
        driver.Wizard.Message.Should().Contain("starting with a letter or digit.");
        var error = driver.Wizard.Window.SubViews.OfType<TerminalTextView>().Single();
        var nextLabel = mode.EndsWith(",ascii", StringComparison.Ordinal) ? "[ > Next < ]" : "[ ► Next ◄ ]";
        driver.Screen.Should().Contain("[ Back ]").And.Contain(nextLabel).And.Contain("F10");
        var nextBounds = driver.Wizard.NextButton.FrameToScreen();
        nextBounds.Right.Should().BeLessThan(width);
        nextBounds.Bottom.Should().BeLessThan(height);
        driver.Wizard.Window.GetViewsUnderLocation(nextBounds.Location, Terminal.Gui.ViewBase.ViewportSettingsFlags.None).Should().Contain(driver.Wizard.NextButton);
        error.FrameToScreen().Bottom.Should().BeLessThanOrEqualTo(driver.Wizard.BackButton.FrameToScreen().Y);
        for (var count = 0; count < 16 && !error.HasFocus; count++) driver.Press(Key.Tab);
        error.HasFocus.Should().BeTrue("Tab reaches the capped validation reader");
        driver.Press(Key.End);
        driver.Screen.Should().Contain("starting with a letter or digit.").And.Contain("[ Back ]").And.Contain(nextLabel).And.Contain("F10");
        driver.Render(TestContext, $"176-long-installer-validation-{mode.Replace(',', '-')}-{width}x{height}");
        driver.Press(Key.Esc);
        driver.Wizard.Page.Should().NotBeSameAs(page, "Esc remains Back while reading validation");
        driver.Press(Key.F10);
        driver.Wizard.Result.Should().Be(InstallerExitCode.Cancelled);
        driver.Closed.Should().BeTrue();
        machine.Unexpected.Should().BeEmpty();
    }

    private static IEnumerable<Terminal.Gui.ViewBase.View> Descendants(Terminal.Gui.ViewBase.View root)
        => root.SubViews.SelectMany(view => new[] { view }.Concat(Descendants(view)));
}
