using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using TestSecrets = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.Core.Results;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof ui</c> (#45) on a virtual terminal against the real API with a mocked roof: Stop (F9 and the button) on
/// every page and over a prompt, Esc never closing the interface, the stale banner, the operator lease while the roof
/// moves, and the Setup, People, Settings and System pages. The tests drive the interface's own loop on the test
/// thread, so they never await.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofTerminalUiTests
{
    private const string WrongKey = "test-wrong-key-not-a-real-secret-99";

    // ---- Stop --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void F9_SendsStop_FromEveryPage()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        foreach (var (key, page) in new[] { (Key.F1, "Roof"), (Key.F2, "Settings"), (Key.F3, "People"), (Key.F4, "System"), (Key.F5, "Setup") })
        {
            tui.Press(key);
            tui.WaitIdle($"the {page} page");
            tui.Ui.CurrentPage.Title.Should().Be(page);
            tui.Screen.Should().Contain($"{RoofStopText.ButtonLabel} (F9)", "Stop is on every page");

            tui.Press(Key.F9);
            tui.WaitIdle($"Stop from the {page} page", () => tui.Ui.StopResult != RoofStopText.Sending);
            tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        }

        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(5));
    }

    [TestMethod]
    public void TheStopButton_SendsStop_AndSaysSoAtOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);

        tui.Ui.StopButton.InvokeCommand(Command.Accept);

        tui.Ui.StopResult.Should().Be(RoofStopText.Sending, "the interface says Stop is on its way before the answer");
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified, "a viewer may stop the roof");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void F9_SendsStop_WhileAPromptIsOpen_AndLeavesThePromptOpen()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F5);
        Click(tui, "Connection");
        tui.Ui.Panel.Should().NotBeNull();

        tui.Ui.StopButton.Enabled.Should().BeTrue("a prompt never disables Stop");
        tui.Press(Key.F9);
        tui.WaitIdle("Stop over the prompt", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        tui.Ui.Panel.Should().NotBeNull("Stop does not close what the person was typing");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void Stop_WithNoController_SaysItFailed_AndStaysEnabled()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var tui = new TuiDriver(rig);

        tui.Ui.CurrentPage.Should().BeOfType<RoofUiSetupPage>("with no controller, the interface opens on Setup");
        tui.Screen.Should().Contain("No controller is configured: use Setup (F5).");
        tui.Press(Key.F9);

        tui.Ui.StopResult.Should().Be(RoofStopText.Failed("No controller address is configured."));
        tui.Ui.StopButton.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void Stop_ThatTheControllerRefuses_SaysSo_InTheSharedWording()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the refusal", () => tui.Screen.Contains("No status: the controller refused the credential. Sign in on Setup (F5)."));

        tui.Press(Key.F9);
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.KeyRefused);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    // ---- Keys --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Esc_ClosesAPrompt_ButNeverTheInterface()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);

        tui.Press(Key.Esc);
        tui.Ui.QuitRequested.Should().BeFalse("Esc must not close the interface by accident");

        tui.Press(Key.F5);
        Click(tui, "Connection");
        tui.Ui.Panel.Should().NotBeNull();
        tui.Press(Key.Esc);

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.QuitRequested.Should().BeFalse();
        tui.Screen.Should().Contain(RoofTerminalUi.KeyBar);
    }

    [TestMethod]
    public void F10_Quits_WithoutStoppingARoofItDoesNotMove()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        tui.Press(Key.F10);

        tui.Ui.QuitRequested.Should().BeTrue();
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    // ---- Staleness ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void AStaleStatus_IsShownAsTheLastKnownState_AndMotionIsNotOffered_ButStopIs()
    {
        // The controller's clock is frozen, so it sends no heartbeat; the client's clock moves only when told.
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var rig = new CliRig(host) { Time = clientClock };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.Enabled.Should().BeTrue("the status is current and the roof is closed");

        clientClock.Advance(ClientTestSupport.FastFeed.StaleAfter);
        tui.WaitIdle("the stale banner", () => tui.Ui.IsStale);

        var screen = tui.Screen;
        screen.Should().Contain("STALE: no status since 2026-03-01");
        screen.Should().Contain("Showing the last known state; Stop still works.");
        screen.Should().Contain("status STALE");
        page.Describe().Should().StartWith("LAST KNOWN STATE, as of ");
        page.OpenButton.Enabled.Should().BeFalse();
        page.BlockReason(RoofMotionDirection.Opening).Should().Be("the status is stale, so the roof cannot be watched");
        tui.Ui.StopButton.Enabled.Should().BeTrue();

        tui.Press(Key.F9);
        tui.WaitIdle("Stop while stale", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);

        host.RoofService.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(RoofServiceMock.Snapshot() with { StatusVersion = 12 }));
        tui.WaitIdle("a current status again", () => !tui.Ui.IsStale && tui.Ui.Status?.StatusVersion == 12);
        tui.Screen.Should().NotContain("STALE");
        page.Describe().Should().StartWith("Status at ");
        page.OpenButton.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void BeforeTheFirstStatus_NothingIsClaimed()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the refusal", () => tui.Screen.Contains("No status: the controller refused the credential."));

        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.Ui.Status.Should().BeNull();
        page.Describe().Should().StartWith("No status from the controller yet.");
        page.Describe().Should().NotContain("Closed", "the interface was never told the roof's state");
        page.OpenButton.Enabled.Should().BeFalse();
        tui.Screen.Should().Contain("status refused");
    }

    // ---- The roof page -----------------------------------------------------------------------------------------------

    [TestMethod]
    public void AViewer_IsNotOfferedMotion()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.Enabled.Should().BeFalse();
        page.CloseButton.Enabled.Should().BeFalse();
        page.ClearFaultButton.Enabled.Should().BeFalse();
        page.Describe().Should().EndWith("Open and Close: the Operator role is needed to open or close the roof.");
        tui.Screen.Should().Contain("test-viewer (viewer, API key)");
    }

    [TestMethod]
    public void Open_HoldsTheLease_RenewsIt_AndF10StopsTheRoofBeforeClosing()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(
            RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.HoldsLease);

        tui.Ui.Message.Should().Be("Open accepted. This interface renews the operator lease while the roof moves; F9 or quitting stops it.");
        page.BlockReason(RoofMotionDirection.Closing).Should().Be("the roof is moving: stop it first");
        roof.Verify(service => service.Open(), Times.Once());

        // A third of the 30 s lease: renewed after 10 s, not before.
        tui.Tick(9);
        tui.WaitIdle("nothing");
        roof.Verify(service => service.RenewLease(), Times.Never());
        tui.Tick();
        tui.WaitIdle("the renewal");
        roof.Verify(service => service.RenewLease(), Times.Once());
        tui.Tick(10);
        tui.WaitIdle("the next renewal");
        roof.Verify(service => service.RenewLease(), Times.Exactly(2));

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.QuitRequested.Should().BeTrue();
        tui.Ui.Message.Should().Be("Stopping the roof, which moves on this interface's lease, before closing.");
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
        tui.Ui.HoldsLease.Should().BeFalse();
    }

    [TestMethod]
    public void F9_EndsTheLease_SoItIsNotRenewedAfterStop()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() =>
            RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing) with { LeaseSecondsRemaining = 6 });
        roof.SetupGet(service => service.Status).Returns(RoofControllerStatus.Open);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.BlockReason(RoofMotionDirection.Closing).Should().Be("the roof is moving: stop it first", "someone else's motion is not held here");
        tui.Ui.HoldsLease.Should().BeFalse();

        tui.Ui.HoldLease(tui.Ui.Status!);
        tui.Ui.HoldsLease.Should().BeTrue();
        tui.Press(Key.F9);
        tui.WaitIdle("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Tick(5);
        tui.WaitIdle("no renewal");

        tui.Ui.HoldsLease.Should().BeFalse();
        roof.Verify(service => service.RenewLease(), Times.Never());
    }

    [TestMethod]
    public void ARefusedOpen_SaysWhy_AndOffersTheButtonAgain()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Open()).Returns(Result<RoofControllerStatus>.Failure(new InvalidOperationException("The roof is not ready.")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the refusal", () => !tui.Ui.Message.StartsWith("Sending", StringComparison.Ordinal));

        tui.Ui.Message.Should().NotBeNullOrWhiteSpace();
        tui.Ui.Message.Should().NotStartWith("Open accepted");
        tui.Ui.HoldsLease.Should().BeFalse();
        page.OpenButton.Enabled.Should().BeTrue("the command is no longer on its way");
    }

    // ---- Setup -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Setup_FromNothing_SavesTheConnection_AddsTheFirstAdmin_SignsInAndOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        _ = host.Server;
        using var rig = new CliRig(host);
        using var tui = new TuiDriver(rig);
        var setup = (RoofUiSetupPage)tui.Ui.CurrentPage;
        setup.Describe().Should().Contain("Connection saves the controller's address");

        Click(tui, "Connection");
        Fill(tui, "http://localhost/", string.Empty, TestApiKeys.Admin);
        tui.Ui.Panel!.Fields[2].Secret.Should().BeTrue("the API key is not shown as it is typed");
        tui.Ui.Panel.Press("Save and check");
        tui.WaitIdle("the check", () => setup.LastCheck is not null);

        tui.Ui.Panel.Should().BeNull();
        rig.Stored!.Controller.Should().Be(ClientTestSupport.BaseAddress);
        rig.Stored.ApiKey.Should().Be(TestApiKeys.Admin);
        setup.LastCheck!.NeedsFirstAdmin.Should().BeTrue();
        tui.Ui.Message.Should().StartWith("Saved in ").And.EndWith("Connected with an admin API key, and nobody can sign in yet: add the first admin person.");
        setup.Describe().Should().Contain("Credential:  API key (from ");

        Click(tui, "First admin");
        Fill(tui, "ada", TestSecrets.Password, TestSecrets.OtherPassword);
        tui.Ui.Panel!.Press("Add");
        tui.Ui.Panel!.Error.Should().Be("The two passwords do not match.");
        Fill(tui, "ada", TestSecrets.Password, TestSecrets.Password);
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the first admin", () => tui.Ui.Message.StartsWith("Added ada", StringComparison.Ordinal) && !tui.Ui.Message.Contains("Checking", StringComparison.Ordinal));
        tui.Ui.Message.Should().Be("Added ada (admin). Sign in as ada here, with Sign in. The connection works.");

        Click(tui, "Sign in");
        Fill(tui, "ada", TestSecrets.Password);
        tui.Ui.Panel!.Fields[1].Secret.Should().BeTrue();
        tui.Ui.Panel.Press("Sign in");
        tui.WaitIdle("the sign-in", () => tui.Ui.Caller?.Kind == RoofCredentialKind.Session);

        tui.Ui.Message.Should().StartWith("Signed in as ada (admin) until ");
        rig.Stored!.Session!.Name.Should().Be("ada");
        rig.Stored.ApiKey.Should().Be(TestApiKeys.Admin, "signing in keeps the saved key");
        tui.Screen.Should().Contain("ada (admin) · status");

        Click(tui, "Sign out");
        tui.WaitIdle("the sign-out", () => tui.Ui.Caller?.Kind == RoofCredentialKind.ApiKey);

        tui.Ui.Message.Should().Be($"Signed out. The session was removed from {rig.CredentialsPath}.");
        rig.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    public void Setup_Connection_RefusesABadAddress_InThePrompt()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var tui = new TuiDriver(rig);

        Click(tui, "Connection");
        Fill(tui, "roof.local", string.Empty, string.Empty);
        tui.Ui.Panel!.Press("Save and check");

        tui.Ui.Panel.Should().NotBeNull("the prompt stays open to fix the address");
        tui.Ui.Panel!.Error.Should().Contain("'roof.local' is not a controller address.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public void Setup_FirstAdmin_WithAnOperatorKey_IsRefusedWithoutAPrompt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F5);

        Click(tui, "First admin");

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().Be("Adding a person needs the Admin role; test-operator is operator.");
    }

    // ---- People ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void People_AddsAPerson_AndShowsANewKeysSecretOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F3);
        tui.WaitIdle("the lists");
        var people = (RoofUiPeoplePage)tui.Ui.CurrentPage;
        people.Describe().Should().Contain("No people yet");
        tui.Ui.Message.Should().MatchRegex(@"^0 people, \d+ API keys, 0 sessions\.$");

        Click(tui, "Add");
        Fill(tui, "grace", "operator", TestSecrets.Password, TestSecrets.Password, string.Empty, string.Empty);
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the new person", () => people.Describe().Contains("grace", StringComparison.Ordinal));

        tui.Ui.Message.Should().Be("Added grace (operator).", "the result is not replaced by the reload");
        people.Describe().Should().MatchRegex("grace +operator");

        Click(tui, "API keys");
        people.Showing.Should().Be(RoofUiPeoplePage.Mode.Keys);
        people.Describe().Should().Contain("test-admin");
        Click(tui, "Add");
        Fill(tui, "dome-kiosk", "viewer", "yes");
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the new key", () => tui.Ui.Panel?.Prompt.Title == "New API key");

        var secret = tui.Ui.Panel!.Fields[0];
        secret.ReadOnly.Should().BeTrue();
        secret.Text.Should().NotBeNullOrWhiteSpace();
        tui.Ui.Panel.Prompt.Message.Should().Be("The secret of dome-kiosk. Copy it now: it is not shown again.");
        tui.Ui.Message.Should().Be("The key dome-kiosk is ready (viewer, kiosk).");
        people.Describe().Should().NotContain(secret.Text, "the list never shows a secret");
        tui.Ui.Panel.Press("Done");
        tui.Ui.Panel.Should().BeNull();

        people.Select("test-admin");
        Click(tui, "Rotate");
        tui.Ui.Message.Should().Be("The key test-admin comes from the controller's configuration and is read-only here.");
        tui.Ui.Panel.Should().BeNull();
    }

    [TestMethod]
    public void People_ForAnOperator_SaysTheAdminRoleIsNeeded()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        tui.Press(Key.F3);
        tui.WaitIdle("the page");

        tui.Ui.CurrentPage.Describe().Should().StartWith("People, API keys and sessions need the Admin role; test-operator is operator.");
    }

    // ---- Settings ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Settings_AnOperatorChangesTheUiGroup()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F2);
        tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
        var settings = (RoofUiSettingsPage)tui.Ui.CurrentPage;
        settings.Form!.Groups.Select(group => group.Name).Should().Equal(RoofSettingsContract.UiGroup);

        settings.Select("RoofControllerUi:DefaultCamera");
        Click(tui, "Change");
        tui.Ui.Panel!.Prompt.Title.Should().Be("Change Default camera");
        Fill(tui, "Pier");
        tui.Ui.Panel!.Press("Save");
        tui.WaitIdle("the save", () => tui.Ui.Message.StartsWith("Saved", StringComparison.Ordinal));

        tui.Ui.Message.Should().StartWith($"Saved (settings version {settings.Form!.Version}).");
        settings.Form.FindField("RoofControllerUi:DefaultCamera")!.DisplayValue.Should().Contain("Pier");
    }

    [TestMethod]
    public void Settings_ASafetyCriticalChange_IsShown_AndSentOnlyWhenConfirmed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-roof-tui-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var roof = new SettingsApiTests.RoofDouble();
            using var host = new RoofApiTestHost(
                roof.Mock,
                configureServices: services => services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: Path.Combine(directory, "config", "appsettings.Local.json"),
                secretsFilePath: Path.Combine(directory, "secrets", "managed-secrets.json"));
            roof.StartFrom(host);
            using var rig = new CliRig(host);
            rig.UseApiKey(TestApiKeys.Admin);
            using var tui = Started(rig);
            tui.Press(Key.F2);
            tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
            var settings = (RoofUiSettingsPage)tui.Ui.CurrentPage;
            var before = settings.Form!.Version;

            settings.Select("RoofControllerOptionsV4:AtSpeedConfirmationTimeout");
            Click(tui, "Change");
            Fill(tui, string.Empty);
            tui.Ui.Panel!.Press("Save");

            tui.Ui.Panel!.Prompt.Title.Should().Be("Safety-critical change");
            tui.Ui.Panel.Prompt.Message.Should().Contain("SAFETY-CRITICAL").And.Contain("Send it only if the roof is safe with it.");
            tui.WaitIdle("nothing sent");
            settings.Form!.Version.Should().Be(before, "nothing is sent before it is confirmed");

            tui.Ui.Panel.Press("Confirm and send");
            tui.WaitIdle("the save", () => tui.Ui.Message.StartsWith("Saved", StringComparison.Ordinal));

            settings.Form!.Version.Should().Be(before + 1);
            settings.Form.FindField("RoofControllerOptionsV4:AtSpeedConfirmationTimeout")!.DisplayValue.Should().Be(RoofSettingValues.None);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- System ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void System_ShowsHealth_AndRestartsOnlyWhenConfirmed()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F4);
        tui.WaitIdle("the health report", () => tui.Ui.Message.StartsWith("Health:", StringComparison.Ordinal));
        var system = (RoofUiSystemPage)tui.Ui.CurrentPage;

        system.Describe().Should().Contain("Health: Degraded").And.Contain("identity_store");
        system.Describe().Should().NotContain("need the Admin role");

        Click(tui, "Restart");
        tui.WaitIdle("the restart prompt", () => tui.Ui.Panel is not null);
        tui.Ui.Panel!.Prompt.Message.Should().StartWith(RoofUiSystemPage.RestartQuestion);
        tui.Press(Key.Esc);
        tui.Ui.Panel.Should().BeNull();
        host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse("the restart was not confirmed");

        Click(tui, "Restart");
        tui.WaitIdle("the restart prompt", () => tui.Ui.Panel is not null);
        tui.Ui.Panel!.Press("Restart");
        tui.WaitIdle("the restart", () => tui.Ui.Message.StartsWith("The roof is stopped.", StringComparison.Ordinal));

        tui.Ui.Message.Should().EndWith("Readiness says when it is back; the status comes back by itself.");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.AtLeastOnce());
    }

    [TestMethod]
    public void System_ForAnOperator_ShowsHealth_ButNotRestart()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F4);
        tui.WaitIdle("the health report", () => tui.Ui.Message.StartsWith("Health:", StringComparison.Ordinal));

        tui.Ui.CurrentPage.Describe().Should().StartWith("The version, host and resource use need the Admin role.");
        Click(tui, "Restart");

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().Be("Restarting the controller needs the Admin role.");
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    /// <summary>The interface with its first status and the caller known.</summary>
    private static TuiDriver Started(CliRig rig)
    {
        var tui = new TuiDriver(rig);
        try
        {
            tui.WaitIdle("the first status", () => tui.Ui.Status is not null && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }

    /// <summary>Presses the visible button labelled <paramref name="label"/> on the page or prompt, as Enter on it would.</summary>
    private static void Click(TuiDriver tui, string label)
    {
        View root = tui.Ui.Panel is { } panel ? panel : tui.Ui.CurrentPage;
        var button = Find(root, label) ?? throw new AssertFailedException($"No visible button '{label}'. Screen:\n{tui.Screen}");
        button.InvokeCommand(Command.Accept);
        tui.Pump();
    }

    private static Button? Find(View view, string label)
    {
        foreach (var child in view.SubViews)
        {
            if (child is Button { Visible: true } button && button.Text == label)
            {
                return button;
            }

            if (child.Visible && Find(child, label) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Fill(TuiDriver tui, params string[] values)
    {
        var fields = tui.Ui.Panel?.Fields ?? throw new AssertFailedException($"No prompt is open. Screen:\n{tui.Screen}");
        fields.Should().HaveCount(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            fields[i].Text = values[i];
        }
    }
}
