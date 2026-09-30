using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.Installer.Wizard;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The installer's wizard (#67), driven with keys through Terminal.Gui's ANSI driver on fake machines. Each page is also
/// saved as ANSI (<see cref="WizardDriver.Render"/>): CI draws them as the installer's screenshots.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerWizardTests
{
    private const string Secret = "not-a-real-secret-wizard";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheMachinePage_ShowsWhatTheInstallerFound_InHvoDark()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);

        wizard.Page.Should().BeOfType<MachinePage>();
        var screen = wizard.Screen;
        screen.Should().Contain("HVO Roof installer 4.0.0", "the title names the release; its commit is in --version and the log")
            .And.NotContain("0123456789abcdef");
        screen.Should().Contain("Step 1 of 6: This machine")
            .And.Contain("Machine:     roofpi (linux-arm64, Debian GNU/Linux 12 (bookworm))")
            .And.Contain("Running as:  root")
            .And.Contain("HAT devices: /dev/i2c-1, /dev/gpiomem, the thermal sensor present")
            .And.Contain("Container:   roof-controller: running, 4.0.0, the real HAT (made by the deploy script)")
            .And.NotContain(Secret);
        wizard.Page.Describe().Should().Equal(InstallerSession.DescribeSurvey(wizard.Session.Survey, wizard.Session.Time.GetUtcNow()));
        wizard.Wizard.BackButton.Enabled.Should().BeFalse();
        wizard.Wizard.NextButton.Enabled.Should().BeTrue();

        ShouldHaveColours(wizard.ColoursOf("Step 1 of 6"), RoofUiPalette.Text, RoofUiPalette.Surface);
        ShouldHaveColours(wizard.ColoursOf("Machine:"), RoofUiPalette.Text, RoofUiPalette.Background);
        ShouldHaveColours(wizard.ColoursOf("F10"), RoofUiPalette.Accent, RoofUiPalette.Badge);
        ShouldHaveColours(wizard.ColoursOf("Quit"), RoofUiPalette.Muted, RoofUiPalette.Badge);
        wizard.Render(TestContext, "1-machine");
    }

    [TestMethod]
    public async Task AMachineTheInstallerDoesNotRunOn_SaysWhy_AndGoesNoFurther()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.X64, root: false, hostName: "old-mac", userName: "roy");
        using var wizard = await WizardDriver.StartAsync(mac);

        var problem = RoleGuards.PlatformProblem(wizard.Session.Survey);
        problem.Should().NotBeNull();
        wizard.Page.Describe()[0].Should().Be(problem);
        wizard.Wizard.NextButton.Enabled.Should().BeFalse();
        ShouldHaveColours(wizard.ColoursOf(problem![..20]), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);

        wizard.Press(Key.Enter);

        wizard.Page.Should().BeOfType<MachinePage>();
    }

    [TestMethod]
    public async Task Esc_GoesBackAPage_AndNeverCloses_WhileF10Quits()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Page.Should().BeOfType<RolesPage>();

        wizard.Press(Key.Esc);
        wizard.Page.Should().BeOfType<MachinePage>();
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeOfType<MachinePage>();
        wizard.Closed.Should().BeFalse("Esc never closes the installer");

        wizard.Press(Key.F10);

        wizard.Closed.Should().BeTrue();
        wizard.Wizard.Result.Should().Be(InstallerExitCode.Cancelled);
    }

    [TestMethod]
    public async Task TheRolesPage_OffersWhatThisMachineCanHave_AndSaysWhyNotTheRest()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        var roles = (RolesPage)wizard.Page;

        roles.Choices[InstallRole.Controller].Enabled.Should().BeTrue();
        roles.Choices[InstallRole.Rig].Enabled.Should().BeFalse();
        roles.Choices[InstallRole.MacApp].Enabled.Should().BeFalse();
        roles.Confirmation.Visible.Should().BeFalse();
        wizard.Screen.Should().Contain("The roof-controller container on this machine drives the real HAT: a test rig is never");
        ShouldHaveColours(wizard.ColoursOf("The roof-controller container on"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);

        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("Choose at least one role.");
        ShouldHaveColours(wizard.ColoursOf("Choose at least one role."), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);

        wizard.Press(Key.Space);
        roles.Chosen.Should().Equal(InstallRole.Controller);
        wizard.Wizard.Message.Should().BeEmpty("a role is chosen now");
        wizard.Render(TestContext, "2-roles");
        wizard.NextTo(2);
        wizard.Session.Answers.Roles.Should().Equal(InstallRole.Controller);
    }

    [TestMethod]
    public async Task AMachinesRolesAndThePersons_AreInstalledInSeparateRuns()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        var roles = (RolesPage)wizard.Page;
        roles.Choices[InstallRole.Controller].Value = CheckState.Checked;
        roles.Choices[InstallRole.Cli].Value = CheckState.Checked;

        wizard.Press(Key.Enter);

        wizard.Page.Should().BeSameAs(roles);
        wizard.Wizard.Message.Should().Contain("Install them in separate runs.");
    }

    [TestMethod]
    public async Task ATestRigOnAMachineWithI2c_IsInstalledOnlyWithItsHostNameTyped_WhichIsNeverSaved()
    {
        using var pi = new FakeMachine().WithPi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        var roles = (RolesPage)wizard.Page;

        wizard.Press(Key.CursorDown);
        wizard.Press(Key.Space);

        roles.Chosen.Should().Equal(InstallRole.Rig);
        roles.Confirmation.Visible.Should().BeTrue();
        wizard.Screen.Should().Contain("Type this machine's host name (roofpi) to confirm it is not.");
        ShouldHaveColours(wizard.ColoursOf("This machine has the HAT's I2C bus"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);

        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().StartWith("Confirm the test rig: this machine has the HAT's I2C bus.");

        roles.Confirmation.SetFocus();
        wizard.Type("benchpi");
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(roles);
        wizard.Wizard.Message.Should().Be("The confirmation does not match: type this machine's host name, roofpi.");

        roles.Confirmation.Text = string.Empty;
        wizard.Wizard.Message.Should().BeEmpty("the confirmation it was about is gone");
        wizard.Type("roofpi");
        wizard.Render(TestContext, "2-rig-confirmation");
        wizard.NextTo(2);
        wizard.Session.Answers.RigConfirmation.Should().Be("roofpi");

        wizard.NextTo(4);
        var review = (ReviewPage)wizard.Page;
        review.SavePath.SetFocus();
        wizard.Press(Key.Enter);
        var saved = pi.Read("/root/hvo-roof-answers.json");
        saved.Should().NotContain("rigConfirmation", "the confirmation is typed on the machine, each time").And.NotContain("roofpi");
        InstallAnswers.Parse(saved).Roles.Should().Equal(InstallRole.Rig);
    }

    [TestMethod]
    public async Task TheSettingsPage_AsksTheControllersQuestions_AndChecksTheAnswers()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(2);
        var settings = (SettingsPage)wizard.Page;

        settings.Connection!.Value.Should().Be(0, "the installer's CA is the default");
        settings.HttpsPort!.Text.Should().Be("8443");
        settings.HttpPort!.Text.Should().Be("8080");
        settings.WebPort!.Text.Should().Be("8088");
        settings.CliFolder.Should().BeNull("hvo-roof was not chosen");
        wizard.Screen.Should().Contain("HTTPS, with a certificate from this installer's certificate authority (recommended)");
        ShouldHaveColours(wizard.ColoursOf("How the controller is reached"), RoofUiPalette.Text, RoofUiPalette.Surface);
        wizard.Render(TestContext, "3-settings");

        settings.HttpsPort.Text = "x";
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(settings);
        wizard.Wizard.Message.Should().Be("The HTTPS port must be a number from 1 to 65535, not 'x'.");

        settings.HttpsPort.Text = "8088";
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The web UI needs a port of its own: 8088 is the controller's API's port too.");

        settings.HttpsPort.Text = "9443";
        wizard.Wizard.Message.Should().BeEmpty("the port it was about has changed");
        wizard.NextTo(4);
        wizard.Session.Answers.Controller.Should().Be(new ControllerSettings { HttpsPort = 9443 });
    }

    [TestMethod]
    public async Task TheSettingsPage_AsksForTheNamesTheCertificateIsFor()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(2);
        var settings = (SettingsPage)wizard.Page;

        settings.HostNames!.Text.Should().BeEmpty();
        settings.Domains!.Text.Should().BeEmpty("the machine's resolver searches no domain");
        wizard.Screen.Should().Contain("Names clients use for it")
            .And.Contain("Other host names:")
            .And.Contain("Domains:")
            .And.Contain("Separate them with spaces. The controller answers to roofpi and each of these, alone, under")
            .And.Contain(".local and under each domain, and its certificate is for them all.")
            .And.NotContain("This machine is in");
        ShouldHaveColours(wizard.ColoursOf("Names clients use for it"), RoofUiPalette.Text, RoofUiPalette.Surface);

        settings.HostNames.Text = "roof roof.observatory.example";
        settings.Domains.Text = "observatory..example";
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(settings);
        wizard.Wizard.Message.Should().Be(
            "'roof.observatory.example' is not a host name: give a single name of letters, digits and hyphens (roof), not an address or a name with dots. "
            + "'observatory..example' is not a domain: give names of letters, digits and hyphens joined by dots (observatory.example).");

        // Names as a person types them: commas or spaces, any case, each once.
        settings.HostNames.Text = "Roof, roof dome";
        settings.Domains.Text = "Observatory.Example.";
        wizard.NextTo(4);
        wizard.Session.Answers.Controller!.HostNames.Should().Equal("roof", "dome");
        wizard.Session.Answers.Controller.Domains.Should().Equal("observatory.example");

        wizard.Press(Key.Esc);
        wizard.Page.Should().BeOfType<ControllerPage>();
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeSameAs(settings);
        settings.HostNames.Text.Should().Be("roof dome");
        settings.Domains.Text.Should().Be("observatory.example");
        settings.Describe().Should().Contain("Names: roof, dome; domains: observatory.example");
    }

    [TestMethod]
    public async Task TheSettingsPage_SuggestsTheMachinesDomains_WithoutListingThem()
    {
        using var pi = AdoptablePi().Write(CertificateNames.ResolverConfiguration, "search observatory.example\n");
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(2);
        var settings = (SettingsPage)wizard.Page;

        settings.Domains!.Text.Should().BeEmpty("each domain listed lets the CA sign for any name in it, so none is listed unasked");
        wizard.Screen.Should().Contain("This machine is in").And.Contain("observatory.example: add a domain only if clients use names in it.");

        wizard.NextTo(4);
        wizard.Session.Answers.Controller!.Domains.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheSettingsPage_AsksForAConfirmation_BeforePlainHttp()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(2);
        var settings = (SettingsPage)wizard.Page;
        settings.HttpConfirmation!.Visible.Should().BeFalse("HTTPS is chosen");
        wizard.Screen.Should().NotContain("Type http");

        settings.Connection!.Value = Array.IndexOf(Enum.GetValues<ConnectionMode>(), ConnectionMode.Http);
        wizard.Pump();
        settings.HttpConfirmation.Visible.Should().BeTrue();
        settings.Describe()[^1].Should().Be(RoleGuards.HttpConfirmationPrompt);
        wizard.Screen.Should().Contain("Over HTTP, API keys, session tokens and PINs cross the network unencrypted");
        ShouldHaveColours(wizard.ColoursOf("Over HTTP"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);
        wizard.Screen.Should().Contain(".local and under each domain.").And.NotContain("certificate is for", "there is no certificate");

        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(settings);
        wizard.Wizard.Message.Should().Be(
            "Confirm plain HTTP: API keys, session tokens and PINs would cross the network unencrypted. Type http to confirm (httpConfirmation in an answers file), or choose private-ca.");

        settings.HttpConfirmation.Text = "yes";
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("The confirmation does not match: type http to serve plain HTTP, or choose private-ca.");

        settings.HttpConfirmation.Text = "http";
        wizard.Render(TestContext, "3-settings-http");
        wizard.NextTo(4);
        wizard.Session.Answers.Controller!.Connection.Should().Be(ConnectionMode.Http);
        wizard.Session.Answers.HttpConfirmation.Should().Be("http");

        // Back to HTTPS: the confirmation is no longer asked for, or kept.
        wizard.Press(Key.Esc);
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeSameAs(settings);
        settings.Connection.Value = Array.IndexOf(Enum.GetValues<ConnectionMode>(), ConnectionMode.PrivateCa);
        wizard.Pump();
        settings.HttpConfirmation.Visible.Should().BeFalse();
        wizard.NextTo(4);
        wizard.Session.Answers.HttpConfirmation.Should().BeNull();
    }

    [TestMethod]
    public async Task TheReviewPage_ChecksThePlan_BeforeInstallCanBePressed()
    {
        using var pi = AdoptablePi();
        using var held = new HeldInspect(pi);
        using var wizard = await WizardDriver.StartAsync(pi, commands: held);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(3);

        held.Hold();
        wizard.Press(Key.Enter);

        wizard.Page.Should().BeOfType<ReviewPage>();
        wizard.Screen.Should().Contain("Checking this machine against the plan…");
        wizard.Wizard.NextButton.Text.Should().Be("Install");
        wizard.Wizard.EnterName.Should().Be("Install", "the key bar says what Enter does");
        wizard.Wizard.NextButton.Enabled.Should().BeFalse();
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeOfType<ReviewPage>("Install waits for the check");
        wizard.Wizard.BackButton.Enabled.Should().BeFalse("Back waits for the check too, so Install carries out the plan checked for the answers shown");
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeOfType<ReviewPage>();
        ShouldHaveColours(wizard.ColoursOf("Esc Back"), RoofUiPalette.MutedWeak, RoofUiPalette.Badge, "the key bar dims a key that does nothing");
        ShouldHaveColours(wizard.ColoursOf("Enter Install"), RoofUiPalette.MutedWeak, RoofUiPalette.Badge);

        held.Release();
        wizard.WaitIdle("the plan's check");
        wizard.Wizard.BackButton.Enabled.Should().BeTrue();
        ShouldHaveColours(wizard.ColoursOf("Esc Back"), RoofUiPalette.Accent, RoofUiPalette.Badge);
        ShouldHaveColours(wizard.ColoursOf("Enter Install"), RoofUiPalette.Accent, RoofUiPalette.Badge);

        var review = (ReviewPage)wizard.Page;
        review.Plan.Should().NotBeNull();
        review.Plan!.IsBlocked.Should().BeFalse();
        wizard.Wizard.NextButton.Enabled.Should().BeTrue();
        review.Describe().Should().Equal(
            ["Installing the controller on roofpi, 4.0.0:", string.Empty, .. HVO.RoofControllerV4.Installer.Plan.PlanText.Lines(review.Plan)]);
        var screen = wizard.Screen;
        screen.Should().Contain("Installing the controller on roofpi, 4.0.0:").And.Contain("  unchanged  /etc/hvo-roof ");
        screen.Should().Contain("Save these answers (no secrets) to:").And.Contain("/root/hvo-roof-answers.json");
        wizard.Render(TestContext, "5-review");
    }

    [TestMethod]
    public async Task TheReviewPage_SavesTheAnswers_WithNoSecret_AndInstallsNothing()
    {
        using var pi = AdoptablePi(Secret);
        var before = pi.Snapshot();
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(4);
        var review = (ReviewPage)wizard.Page;
        pi.Snapshot().Should().Equal(before, "nothing is changed before Install");

        review.SavePath.SetFocus();
        wizard.Press(Key.Enter);

        wizard.Page.Should().BeSameAs(review, "Enter in the path saves; it does not install");
        wizard.Wizard.Message.Should().Be("Saved: install the same way with hvo-roof-install --answers /root/hvo-roof-answers.json");
        pi.Mode("/root/hvo-roof-answers.json").Should().Be(Modes.File);
        var saved = pi.Read("/root/hvo-roof-answers.json");
        InstallAnswers.Parse(saved).Should().BeEquivalentTo(wizard.Session.Answers);
        pi.AllText().Should().NotContain(Secret);
        pi.Snapshot().Keys.Except(before.Keys).Should().Equal("/root/hvo-roof-answers.json");

        review.SavePath.Text = "/nowhere/answers.json";
        wizard.Press(Key.Enter);
        wizard.Wizard.Message.Should().Be("Not saved: There is no folder /nowhere.");
        ShouldHaveColours(wizard.ColoursOf("Not saved"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
    }

    [TestMethod]
    public async Task APlanThatIsBlocked_CannotBeInstalled_AndSaysWhy()
    {
        using var pi = new FakeMachine().WithPi()
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { ComposeProject = "hvo-roofcontroller-rpi" });
        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(4);

        var review = (ReviewPage)wizard.Page;
        review.Plan!.IsBlocked.Should().BeTrue();
        wizard.Wizard.NextButton.Enabled.Should().BeFalse();
        wizard.Wizard.Message.Should().Be("Something here blocks the plan: see above.");
        ShouldHaveColours(wizard.ColoursOf("Something here blocks"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
        var screen = wizard.Screen;
        screen.Should().Contain("  blocked    roof-controller").And.Contain("Docker Compose made it (project hvo-roofcontroller-rpi)");
        wizard.Render(TestContext, "5-review-blocked");

        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(review);
    }

    [TestMethod]
    public async Task Install_ShowsItsProgress_CannotBeLeft_AndEndsOnDone()
    {
        using var pi = AdoptablePi(Secret);

        // The log's clock stops once the record is written, in the install's last step: the install waits there.
        using var clock = new HeldClock(() => pi.Exists(InstallPaths.SystemRecord));
        var log = InstallLog.Open(pi.Machine, InstallPaths.SystemLog, clock);
        using var wizard = await WizardDriver.StartAsync(pi, log);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.NextTo(4);

        // Install, with what it does on its way: it comes back when the loop runs.
        wizard.PressOnly(Key.Enter);

        wizard.Page.Should().BeOfType<InstallingPage>();
        wizard.Wizard.Installing.Should().BeTrue();
        wizard.Wizard.BackButton.Enabled.Should().BeFalse();
        wizard.Wizard.NextButton.Enabled.Should().BeFalse();
        wizard.PressOnly(Key.Esc);
        wizard.Page.Should().BeOfType<InstallingPage>();
        wizard.PressOnly(Key.F10);
        wizard.Closed.Should().BeFalse("the install finishes or stops first");
        wizard.Wizard.Message.Should().Be(InstallerWizard.WaitForInstallText);
        wizard.Screen.Should().Contain("Step 6 of 7: Installing").And.Contain("Installing the controller…");

        // Each step as it is made, while the install is on its way.
        var installing = wizard.PageOf<InstallingPage>();
        wizard.PumpUntil("the record's step", () => clock.Holding && installing.Describe()[^1] == $"Creating file {InstallPaths.SystemRecord}…");
        wizard.Page.Should().BeSameAs(installing);
        wizard.Wizard.Installing.Should().BeTrue();
        wizard.Screen.Should().Contain("Creating folder /var/lib/hvo-roof/settings-secrets: done.").And.Contain(InstallerWizard.WaitForInstallText);
        wizard.Render(TestContext, "7-installing");

        clock.Release();
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Success);
        wizard.Wizard.Installing.Should().BeFalse();
        installing.Describe()[^1].Should().Be($"Creating file {InstallPaths.SystemRecord}: done.");
        wizard.Page.Describe().Should().Equal(["The install finished.", .. wizard.Session.DoneLines()]);
        ShouldHaveColours(wizard.ColoursOf("The install finished."), RoofUiPalette.OpenButtonText, RoofUiPalette.OpenButton);
        wizard.Screen.Should().Contain("The controller's API:  https://roofpi.local:8443/")
            .And.Contain("Clients trust its CA:  /etc/hvo-roof/ca.crt, or https://roofpi.local:8443/ca.crt")
            .And.Contain(ControllerCertificates.Fingerprint(X509Certificate2.CreateFromPem(pi.Read("/etc/hvo-roof/ca.crt"))))
            .And.Contain("Its log:               docker logs roof-controller")
            .And.Contain("Back up /etc/hvo-roof and /var/lib/hvo-roof now, and after each change");
        wizard.Wizard.NextButton.Text.Should().Be("Quit");
        wizard.Screen.Should().Contain("Enter Quit");
        wizard.Wizard.BackButton.Enabled.Should().BeFalse();
        ShouldHaveColours(wizard.ColoursOf("Esc Back"), RoofUiPalette.MutedWeak, RoofUiPalette.Badge, "there is no going back from Done");
        wizard.Render(TestContext, "8-done");

        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Roles.Should().Equal(InstallRole.Controller);
        pi.Read(InstallPaths.SystemLog).Should().Contain("Installed the controller.");
        pi.AllText().Should().NotContain(Secret);

        wizard.Press(Key.Enter);
        wizard.Closed.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnInstallTheInstallerCannotDoYet_IsRefused_AndChangesNothing()
    {
        using var pi = new FakeMachine().WithPi();
        var log = InstallLog.Open(pi.Machine, InstallPaths.SystemLog, TimeProvider.System);
        using var wizard = await WizardDriver.StartAsync(pi, log);
        wizard.NextTo(1);
        wizard.Press(Key.Space);
        wizard.Press(Key.CursorDown);
        wizard.Press(Key.CursorDown);
        wizard.Press(Key.Space);
        ((RolesPage)wizard.Page).Chosen.Should().Equal(InstallRole.Controller, InstallRole.Kiosk);
        wizard.NextTo(4);
        var before = pi.Snapshot(InstallPaths.SystemLog);

        wizard.Press(Key.Enter);
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Refused);
        wizard.Wizard.Failure.Should().StartWith("This installer cannot install /opt/hvo-roof-kiosk, /etc/hvo-roof-kiosk, hvo-roof-kiosk.service yet, so nothing was installed.");
        wizard.Page.Describe().Should().Equal(wizard.Wizard.Failure, "Nothing was changed.", "The log: /var/log/hvo-roof-install.log");
        ShouldHaveColours(wizard.ColoursOf("This installer cannot install"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
        pi.Read(InstallPaths.SystemLog).Should().Contain("Refused: This installer cannot install /opt/hvo-roof-kiosk, /etc/hvo-roof-kiosk, hvo-roof-kiosk.service yet");
        wizard.Render(TestContext, "8-refused");
    }

    [TestMethod]
    public async Task NoColor_DrawsTheWizardInTheTerminalsOwnColours()
    {
        using var pi = AdoptablePi();
        pi.Environment["NO_COLOR"] = "1";
        using var wizard = await WizardDriver.StartAsync(pi);

        wizard.Wizard.Theme.Should().BeSameAs(RoofUiTheme.NoColour);
        wizard.DrawnAttributes().Should().OnlyContain(drawn => drawn.Foreground == Color.None && drawn.Background == Color.None);
        wizard.NextTo(1);
        wizard.Press(Key.Enter);
        wizard.DrawnAttributes().Should().OnlyContain(drawn => drawn.Foreground == Color.None && drawn.Background == Color.None);
    }

    [TestMethod]
    public async Task InAn80By24Terminal_EveryPageFits_AndLongLinesWrap()
    {
        using var pi = AdoptablePi();
        using var wizard = await WizardDriver.StartAsync(pi, width: 80, height: 24);

        foreach (var page in new[] { 0, 1, 2, 3, 4 })
        {
            if (page == 2)
            {
                wizard.Press(Key.Space);
            }

            wizard.NextTo(page);
            var rows = wizard.Rows;
            rows.Should().Contain(row => row.Contains("F10 Quit", StringComparison.Ordinal), wizard.Page.PageTitle);
            rows.Should().Contain(row => row.Contains($"► {wizard.Wizard.NextButton.Text} ◄", StringComparison.Ordinal), wizard.Page.PageTitle);
        }

        var plan = wizard.PageOf<ReviewPage>().SubViews.OfType<ReadingView>().Single();
        plan.Shown.Should().HaveCountGreaterThan(plan.Lines.Count, "a plan's lines are longer than 80 columns")
            .And.OnlyContain(line => line.Length <= plan.Viewport.Width);
    }

    [TestMethod]
    public async Task TheInstaller_RunsTheWizard_AndLeavesTheDoneTextOnTheTerminal()
    {
        using var pi = AdoptablePi(Secret);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await HVO.RoofControllerV4.Installer.Installer.RunAsync([], Interactive(pi, output, error, (app, window) =>
        {
            Until(app, window, screen => screen.Contains("Step 1 of 6", StringComparison.Ordinal));
            Press(app, window, Key.Enter, screen => screen.Contains("Step 2 of 6", StringComparison.Ordinal));
            Press(app, window, Key.Space, screen => screen.Contains("☑ Controller", StringComparison.Ordinal));
            Press(app, window, Key.Enter, screen => screen.Contains("Step 3 of 7", StringComparison.Ordinal));
            Press(app, window, Key.Enter, screen => screen.Contains("Step 4 of 7: The controller", StringComparison.Ordinal));
            Press(app, window, Key.Enter, screen => screen.Contains("Installing the controller on roofpi, 4.0.0:", StringComparison.Ordinal));
            Press(app, window, Key.Enter, screen => screen.Contains("The install finished.", StringComparison.Ordinal));
            Press(app, window, Key.Enter, _ => window.StopRequested);
        }));

        exit.Should().Be((int)InstallerExitCode.Success, error.ToString());
        error.ToString().Should().Be($"Looking at this machine…{Environment.NewLine}");
        output.ToString().Should().Contain("Installed the controller (4.0.0) on roofpi.").And.Contain("The controller's API:  https://roofpi.local:8443/");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Installed the controller.");
        pi.AllText().Should().NotContain(Secret);
    }

    [TestMethod]
    public async Task TheInstaller_QuitFromTheWizard_ChangesNothing_ButTheLog()
    {
        using var pi = AdoptablePi();
        var before = pi.Snapshot(InstallPaths.SystemLog);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await HVO.RoofControllerV4.Installer.Installer.RunAsync([], Interactive(pi, output, error, (app, window) =>
        {
            Until(app, window, screen => screen.Contains("Step 1 of 6", StringComparison.Ordinal));
            Press(app, window, Key.F10, _ => window.StopRequested);
        }));

        exit.Should().Be((int)InstallerExitCode.Cancelled);
        output.ToString().Should().BeEmpty();
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
    }

    [TestMethod]
    [DataRow(60, DisplayName = "60 columns")]
    [DataRow(40, DisplayName = "40 columns")]
    public void ALongLine_WrapsUnderItsColumn(int width)
    {
        const string container = "Container:   roof-controller: running, 4.0.0, the real HAT (made by Docker Compose project hvo-roofcontroller-rpi)";

        var lines = ReadingView.Wrap(container, width).ToArray();

        lines.Should().HaveCountGreaterThan(1).And.OnlyContain(line => line.Length <= width);
        lines.Skip(1).Should().OnlyContain(line => line.StartsWith(new string(' ', 13), StringComparison.Ordinal) && line[13] != ' ',
            "each continuation starts under the value");
        lines.SelectMany(Words).Should().Equal(Words(container), "wrapping loses no word");
    }

    [TestMethod]
    public void APlansLine_WrapsUnderItsPurpose_OrItsTarget_WhenThePurposeIsTooFarRight()
    {
        const string line = "  create     /etc/hvo-roof/secrets               secrets the controller reads, one file per setting (0700)";

        ReadingView.HangingIndent(line, 100).Should().Be(49, "the purpose starts in column 49");
        ReadingView.HangingIndent(line, 80).Should().Be(13, "past half the width, the target's column");
        ReadingView.Wrap(line, 80).Should().Equal(
            "  create     /etc/hvo-roof/secrets               secrets the controller reads,",
            "             one file per setting (0700)");
    }

    [TestMethod]
    public void AWordLongerThanTheWidth_IsCut_AndAShortLineOrNoWidth_IsLeftAlone()
    {
        ReadingView.Wrap("The log: /var/log/a-very-long-folder-name/hvo-roof-install.log", 30).Should().Equal(
            "The log:",
            "/var/log/a-very-long-folder-na",
            "me/hvo-roof-install.log");
        ReadingView.Wrap("Nothing was changed.", 30).Should().Equal("Nothing was changed.");
        ReadingView.Wrap("Nothing was changed, and nothing will be.", 0).Should().Equal("Nothing was changed, and nothing will be.");
    }

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    internal static FakeMachine AdoptablePi(string? secret = null)
    {
        var pi = InstallerPlanTests.AdoptablePi();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { Secret = secret };
        return pi;
    }

    /// <summary>The installer at a terminal on <paramref name="machine"/>, its wizard driven by <paramref name="drive"/>.</summary>
    private static InstallerHost Interactive(FakeMachine machine, TextWriter output, TextWriter error, Action<IApplication, IRunnable> drive) => new()
    {
        Out = output,
        Error = error,
        Machine = machine.Machine,
        IsInteractive = true,
        Version = WizardDriver.Version,
        CreateApplication = () =>
        {
            var app = Application.Create(new VirtualTimeProvider());
            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(100, 30);
            return app;
        },
        RunApplication = (app, window) =>
        {
            var session = app.Begin(window);
            try
            {
                drive(app, window);
            }
            finally
            {
                if (session is not null)
                {
                    app.End(session);
                }
            }
        }
    };

    private static void Press(IApplication app, IRunnable window, Key key, Func<string, bool> then)
    {
        app.InjectKey(key);
        Until(app, window, then);
    }

    /// <summary>Runs the loop until the screen shows what <paramref name="shown"/> looks for, or fails after 10 s.</summary>
    private static void Until(IApplication app, IRunnable window, Func<string, bool> shown)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            app.TimedEvents!.RunTimers();
            app.LayoutAndDraw(true);
            var screen = app.Driver!.ToString() ?? string.Empty;
            if (shown(screen))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"The wizard did not get there (stopped: {window.StopRequested}). Screen:\n{screen}");
            }

            Thread.Sleep(10);
        }
    }

    internal static void ShouldHaveColours(TuiAttribute drawn, string foreground, string background, string because = "")
    {
        drawn.Foreground.Should().Be(new Color(foreground), because);
        drawn.Background.Should().Be(new Color(background), because);
    }
}
