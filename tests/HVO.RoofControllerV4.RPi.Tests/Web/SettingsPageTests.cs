using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Components;
using HVO.RoofControllerV4.Web.Components.Pages;
using SettingsPage = HVO.RoofControllerV4.Web.Components.Pages.Settings;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The Settings page reads the controller's settings with the person's session and changes one setting at a time: a
/// safety-critical change is shown for review before it is sent, a secret is typed twice and never shown, and a refusal
/// for settings that changed since they were read (another change, or a hand edit of the file) reads them again.
/// </summary>
[TestClass]
public sealed class SettingsPageTests
{
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";
    private const string KioskScreenTimeout = "RoofControllerUi:KioskScreenTimeout";
    private const string AtSpeedTimeout = "RoofControllerOptionsV4:AtSpeedConfirmationTimeout";
    private const string CameraServer = "BlueIris:BaseUrl";
    private const string CameraUser = "BlueIris:UserName";
    private const string CameraPassword = "BlueIris:Password";
    private const string CameraUserName = "test-camera-user-not-real";
    private const string NewCameraPassword = "test-camera-password-not-real";
    private const string FirstCameraServer = "http://camera-one.test:81/";
    private const string SecondCameraServer = "http://camera-two.test:81/";

    [TestMethod]
    public async Task AnAdmin_SeesEveryGroup_AndTheFirstGroupsSettings()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);

        var cut = Loaded(context);

        cut.Find("[data-testid=settings-summary] h2").TextContent.Should().MatchRegex(@"^Settings version \d+$");
        cut.Find("[data-testid=settings-summary]").TextContent.Should().Contain("Not changed through the controller yet.")
            .And.Contain(harness.SettingsPath);
        cut.FindAll("[data-testid=settings-in-memory]").Should().BeEmpty("the settings are kept in the file");
        cut.FindAll("[data-testid=settings-group]").Select(group => group.GetAttribute("data-group")).ToList().Should().Equal(
            RoofSettingsContract.RoofGroup, RoofSettingsContract.ControllerGroup, RoofSettingsContract.CameraGroup, RoofSettingsContract.SecurityGroup,
            RoofSettingsContract.IdentityGroup, RoofSettingsContract.LoggingGroup, RoofSettingsContract.UiGroup);
        cut.Find("[data-testid=settings-group][aria-pressed=true]").TextContent.Trim().Should().Be("Roof");
        cut.Find("[data-testid=settings-fields]").GetAttribute("data-group").Should().Be(RoofSettingsContract.RoofGroup);

        var openRelay = Setting(cut, "RoofControllerOptionsV4:OpenRelayId");
        openRelay.QuerySelector("strong")!.TextContent.Should().Be("Open relay ID");
        openRelay.QuerySelector("[data-testid=setting-value]")!.TextContent.Should().Be("1");
        Notes(openRelay).Should().Equal("safety-critical");
        Notes(Setting(cut, "RoofControllerOptionsV4:AllowIgnoringLimitSwitchesOnPhysicalHardware"))
            .Should().Equal("safety-critical", "local credential");
        Setting(cut, "RoofControllerOptionsV4:AllowIgnoringLimitSwitchesOnPhysicalHardware").QuerySelector("[data-testid=setting-read-only]")!
            .TextContent.Should().StartWith("Read-only: ", "a session is not a local credential");

        cut.Find("[data-testid=settings-group][data-group=logging]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=settings-fields]").GetAttribute("data-group").Should().Be(RoofSettingsContract.LoggingGroup));
        Setting(cut, "Logging:LogLevel:Default").QuerySelector("[data-testid=setting-value]")!.TextContent.Should().Be("Information");
    }

    [TestMethod]
    public async Task WithoutASettingsFile_ThePageSaysOnce_ThatChangesAreLostAtARestart_AndWhy()
    {
        using var harness = new WebAdminHarness(fileBacked: false);
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        cut.FindAll("[data-testid=settings-in-memory]").Should().BeEmpty("the controller's warning says it, and why");
        var warning = cut.FindAll("[data-testid=settings-warning]").Should().ContainSingle().Subject.TextContent;
        warning.Should().Contain("held in memory only").And.Contain("RoofControllerSettings:FilePath is not set");

        cut.Find($"[data-testid=settings-group][data-group={RoofSettingsContract.UiGroup}]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=settings-fields]").GetAttribute("data-group").Should().Be(RoofSettingsContract.UiGroup));
        Change(cut, DefaultCamera, "Pier");

        cut.WaitForAssertion(() => Message(cut).Should().StartWith($"Saved (settings version {before + 1})."));
        Message(cut).Should().NotContain(RoofSettingsText.InMemoryOnly).And.Contain("held in memory only");
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Warning));
    }

    [TestMethod]
    public async Task AnOperator_ChangesTheDefaultCamera_WithTheirSession()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        cut.FindAll("[data-testid=settings-group]").Select(group => group.GetAttribute("data-group")).ToList()
            .Should().Equal([RoofSettingsContract.UiGroup], "an operator reads only the clients' settings");
        Change(cut, DefaultCamera, "Pier");

        cut.WaitForAssertion(() => Message(cut).Should().Be($"Saved (settings version {before + 1})."));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Success));
        Value(cut, DefaultCamera).Should().Be("Pier");
        cut.Find("[data-testid=settings-summary]").TextContent.Should().Contain("Saved 2026-09-29 12:00:00Z by olga (signed in).");
        cut.FindAll("[data-testid=setting-editor]").Should().BeEmpty();
        using var admin = harness.Admin();
        (await admin.Settings.GetAsync()).Settings.Single(setting => setting.Key == DefaultCamera).Value!.Value.GetString().Should().Be("Pier");
        File.ReadAllText(harness.SettingsPath).Should().Contain("Pier");
    }

    [TestMethod]
    public async Task AViewer_SeesTheClientSettings_ReadOnly()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("vic", RoofControllerApiContract.ViewerRole);
        await using var context = harness.Context(session);

        var cut = Loaded(context);

        cut.FindAll("[data-testid=setting]").Select(setting => setting.GetAttribute("data-key")).ToList().Should().Equal(DefaultCamera, KioskScreenTimeout);
        cut.FindAll("[data-testid=setting-read-only]").Should().HaveCount(2);
        cut.FindAll("[data-testid=setting-change]").Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(KioskScreenTimeout, "soon", "Enter a duration")]
    [DataRow(KioskScreenTimeout, "999999", "at most")]
    public async Task AValueThatCannotBeRight_IsSaidInTheEditor_AndNotSent(string key, string text, string said)
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        Change(cut, key, text);

        cut.WaitForAssertion(() => cut.Find("[data-testid=setting-error]").TextContent.Should().ContainEquivalentOf(said));
        cut.FindAll("[data-testid=setting-editor]").Should().ContainSingle("the editor stays open to correct it");
        using var admin = harness.Admin();
        (await admin.Settings.GetAsync()).Version.Should().Be(before);
    }

    [TestMethod]
    public async Task TheValueTheSettingHas_IsNotSent()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        Open(cut, KioskScreenTimeout);
        cut.Find("[data-testid=setting-save]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().Be(RoofSettingsText.NothingToChange));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Info));
        Version(cut).Should().Be(before);
    }

    [TestMethod]
    public async Task ASafetyCriticalChange_IsShownForReview_AndSentOnlyWhenConfirmed()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        Change(cut, AtSpeedTimeout, string.Empty);

        var review = cut.WaitForElement("[data-testid=setting-review]");
        review.QuerySelector("li")!.TextContent.Should().StartWith("At speed confirmation timeout: ").And.EndWith("-> (none)  [SAFETY-CRITICAL]");
        review.TextContent.Should().Contain(RoofSettingsText.SafetyCriticalChange);
        cut.Find("[data-testid=setting-send]").TextContent.Trim().Should().Be("Confirm and send");
        harness.Roof.Applied.Should().BeEmpty("nothing is sent before it is confirmed");

        cut.Find("[data-testid=setting-send]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().Be($"Saved (settings version {before + 1})."));
        Value(cut, AtSpeedTimeout).Should().Be(RoofSettingValues.None);
        harness.Roof.Current.AtSpeedConfirmationTimeout.Should().BeNull();
    }

    [TestMethod]
    public async Task AReviewThatIsCancelled_SendsNothing()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        Change(cut, AtSpeedTimeout, string.Empty);
        cut.WaitForElement("[data-testid=setting-review] button:not([data-testid])").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=setting-review]").Should().BeEmpty());
        Setting(cut, AtSpeedTimeout).QuerySelector("[data-testid=setting-change]").Should().NotBeNull();
        harness.Roof.Applied.Should().BeEmpty();
    }

    /// <summary>
    /// With the camera proxy on, the controller takes the Blue Iris user and password only together, so the page sets and
    /// clears them together; and moving the proxy to another server is done by clearing them, moving it, and setting them
    /// again, as docs/web.md says.
    /// </summary>
    [TestMethod]
    public async Task TheCamerasUserAndPassword_AreTypedTwice_NeverShown_AndSetAndClearedTogether()
    {
        using var harness = new WebAdminHarness(new Dictionary<string, string?> { [CameraServer] = FirstCameraServer });
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Find("[data-testid=settings-group][data-group=camera]").Click();
        cut.WaitForAssertion(() => Value(cut, CameraPassword).Should().Be(RoofSettingValues.SecretNotSet));
        Value(cut, CameraUser).Should().Be(RoofSettingValues.SecretNotSet);
        cut.FindAll("[data-testid=setting-clear]").Should().BeEmpty("there is nothing to clear");

        Open(cut, CameraPassword);
        cut.FindAll("[data-testid=setting-input]").Select(input => input.GetAttribute("data-key")).Should().Equal(CameraUser, CameraPassword);
        cut.FindAll("[data-testid=setting-editor] input").Select(input => input.GetAttribute("type")).Should().AllBe("password");
        cut.Find("[data-testid=setting-together]").TextContent.Should().Be("User name and Password are set and cleared together.");
        cut.Find("[data-testid=setting-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=setting-error]").TextContent.Should().Be(
            "Type the new User name: User name and Password are set and cleared together. To remove them, use Clear secret."));

        Type(cut, CameraUser, CameraUserName);
        Type(cut, CameraPassword, NewCameraPassword, again: NewCameraPassword + "!");
        cut.Find("[data-testid=setting-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=setting-error]").TextContent.Should().Be("The two Password values differ."));

        Type(cut, CameraPassword, NewCameraPassword);
        cut.Find("[data-testid=setting-save]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().StartWith("Saved (settings version "));
        Value(cut, CameraUser).Should().Be(RoofSettingValues.SecretSet);
        Value(cut, CameraPassword).Should().Be(RoofSettingValues.SecretSet);
        cut.Markup.Should().NotContain(NewCameraPassword).And.NotContain(CameraUserName);

        // The controller keeps the credentials with their server: a move while they are set is refused.
        Change(cut, CameraServer, SecondCameraServer);
        cut.WaitForAssertion(() => Message(cut).Should().Contain("Moving the camera proxy to another server"));
        Value(cut, CameraServer).Should().Be(FirstCameraServer);

        cut.WaitForAssertion(() => Setting(cut, CameraUser).QuerySelector("[data-testid=setting-clear]")!.HasAttribute("disabled").Should().BeFalse());
        Setting(cut, CameraUser).QuerySelector("[data-testid=setting-clear]")!.Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=setting-review] li").Select(change => change.TextContent).Should().Equal(
            $"User name: {RoofSettingValues.SecretSet} -> {RoofSettingValues.SecretNotSet}",
            $"Password: {RoofSettingValues.SecretSet} -> {RoofSettingValues.SecretNotSet}"));
        cut.Find("[data-testid=setting-send]").TextContent.Trim().Should().Be("Clear them");
        cut.Find("[data-testid=setting-send]").Click();
        cut.WaitForAssertion(() => Value(cut, CameraPassword).Should().Be(RoofSettingValues.SecretNotSet));
        Value(cut, CameraUser).Should().Be(RoofSettingValues.SecretNotSet);

        Change(cut, CameraServer, SecondCameraServer);
        cut.WaitForAssertion(() => Value(cut, CameraServer).Should().Be(SecondCameraServer));

        Open(cut, CameraUser);
        Type(cut, CameraUser, CameraUserName);
        Type(cut, CameraPassword, NewCameraPassword);
        cut.Find("[data-testid=setting-save]").Click();
        cut.WaitForAssertion(() => Value(cut, CameraPassword).Should().Be(RoofSettingValues.SecretSet));
        Value(cut, CameraUser).Should().Be(RoofSettingValues.SecretSet);

        var settings = (await harness.Admin().Settings.GetAsync()).Settings.ToDictionary(setting => setting.Key);
        settings[CameraServer].Value!.Value.GetString().Should().Be(SecondCameraServer);
        settings[CameraUser].IsSet.Should().BeTrue();
        settings[CameraPassword].IsSet.Should().BeTrue();
    }

    [TestMethod]
    public async Task AChange_RefusedBecauseTheSettingsChanged_ReadsThemAgain()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);
        using var admin = harness.Admin();
        var values = (await admin.Settings.GetAsync()).Settings.Where(setting => setting.Group == RoofSettingsContract.UiGroup)
            .ToDictionary(setting => setting.Key, setting => setting.Value ?? JsonSerializer.SerializeToElement<string?>(null));
        values[DefaultCamera] = JsonSerializer.SerializeToElement("Yard");
        await admin.Settings.UpdateAsync(RoofSettingsContract.UiGroup, new RoofSettingsUpdateRequest { ExpectedVersion = before, Values = values });

        Change(cut, KioskScreenTimeout, "120");

        cut.WaitForAssertion(() => cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent).ToList()
            .Should().HaveCount(2).And.EndWith("The settings were read again: check them, then try again."));
        Message(cut).Should().StartWith("Clients could not be saved. ").And.Contain("The configuration changed since it was read.");
        Version(cut).Should().Be(before + 1);
        Value(cut, DefaultCamera).Should().Be("Yard", "the page shows the settings as they are now");
    }

    [TestMethod]
    public async Task AHandEdit_IsShownToAnAdmin_AndApplied()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.EditSettingsFile("{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);

        var handEdit = cut.Find("[data-testid=hand-edit]");
        handEdit.TextContent.Should().Contain(RoofSettingsText.HandEditPending);
        cut.Find("[data-testid=hand-edit-changes] li").TextContent.Should().Be($"{DefaultCamera}: {RoofSettingValues.None} -> Yard");
        cut.Find("[data-testid=hand-edit-apply]").TextContent.Trim().Should().Be("Apply");

        cut.Find("[data-testid=hand-edit-apply]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().Be($"Applied the hand edit (settings version {before + 1})."));
        cut.FindAll("[data-testid=hand-edit]").Should().BeEmpty();
        cut.Find("[data-testid=settings-group][data-group=ui]").Click();
        cut.WaitForAssertion(() => Value(cut, DefaultCamera).Should().Be("Yard"));
    }

    [TestMethod]
    public async Task ASave_WhoseReadAfterFails_SaysItWasSaved_AndThatThePageIsFromBefore()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);
        harness.LoseAnswer = request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/Settings/Catalogue", StringComparison.Ordinal);

        Change(cut, DefaultCamera, "Pier");

        cut.WaitForAssertion(() => Lines(cut).Should().Equal(
            $"Saved (settings version {before + 1}).",
            "The settings could not be read again. The controller could not be reached. This page shows them from before the change: reload it to see them now."));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Warning));
        cut.FindAll("[data-testid=setting-editor]").Should().BeEmpty("the change was made, so it is not offered again");
        using var admin = harness.Admin();
        (await admin.Settings.GetAsync()).Settings.Single(setting => setting.Key == DefaultCamera).Value!.Value.GetString().Should().Be("Pier");

        harness.LoseAnswer = null;
        cut.Find("[data-testid=settings-group][data-group=ui]").Click();
        Change(cut, KioskScreenTimeout, "120");

        cut.WaitForAssertion(() => Lines(cut).Should().EndWith("The settings were read again: check them, then try again."));
        Version(cut).Should().Be(before + 1, "a change sent from the page from before is refused, and the settings are read again");
        Value(cut, DefaultCamera).Should().Be("Pier");
    }

    [TestMethod]
    public async Task AHandEdit_WhoseReadAfterFails_SaysItWasApplied()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.EditSettingsFile("{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var before = Version(cut);
        harness.LoseAnswer = request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/Settings/Catalogue", StringComparison.Ordinal);

        cut.Find("[data-testid=hand-edit-apply]").Click();

        cut.WaitForAssertion(() => Lines(cut).Should().Equal(
            $"Applied the hand edit (settings version {before + 1}).",
            "The settings could not be read again. The controller could not be reached. This page shows them from before the change: reload it to see them now."));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Warning));
        using var admin = harness.Admin();
        (await admin.Settings.GetAsync()).Version.Should().Be(before + 1);
    }

    [TestMethod]
    public async Task AHandEdit_IsDiscarded_OnlyOnceConfirmed()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.EditSettingsFile("{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Find("[data-testid=hand-edit-discard]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=hand-edit-discard-confirm]").TextContent.Should().Contain(RoofSettingsText.DiscardHandEditQuestion));
        File.ReadAllText(harness.SettingsPath).Should().Contain("Yard", "nothing is discarded before it is confirmed");
        cut.Find("[data-testid=hand-edit-discard-yes]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().StartWith("Discarded the hand edit (settings version "));
        cut.FindAll("[data-testid=hand-edit]").Should().BeEmpty();
        File.ReadAllText(harness.SettingsPath).Should().NotContain("Yard");
    }

    [TestMethod]
    public async Task ASafetyCriticalHandEdit_NeedsConfirming()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.EditSettingsFile("{ \"RoofControllerSecurity\": { \"AllowAnonymousStop\": true } }");
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Find("[data-testid=hand-edit-changes] li").TextContent.Should().EndWith("  [SAFETY-CRITICAL]");
        cut.FindAll("[data-testid=hand-edit-note]").Select(note => note.TextContent).Should().Contain("Applying it is safety-critical and needs confirming.");
        cut.Find("[data-testid=hand-edit-apply]").TextContent.Trim().Should().Be("Confirm and apply");

        cut.Find("[data-testid=hand-edit-apply]").Click();

        cut.WaitForAssertion(() => Message(cut).Should().StartWith("Applied the hand edit"));
        using var admin = harness.Admin();
        (await admin.Settings.GetAsync()).Settings.Single(setting => setting.Key == "RoofControllerSecurity:AllowAnonymousStop").Value!.Value.GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task AnOperator_IsToldAHandEditIsPending_AndTheirChangeIsRefused()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        harness.EditSettingsFile("{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");

        Change(cut, KioskScreenTimeout, "120");

        cut.WaitForAssertion(() => Message(cut).Should().StartWith("Clients could not be saved. ")
            .And.EndWith("The settings were read again: check them, then try again."));
        cut.FindAll("[data-testid=hand-edit]").Should().BeEmpty("only an admin reviews a hand edit");
        cut.Find("[data-testid=hand-edit-seen]").TextContent.Should().Be(RoofSettingsText.HandEditSeenByOthers);
        Setting(cut, KioskScreenTimeout).QuerySelector("[data-testid=setting-change]").Should().BeNull("the settings cannot change until an admin applies or discards the edit");
        Setting(cut, KioskScreenTimeout).QuerySelector("[data-testid=setting-read-only]")!.TextContent.Should().StartWith("Read-only: ");
        harness.Roof.Applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SignedOut_ThePageSaysSo_AndOffersToTryAgain()
    {
        using var harness = new WebAdminHarness();
        await using var context = harness.Context(session: null);

        var cut = context.Render<SettingsPage>();

        cut.WaitForAssertion(() => Message(cut).Should().Be(WebClientPage.SignedOut));
        cut.Find("[data-testid=settings-loading]").TextContent.Should().Be("The settings are not read.");
        cut.Find("[data-testid=settings-reload]").TextContent.Trim().Should().Be("Try again");
    }

    private static IRenderedComponent<SettingsPage> Loaded(BunitContext context)
    {
        var cut = context.Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=settings-summary]").Should().ContainSingle(Describe(cut)));
        return cut;
    }

    private static string Describe(IRenderedComponent<SettingsPage> cut)
        => cut.FindAll("[data-testid=page-message]").FirstOrDefault()?.TextContent ?? "the settings to be read";

    private static long Version(IRenderedComponent<SettingsPage> cut)
        => long.Parse(cut.Find("[data-testid=settings-summary] h2").TextContent["Settings version ".Length..], System.Globalization.CultureInfo.InvariantCulture);

    private static IElement Setting(IRenderedComponent<SettingsPage> cut, string key)
        => cut.Find($"[data-testid=setting][data-key='{key}']");

    private static string Value(IRenderedComponent<SettingsPage> cut, string key)
        => Setting(cut, key).QuerySelector("[data-testid=setting-value]")!.TextContent;

    private static List<string> Notes(IElement setting)
        => setting.QuerySelectorAll("[data-testid=setting-note]").Select(note => note.TextContent).ToList();

    private static List<string> Lines(IRenderedComponent<SettingsPage> cut)
        => cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent).ToList();

    private static string Message(IRenderedComponent<SettingsPage> cut)
        => string.Join(' ', cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent));

    /// <summary>Opens the setting's editor, and waits for it: a click is queued while the page still renders.</summary>
    private static void Open(IRenderedComponent<SettingsPage> cut, string key)
    {
        // The buttons are disabled while a request is in flight, and drawn again when it ends.
        cut.WaitForAssertion(() => Setting(cut, key).QuerySelector("[data-testid=setting-change]")!.HasAttribute("disabled").Should().BeFalse());
        Setting(cut, key).QuerySelector("[data-testid=setting-change]")!.Click();
        cut.WaitForElement("[data-testid=setting-editor]");
    }

    /// <summary>Types a secret in the open editor, and again (<paramref name="again"/>, or the same).</summary>
    private static void Type(IRenderedComponent<SettingsPage> cut, string key, string value, string? again = null)
    {
        cut.Find($"[data-testid=setting-input][data-key='{key}']").Input(value);
        cut.Find($"[data-testid=setting-input-again][data-key='{key}']").Input(again ?? value);
    }

    /// <summary>Opens the setting's editor, types <paramref name="text"/> and saves.</summary>
    private static void Change(IRenderedComponent<SettingsPage> cut, string key, string text)
    {
        Open(cut, key);
        cut.Find("[data-testid=setting-input]").Input(text);
        cut.Find("[data-testid=setting-save]").Click();
    }
}
