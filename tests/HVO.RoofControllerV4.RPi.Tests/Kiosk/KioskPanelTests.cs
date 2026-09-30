using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's PIN pad, settings page and system page against the controller's API: what each reads, asks and sends
/// for the person who unlocked the kiosk, and what it says back.
/// </summary>
[TestClass]
public sealed class KioskPanelTests
{
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";
    private const string AtSpeed = "RoofControllerOptionsV4:AtSpeedConfirmationTimeout";
    private const string Departure = "RoofControllerOptionsV4:DepartureReleaseTimeout";
    private const string RequireHttps = "RoofControllerSecurity:RequireHttps";
    private const string LogLevel = "Logging:LogLevel:Default";
    private const string CameraPassword = "BlueIris:Password";

    // ---- The PIN pad ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ThePinPad_ListsThePeopleWithAPin_AndShowsOnlyADotPerDigit()
    {
        await using var harness = await KioskHarness.CreateAsync();
        var pad = new KioskPinPad(harness.Console);
        var changes = 0;
        pad.Changed += () => changes++;

        await pad.LoadUsersAsync();

        pad.Users.Select(user => user.Name).Should().BeEquivalentTo(KioskHarness.Operator, KioskHarness.Admin);
        pad.Selected.Should().BeNull("two people may unlock it: the kiosk does not guess which");
        pad.Prompt.Should().Be("Choose your name, then enter your PIN.");
        pad.CanUnlock.Should().BeFalse();
        pad.Error.Should().BeNull();
        changes.Should().Be(2, "once as the read starts, once as it ends");

        pad.Select(KioskHarness.Operator);
        pad.Prompt.Should().Be("PIN for olga: 6 to 12 digits.");
        foreach (var digit in "24681")
        {
            pad.Press(digit);
        }

        pad.Press('x');
        pad.Masked.Should().Be("●●●●●");
        pad.CanUnlock.Should().BeFalse("five digits are too few");
        pad.Press('0');
        pad.CanUnlock.Should().BeTrue();

        foreach (var digit in "1234567890")
        {
            pad.Press(digit);
        }

        pad.Length.Should().Be(RoofIdentityContract.MaximumPinLength, "digits past the longest PIN are ignored");
        pad.Backspace();
        pad.Length.Should().Be(RoofIdentityContract.MaximumPinLength - 1);
        pad.Clear();
        pad.Masked.Should().BeEmpty();

        pad.Press('1');
        pad.Select(KioskHarness.Admin);
        pad.Length.Should().Be(0, "a PIN typed for one person is not sent for another");
    }

    [TestMethod]
    public async Task ThePinPad_UnlocksWithTheRightPin_AndForgetsThePinWhateverTheAnswer()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        var pad = new KioskPinPad(harness.Console);
        await pad.LoadUsersAsync();
        pad.Select(KioskHarness.Operator);

        Type(pad, TestSecrets.OtherPin);
        await pad.UnlockAsync();

        pad.Error.Should().NotBeNullOrWhiteSpace();
        pad.Length.Should().Be(0);
        pad.Unlocking.Should().BeFalse();
        harness.Console.View.IsUnlocked.Should().BeFalse();

        Type(pad, TestSecrets.Pin);
        pad.Error.Should().BeNull("typing again clears the last answer");
        await pad.UnlockAsync();

