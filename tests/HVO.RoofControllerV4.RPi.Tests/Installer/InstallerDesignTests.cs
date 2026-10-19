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
                    driver.Screen.Should().NotContainAny("┌", "─", "│", "╭", "╰", "⟦", "⟧", "☑", "☐", "◉", "○");
                if (mode.StartsWith("nocolor", StringComparison.Ordinal))
                    driver.DrawnAttributes().Should().OnlyContain(a => a.Foreground == Color.None && a.Background == Color.None);
                driver.Render(TestContext, tag + "-" + step);
            }
        }
    }

    private static IEnumerable<Terminal.Gui.ViewBase.View> Descendants(Terminal.Gui.ViewBase.View root)
        => root.SubViews.SelectMany(view => new[] { view }.Concat(Descendants(view)));
}
