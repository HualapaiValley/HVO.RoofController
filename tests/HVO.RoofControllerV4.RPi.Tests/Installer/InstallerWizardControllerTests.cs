using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Wizard;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The wizard's controller questions and passwords (#69): the first admin, the camera (or a rig's HAT emulator),
/// telemetry and settings from a backup on a page of their own, passed over for roles without a controller; and the
/// passwords the plan needs, asked for after the review, masked and typed twice, and never shown or kept.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerWizardControllerTests
{
    private const string Password = "a long test password";
    private const string Pin = "9081726354";
    private const string CameraPassword = "camera test password";
    private const string Backup = "/root/backup/appsettings.Local.json";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheControllerPage_AsksForTheFirstAdmin_TheCamera_Telemetry_AndABackup()
    {
        using var pi = InstallerWizardTests.AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(3);
        var page = (ControllerPage)wizard.Page;

        wizard.Wizard.Header.Should().Be("Step 4 of 7: The controller");
        page.AdminName!.Text.Should().BeEmpty("nobody is added unasked");
        page.CameraServer!.Text.Should().BeEmpty();
        page.TimeScale.Should().BeNull("a controller shows the Blue Iris camera, not an emulator's");
        wizard.Screen.Should().Contain("The first admin, who signs in to the web UI and adds everyone else")
            .And.Contain("The camera the controller shows (Blue Iris): empty leaves it as it is")
            .And.Contain("Telemetry, exported over OTLP/HTTP: empty turns the export off")
            .And.Contain("Settings from a backup, for a controller that has none yet")
            .And.Contain("Earlier versions held a Blue Iris credential in their source");
        InstallerWizardTests.ShouldHaveColours(wizard.ColoursOf("Earlier versions held"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);

        page.AdminName.Text = "roy";
        page.AdminPin!.Value = CheckState.Checked;
        page.CameraUser!.Text = "roof-viewer";
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(page);
        wizard.Wizard.Message.Should().Be("The camera's user needs its server: give the Blue Iris server's address, or clear the user.");

        page.CameraServer.Text = "http://192.168.0.4:81";
        page.Telemetry!.Text = "http://collector:4318";
        page.ImportFrom!.Text = "backup/appsettings.Local.json";
        wizard.Wizard.Message.Should().BeEmpty("the answer it was about has changed");
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The settings to import (backup/appsettings.Local.json) must be given by their full path (/home/pi/backup/appsettings.Local.json).");

        page.ImportFrom.Text = Backup;
        wizard.Render(TestContext, "4-controller");
        wizard.NextTo(4);
        wizard.Session.Answers.Controller.Should().BeEquivalentTo(new ControllerSettings
        {
            FirstAdmin = new FirstAdminSettings { Name = "roy", Pin = true },
            Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81", UserName = "roof-viewer" },
            TelemetryEndpoint = "http://collector:4318",
            ImportSettingsFrom = Backup
        });

        wizard.Press(Key.Esc);
        wizard.Page.Should().BeSameAs(page, "Back goes to the controller's page, with its answers");
        page.AdminName.Text.Should().Be("roy");
        page.Describe().Should().Equal(
            "First admin: roy, with a PIN",
            "Camera: http://192.168.0.4:81 as roof-viewer",
            "Telemetry: http://collector:4318",
            $"Settings from a backup: {Backup}");
    }

    [TestMethod]
    public async Task ATestRigsPage_AsksForTheEmulatorsPace_AndWhetherOtherMachinesMayUseIt()
    {
        using var bench = new FakeMachine().WithPi(i2c: false);
        using var wizard = await WizardDriver.StartAsync(bench);
        wizard.NextTo(1);
        ((RolesPage)wizard.Page).Choices[InstallRole.Rig].Value = CheckState.Checked;
        wizard.NextTo(3);
        var page = (ControllerPage)wizard.Page;

        page.CameraServer.Should().BeNull("a rig shows the HAT emulator's camera");
        page.TimeScale!.Text.Should().Be("1");
        page.FramesPerSecond!.Text.Should().Be("5");
        page.OpenToLan!.Value.Should().Be(CheckState.UnChecked, "a rig is kept to this machine unless it is opened");
        wizard.Screen.Should().Contain("The HAT emulator")
            .And.Contain("times as fast as real time (0.1 to 100)")
            .And.Contain("Open to the network: other machines may use the rig (HTTPS only)")
            .And.NotContain("Blue Iris");
        wizard.Render(TestContext, "4-rig");

        page.TimeScale.Text = "fast";
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The rig's time scale must be a number, not 'fast'.");

        page.TimeScale.Text = "500";
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The rig's time scale must be from 0.1 to 100, not 500.");

        page.TimeScale.Text = "10";
        page.FramesPerSecond.Text = "2.5";
        page.OpenToLan.Value = CheckState.Checked;
        wizard.NextTo(4);
        wizard.Session.Answers.Controller!.Rig.Should().Be(new RigSettings { TimeScale = 10, CameraFramesPerSecond = 2.5, OpenToLan = true });
        wizard.Session.Answers.Controller.Camera.Should().BeNull();
    }

    [TestMethod]
    public async Task APageWithNothingToAsk_IsPassedOver_AndNotCounted()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop");
        using var wizard = await WizardDriver.StartAsync(laptop);
        wizard.Wizard.Header.Should().Be("Step 1 of 6: This machine", "no role is chosen yet");
        wizard.NextTo(1);
        ((RolesPage)wizard.Page).Choices[InstallRole.Cli].Value = CheckState.Checked;
        wizard.NextTo(2);
        wizard.Wizard.Header.Should().Be("Step 3 of 6: Choices");
        wizard.Page.MostFocused!.SuperView.Should().BeOfType<OptionSelector>("the folder is the page's one question");

        // Enter is Next, even from an option list, which would take it for itself.
        wizard.Press(Key.Enter);
        wizard.WaitIdle("the plan's check");

        wizard.Page.Should().BeOfType<ReviewPage>("hvo-roof has no controller to ask about");
        wizard.Wizard.Header.Should().Be("Step 4 of 6: Review the plan");
        wizard.Wizard.NextButton.Text.Should().Be("Install", "hvo-roof needs no password");
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeOfType<SettingsPage>("Back passes over it too");
    }

    [TestMethod]
    public async Task ThePasswordsPage_AsksForWhatThePlanNeeds_TypedTwice_AndNeverShowsOrKeepsThem()
    {
        using var pi = InstallerWizardTests.AdoptablePi();
        var log = InstallLog.Open(pi.Machine, InstallPaths.SystemLog, TimeProvider.System);
        using var wizard = await WizardDriver.StartAsync(pi, log);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(3);
        var controller = (ControllerPage)wizard.Page;
        controller.AdminName!.Text = "roy";
        controller.AdminPin!.Value = CheckState.Checked;
        controller.CameraServer!.Text = "http://192.168.0.4:81";
        controller.CameraUser!.Text = "roof-viewer";
        wizard.NextTo(4);

        var review = (ReviewPage)wizard.Page;
        review.Plan!.IsBlocked.Should().BeFalse(string.Join('\n', review.Describe()));
        wizard.Wizard.Header.Should().Be("Step 5 of 8: Review the plan", "the plan needs passwords, which have a page of their own");
        wizard.Wizard.NextButton.Text.Should().Be("Next", "the passwords come before Install");

        wizard.NextTo(5);
        var passwords = (PasswordsPage)wizard.Page;
        wizard.Wizard.Header.Should().Be("Step 6 of 8: Passwords");
        wizard.Wizard.NextButton.Text.Should().Be("Install");
        passwords.Fields.Select(field => field.Secret).Should().Equal(InstallSecret.AdminPassword, InstallSecret.AdminPin, InstallSecret.CameraPassword);
        passwords.Fields.Should().OnlyContain(field => field.Typed.Secret && field.Again.Secret, "what is typed is masked");
        wizard.Screen.Should().Contain("Each is typed twice, and never shown, saved or logged.")
            .And.Contain("The first admin's password:")
            .And.Contain(RoofIdentityText.PasswordRule)
            .And.Contain(RoofIdentityText.PinRule)
            .And.Contain("The camera's password:");

        // Typed as a person types them.
        passwords.Fields[0].Typed.SetFocus();
        wizard.Type("too short");
        passwords.Fields[0].Again.SetFocus();
        wizard.Type("too short");
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(passwords);
        wizard.Wizard.Message.Should().Be($"The first admin's password: {RoofIdentityText.PasswordRule}");

        passwords.Fields[0].Typed.Text = passwords.Fields[0].Again.Text = Password;
        passwords.Fields[1].Typed.Text = Pin;
        passwords.Fields[1].Again.Text = "1234567890";
        passwords.Fields[2].Typed.Text = passwords.Fields[2].Again.Text = CameraPassword;
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The first admin's PIN: the two differ. Type it again in both.");
        wizard.Screen.Should().NotContain(Password).And.NotContain(Pin).And.NotContain(CameraPassword).And.Contain("∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙∙", "what is typed shows only as dots");
        wizard.Render(TestContext, "6-passwords");
        pi.UserRequests.Should().BeEmpty("nothing is installed before Install");

        passwords.Fields[1].Again.Text = Pin;
        wizard.Press(Key.Enter);
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Success, wizard.Wizard.Failure);
        wizard.Wizard.Header.Should().Be("Step 8 of 8: Done");
        pi.UserRequests.Should().ContainSingle().Which.Should().Match<FakeUserRequest>(request => request.Name == "roy" && request.Password == Password && request.Pin == Pin);
        pi.Read($"/etc/hvo-roof/secrets/{CameraSteps.PasswordSetting}").Should().Be(CameraPassword);
        passwords.Fields.Should().OnlyContain(field => field.Typed.Text.Length == 0 && field.Again.Text.Length == 0, "what was typed is not kept");
        passwords.Describe().Should().NotContain(line => line.Contains(Password) || line.Contains(Pin) || line.Contains(CameraPassword));
        foreach (var secret in new[] { Password, Pin, CameraPassword })
        {
            wizard.Screen.Should().NotContain(secret);
            pi.Read(InstallPaths.SystemLog).Should().NotContain(secret);
            pi.Read(InstallPaths.SystemRecord).Should().NotContain(secret);
        }
    }

    [TestMethod]
    public async Task TheControllersQuestions_AndThePasswords_FitAn80By24Terminal()
    {
        using var pi = InstallerWizardTests.AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi, width: 80, height: 24);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(3);
        var controller = (ControllerPage)wizard.Page;
        wizard.Rows.Should().Contain(row => row.Contains("Full path:", StringComparison.Ordinal), "the page's last question is shown")
            .And.Contain(row => row.Contains("(docs/security.md).", StringComparison.Ordinal), "the camera's reminder is shown whole");

        controller.AdminName!.Text = "roy";
        controller.AdminPin!.Value = CheckState.Checked;
        controller.CameraServer!.Text = "http://192.168.0.4:81";
        controller.CameraUser!.Text = "roof-viewer";
        wizard.NextTo(5);

        wizard.Page.Should().BeOfType<PasswordsPage>();
        wizard.Rows.Where(row => row.Contains("Again:", StringComparison.Ordinal)).Should().HaveCount(3, "each password's second field is shown");
        wizard.Rows.Should().Contain(row => row.Contains("► Install ◄", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ATestRigsQuestions_FitAn80By24Terminal()
    {
        using var bench = new FakeMachine().WithPi(i2c: false);
        using var wizard = await WizardDriver.StartAsync(bench, width: 80, height: 24);
        wizard.NextTo(1);
        ((RolesPage)wizard.Page).Choices[InstallRole.Rig].Value = CheckState.Checked;
        wizard.NextTo(3);

        wizard.Rows.Should().Contain(row => row.Contains("Open to the network", StringComparison.Ordinal))
            .And.Contain(row => row.Contains("Full path:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PasswordsGivenInFiles_AreNotAskedFor()
    {
        using var pi = InstallerWizardTests.AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.Session.GiveSecret(InstallSecret.AdminPassword, Password);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(3);
        ((ControllerPage)wizard.Page).AdminName!.Text = "roy";
        wizard.NextTo(4);

        wizard.Wizard.NextButton.Text.Should().Be("Install", "the only password the plan needs was given");
        wizard.Wizard.Header.Should().Be("Step 5 of 7: Review the plan");
    }
}