        pad.Error.Should().BeNull();
        pad.Length.Should().Be(0);
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);
    }

    [TestMethod]
    public async Task ThePinPad_ChoosesTheOnlyPerson_AndDropsAPersonWhoIsGone()
    {
        await using var harness = await KioskHarness.CreateAsync();
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var pad = new KioskPinPad(harness.Console);
        await pad.LoadUsersAsync();
        pad.Select(KioskHarness.Admin);

        await admin.Identity.RemoveUserAsync(KioskHarness.Admin);
        await pad.LoadUsersAsync();

        pad.Users.Should().ContainSingle().Which.Name.Should().Be(KioskHarness.Operator);
        pad.Selected.Should().Be(KioskHarness.Operator, "the only person who may unlock it is chosen");
    }

    [TestMethod]
    public async Task ThePinPad_SaysWhenNoOneHasAPin()
    {
        await using var harness = await KioskHarness.CreateAsync();
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.RemoveUserAsync(KioskHarness.Operator);
        await admin.Identity.RemoveUserAsync(KioskHarness.Admin);
        var pad = new KioskPinPad(harness.Console);

        await pad.LoadUsersAsync();

        pad.Users.Should().BeEmpty();
        pad.Error.Should().Be(KioskPinPad.NoPinUsers);
    }

    [TestMethod]
    public async Task ThePinPad_SaysWhenThePeopleCannotBeRead()
    {
        await using var harness = await KioskHarness.CreateAsync();
        harness.Reachable = false;
        var pad = new KioskPinPad(harness.Console);

        await pad.LoadUsersAsync();

        pad.Error.Should().StartWith("The people who may unlock the kiosk could not be read. ");
        pad.Loading.Should().BeFalse();
    }

    // ---- The settings page ----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnOperator_ChangesTheScreenTimeout_AndTheKioskUsesItAtOnce()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(5), "the default screen timeout");
        var settings = new KioskSettingsPanel(harness.Console);
        settings.Header.Should().Be(KioskSettingsPanel.NotReadYet);

        await settings.LoadAsync();

        var version = settings.Form!.Version;
        settings.Header.Should().StartWith($"Version {version}.");
        settings.SelectField(KioskTimeout);
        settings.Group!.Name.Should().Be(RoofSettingsContract.UiGroup, "choosing a setting shows its group");
        settings.Field!.DisplayValue.Should().StartWith("300 s");
        KioskSettingsPanel.DescribeLine(settings.Field).Should().Be(settings.Field.DisplayValue);
        var details = KioskSettingsPanel.Describe(settings.Field);
        details[0].Should().Be($"{settings.Field.Label} ({KioskTimeout}): {settings.Field.DisplayValue}   default {settings.Field.DefaultValue}");
        details[1].Should().Be("You may change it.");
        details[^1].Should().Be(settings.Field.Description);

        settings.BeginEdit();
        settings.EditorKind.Should().Be(KioskEditorKind.Keypad);
        settings.EditText.Should().Be("300");
        settings.ClearText();
        settings.Type("120");
        await settings.ReviewAsync();

        settings.Message.Should().Be(new KioskNotice($"Saved (settings version {version + 1}).", KioskNoticeLevel.Info, KioskHarness.Start));
        settings.Editing.Should().BeNull();
        settings.Form!.Version.Should().Be(version + 1, "the page is read again after the change");
        settings.Form.FindField(KioskTimeout)!.DisplayValue.Should().StartWith("120 s");
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(2), "the new screen timeout");
    }

    [TestMethod]
    public async Task AValueThatIsUnchangedOrInvalid_IsNotSent()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        var version = settings.Form!.Version;
        settings.SelectField(KioskTimeout);

        settings.BeginEdit();
        settings.Type("0000000");
        await settings.ReviewAsync();

        settings.EditError.Should().NotBeNullOrWhiteSpace();
        settings.Editing.Should().NotBeNull("the value can be corrected");
        settings.Backspace();
        settings.EditError.Should().BeNull();

        settings.ClearText();
        settings.Type("300");
        await settings.ReviewAsync();

        settings.Message!.Text.Should().Be(RoofSettingsText.NothingToChange);
        settings.Editing.Should().BeNull();
        (await harness.Client.Settings.GetAsync()).Version.Should().Be(version);
    }

    [TestMethod]
    public async Task TurningASafetyInterlockOff_IsAskedFirst_AndSentOnlyWhenConfirmed()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(AtSpeed);
        settings.BeginEdit();
        settings.ClearText();

        await settings.ReviewAsync();

        var question = settings.Question!;
        question.Title.Should().Be("Safety-critical change");
        question.Lines.Should().EndWith(RoofSettingsText.SafetyCriticalChange);
        question.Answers.Select(answer => answer.Label).Should().Equal("Confirm and send");
        harness.Roof.Applied.Should().BeEmpty("nothing is sent before it is confirmed");

        settings.Dismiss();
        settings.Question.Should().BeNull();
        harness.Roof.Applied.Should().BeEmpty();

        await settings.ReviewAsync();
        await settings.Question!.Answers[0].Act();

        settings.Message!.Text.Should().StartWith("Saved (settings version ");
        harness.Roof.Applied.Should().ContainSingle().Which.Options.AtSpeedConfirmationTimeout.Should().BeNull();
        settings.Form!.FindField(AtSpeed)!.EditText.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TrueOrFalse_AndAnEnum_AreChosen_NotTyped()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();

        settings.SelectField(RequireHttps);
        settings.BeginEdit();
        settings.EditorKind.Should().Be(KioskEditorKind.Choices);
        settings.Choices.Should().Equal("true", "false", KioskSettingsPanel.NoValue);
        settings.CancelEdit();
        settings.Editing.Should().BeNull();

        settings.SelectField(LogLevel);
        settings.BeginEdit();
        settings.EditorKind.Should().Be(KioskEditorKind.Choices);
        settings.Choices.Should().Contain("Debug").And.NotContain(KioskSettingsPanel.NoValue);
        await settings.ChooseAsync("Loud");
        settings.Message.Should().BeNull("only an offered value is sent");

        await settings.ChooseAsync("Debug");

        settings.Message!.Text.Should().StartWith("Saved (settings version ");
        settings.Form!.FindField(LogLevel)!.DisplayValue.Should().Be("Debug");
    }

    [TestMethod]
    public async Task AString_IsTypedOnTheKeyboard_WithShiftForOneCapital()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(DefaultCamera);

        settings.BeginEdit();
        settings.EditorKind.Should().Be(KioskEditorKind.Keyboard);
        settings.ToggleShift();
        settings.Type("p");
        settings.Shift.Should().BeFalse();
        settings.Type("ier");
        await settings.ReviewAsync();

        settings.Form!.FindField(DefaultCamera)!.DisplayValue.Should().Be("Pier");
    }

    [TestMethod]
    public async Task ASecret_IsNotTypedOnTheKiosk()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();

        settings.SelectField(CameraPassword);
        settings.Field!.CanWrite.Should().BeTrue("an admin may change it, elsewhere");
        settings.BeginEdit();

        settings.Editing.Should().BeNull();
        settings.Message.Should().Be(new KioskNotice(KioskWording.Kiosk.SecretsElsewhere, KioskNoticeLevel.Info, KioskHarness.Start));
        KioskSettingsPanel.Describe(settings.Field)[0].Should().NotContain("default");
    }

    [TestMethod]
    public async Task ALocalOnlySetting_IsChangedByAnAdminAtTheControllersKiosk()
    {
        await using var harness = await KioskHarness.CreateAsync(localKey: true);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(Departure);
        settings.Field!.CanWrite.Should().BeTrue();

        settings.BeginEdit();
        settings.ClearText();
        settings.Type("25");
        await settings.ReviewAsync();

        settings.Message!.Text.Should().StartWith("Saved (settings version ");
        var applied = harness.Roof.Applied.Should().ContainSingle().Subject;
        applied.IncludeLocalOnly.Should().BeTrue();
        applied.Options.DepartureReleaseTimeout.Should().Be(TimeSpan.FromSeconds(25));
    }

    [TestMethod]
    public async Task ALocalOnlySetting_IsReadOnly_AtAnotherKiosk()
    {
        await using var harness = await KioskHarness.CreateAsync(localKey: false);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(Departure);

        settings.Field!.CanWrite.Should().BeFalse();
        settings.Field.ReadOnlyReason.Should().Contain("is local-only");
        KioskSettingsPanel.DescribeLine(settings.Field).Should().EndWith("  (read-only)");
        settings.BeginEdit();
        settings.Editing.Should().BeNull();
        settings.Message!.Level.Should().Be(KioskNoticeLevel.Warning);
        settings.Message.Text.Should().Be($"{settings.Field.Label} is read-only: {settings.Field.ReadOnlyReason}");
    }

    [TestMethod]
    public async Task AHandEdit_IsShownToAnAdmin_AndApplied()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        EditSettingsFile(harness, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        var version = settings.Form!.Version;
        settings.Header.Should().Be($"Version {version}. The settings file was edited by hand (1 change(s)): changes here are refused until it is applied or discarded (Hand edit).");

        settings.ReviewHandEdit();

        var question = settings.Question!;
        question.Title.Should().Be("Hand edit");
        question.Answers.Select(answer => answer.Label).Should().Equal("Apply", "Discard");
        await question.Answers[0].Act();

        settings.Message!.Text.Should().Be($"Applied the hand edit (settings version {version + 1}).");
        settings.Form!.PendingHandEdit.Should().BeNull();
        settings.Form.FindField(DefaultCamera)!.DisplayValue.Should().Be("Yard");
    }

    [TestMethod]
    public async Task AHandEdit_IsDiscarded_OnlyOnceAskedAgain()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        EditSettingsFile(harness, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        var version = settings.Form!.Version;
        settings.ReviewHandEdit();

        await settings.Question!.Answers[1].Act();

        settings.Question!.Title.Should().Be("Discard the hand edit");
        settings.Question.Lines.Should().Equal(RoofSettingsText.DiscardHandEditQuestion);
        (await harness.Client.Settings.GetAsync()).PendingHandEdit.Should().NotBeNull("nothing is discarded before the second answer");

        await settings.Question.Answers.Single(answer => answer.Label == "Discard it").Act();

        settings.Message!.Text.Should().Be($"Discarded the hand edit (settings version {version + 1}).");
        settings.Form!.PendingHandEdit.Should().BeNull();
        settings.Form.FindField(DefaultCamera)!.DisplayValue.Should().NotBe("Yard");
    }

    [TestMethod]
    public async Task AHandEdit_NeedsAnAdmin()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        EditSettingsFile(harness, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        var settings = new KioskSettingsPanel(harness.Console);
        settings.ReviewHandEdit();
        settings.Message!.Text.Should().Be(KioskSettingsPanel.NotReadYet);
        await settings.LoadAsync();

        settings.ReviewHandEdit();

        settings.Question.Should().BeNull();
        settings.Message.Should().Be(new KioskNotice(RoofSettingsText.HandEditNeedsAdmin(settings.Form!), KioskNoticeLevel.Warning, KioskHarness.Start));
    }

    [TestMethod]
    public async Task TheSettings_ThatCannotBeRead_SaySo_AndLockingForgetsThem()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(KioskTimeout);
        settings.BeginEdit();

        settings.Reset();

        settings.Form.Should().BeNull();
        settings.Group.Should().BeNull();
        settings.Field.Should().BeNull();
        settings.Editing.Should().BeNull();
        settings.EditText.Should().BeEmpty();

        harness.Reachable = false;
        await settings.LoadAsync();

        settings.Message!.Level.Should().Be(KioskNoticeLevel.Danger);
        settings.Message.Text.Should().StartWith("The settings could not be read. ");
        settings.Busy.Should().BeNull();
    }

    [TestMethod]
    public async Task TheSettingsReadForAnAdmin_AnsweredAfterTheLock_AreNotShownToTheNextPerson()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var settings = new KioskSettingsPanel(harness.Console);
        await using var held = HoldNext(harness, HttpMethod.Get, RoofApiRoutes.Settings);
        var adminsRead = settings.LoadAsync();
        await held.WaitAsync();
        settings.Busy.Should().NotBeNull("the admin's read of the settings is on its way");

        await harness.Console.LockAsync();
        settings.Reset();
        await harness.UnlockAsync(KioskHarness.Operator);
        settings.Reset();
        await settings.LoadAsync();
        var operators = settings.Form;
        operators.Should().NotBeNull();

        held.Release();
        await adminsRead.WaitAsync(TimeSpan.FromSeconds(10));

        settings.Form.Should().BeSameAs(operators, "the admin's settings, answered after the lock, are not shown to the operator");
        settings.Busy.Should().BeNull("the operator's page is not busy with the admin's read");
        settings.Message.Should().BeNull();
    }

    [TestMethod]
    public async Task ASaveAnsweredAfterTheLock_IsNotShownToTheNextPerson_ButTheKioskUsesIt()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(5), "the default screen timeout");
        var settings = new KioskSettingsPanel(harness.Console);
        await settings.LoadAsync();
        settings.SelectField(KioskTimeout);
        settings.BeginEdit();
        settings.ClearText();
        settings.Type("120");
        var save = RoofApiRoutes.SettingsGroup(RoofSettingsContract.UiGroup);
        await using var held = HoldNext(harness, HttpMethod.Post, save);
        var saving = settings.ReviewAsync();
        await held.WaitAsync();

        await harness.Console.LockAsync();
        settings.Reset();
        held.Release();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));

        settings.Form.Should().BeNull("the page read for the operator is not read again after the lock");
        settings.Message.Should().BeNull("what the save came to is not shown to the next person");
        settings.Busy.Should().BeNull();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromMinutes(2), "the new screen timeout, which the controller saved");
    }

    // ---- The system page ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnOperator_SeesTheHealth_ButNotTheHost_AndCannotRestart()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var system = new KioskSystemPanel(harness.Console);

        await system.LoadAsync();

        system.Health.Should().NotBeNullOrWhiteSpace();
        system.Checks.Should().NotBeEmpty();
        system.Information.Should().BeEmpty();
        system.Self.Should().Be(("This kiosk", RoofProductVersion.Describe(typeof(KioskSystemPanel).Assembly)),
            "everyone may see the kiosk's own version, but not the controller's");
        system.Message!.Text.Should().Be($"Health: {system.Health}.");

        await system.AskRestartAsync();

        system.Question.Should().BeNull();
        system.Message.Should().Be(new KioskNotice(KioskSystemPanel.RestartNeedsAdmin, KioskNoticeLevel.Warning, KioskHarness.Start));
        harness.Host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheHostReadForAnAdmin_AnsweredAfterTheLock_IsNotShownToTheNextPerson()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var system = new KioskSystemPanel(harness.Console);
        await using var held = HoldNext(harness, HttpMethod.Get, RoofApiRoutes.SystemMetrics);
        var adminsRead = system.LoadAsync();
        await held.WaitAsync();

        await harness.Console.LockAsync();
        system.Reset();
        await harness.UnlockAsync(KioskHarness.Operator);
        system.Reset();
        await system.LoadAsync();
        var operators = system.Message;
        system.Information.Should().BeEmpty();

        held.Release();
        await adminsRead.WaitAsync(TimeSpan.FromSeconds(10));

        system.Information.Should().BeEmpty("the admin's host information, answered after the lock, is not shown to the operator");
        system.Message.Should().BeSameAs(operators);
        system.Busy.Should().BeNull("the operator's page is not busy with the admin's read");
    }

    [TestMethod]
    public async Task AnAdmin_SeesTheHost_AndRestartsTheController_OnceAsked()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        var system = new KioskSystemPanel(harness.Console);
        var signal = harness.Host.Services.GetRequiredService<RoofRestartSignal>();

        await system.LoadAsync();
        system.Information.Should().NotBeEmpty();

        await system.AskRestartAsync();
        system.Question!.Title.Should().Be("Restart the controller");
        system.Question.Lines.Should().Equal(RoofSystemText.RestartQuestion);
        system.Question.Answers.Select(answer => answer.Label).Should().Equal("Restart");
        system.Dismiss();
        signal.Requested.Should().BeFalse("a dismissed restart sends nothing");

        await system.AskRestartAsync();
        await system.Question!.Answers[0].Act();

        system.Message!.Level.Should().Be(KioskNoticeLevel.Info);
        system.Message.Text.Should().EndWith($" {RoofSystemText.RestartComingBack}");
        signal.Requested.Should().BeTrue();
        harness.Roof.Mock.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
    }

    [TestMethod]
    public async Task ARestartThatLoadsASafetyCriticalHandEdit_IsConfirmed()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Admin);
        EditSettingsFile(harness, "{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }");
        var system = new KioskSystemPanel(harness.Console);

        await system.AskRestartAsync();

        system.Question!.Lines.Should().Contain(RoofSystemText.RestartLoadsHandEdit);
        system.Question.Lines.Should().Contain("  RoofControllerOptionsV4:OpenRelayId: 1 -> 2  [SAFETY-CRITICAL]");
        system.Question.Answers.Select(answer => answer.Label).Should().Equal("Confirm and restart");
        await system.Question.Answers[0].Act();
        harness.Host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeTrue();
    }

    [TestMethod]
    public async Task Readiness_IsAskedWithoutCredentials_AndAnUnreachableControllerSaysSo()
    {
        await using var harness = await KioskHarness.CreateAsync();
        var system = new KioskSystemPanel(harness.Console);

        await system.CheckReadinessAsync();

        system.Message!.Text.Should().MatchRegex("^(Ready|Not ready): .+\\.$");

        harness.Reachable = false;
        await system.LoadAsync();

        system.Message!.Level.Should().Be(KioskNoticeLevel.Danger);
        system.Busy.Should().BeNull();
        system.Reset();
        system.Message.Should().BeNull();
        system.Health.Should().BeNull();
    }

    private static void Type(KioskPinPad pad, string pin)
    {
        foreach (var digit in pin)
        {
            pad.Press(digit);
        }
    }

    private static void EditSettingsFile(KioskHarness harness, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(harness.SettingsPath)!);
        File.WriteAllText(harness.SettingsPath, json);
    }

    /// <summary>
    /// Holds the answer to the next <paramref name="method"/> request to <paramref name="path"/> on its way, even if the
    /// kiosk gives up on it, until <see cref="HeldAnswer.Release"/> (or the end of the test).
    /// </summary>
    private static HeldAnswer HoldNext(KioskHarness harness, HttpMethod method, string path)
    {
        var held = new HeldAnswer();
        var next = 1;
        harness.DelayAnswer = (request, _) =>
        {
            if (request.Method != method || request.RequestUri!.AbsolutePath.TrimStart('/') != path || Interlocked.Exchange(ref next, 0) == 0)
            {
                return Task.CompletedTask;
            }

            held.Reached.TrySetResult();
            return held.Answer.Task;
        };
        return held;
    }

    private sealed class HeldAnswer : IAsyncDisposable
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => Answer.TrySetResult();

        // A test that fails while the answer is held lets it go, so the harness can close.
        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }
}
