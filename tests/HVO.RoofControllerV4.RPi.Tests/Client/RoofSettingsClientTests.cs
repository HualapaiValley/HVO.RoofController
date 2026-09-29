using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Remote settings through the client library (#44): a form built from the controller's catalogue and values, edited
/// and sent back with the version, and the parsing and display every client shares.
/// </summary>
[TestClass]
public sealed class RoofSettingsClientTests
{
    private const string Watchdog = "RoofControllerOptionsV4:SafetyWatchdogTimeout";
    private const string AtSpeed = "RoofControllerOptionsV4:AtSpeedConfirmationTimeout";
    private const string OpenRelay = "RoofControllerOptionsV4:OpenRelayId";
    private const string Departure = "RoofControllerOptionsV4:DepartureReleaseTimeout";
    private const string RestartWait = "RoofControllerHostOptionsV4:RestartOnFailureWaitTime";
    private const string CameraServer = "BlueIris:BaseUrl";
    private const string CameraUser = "BlueIris:UserName";
    private const string CameraPassword = "BlueIris:Password";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";

    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-client-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "config"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The form against the controller --------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdmin_EditsAGroup_ConfirmsTheSafetyCriticalPart_AndSeesTheNewValues()
    {
        using var host = StartHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var form = await LoadFormAsync(client);

        form.Version.Should().Be(1);
        form.PendingHandEdit.Should().BeNull();
        form.Groups.Select(group => group.Name).Should().Equal(
            RoofSettingsContract.RoofGroup,
            RoofSettingsContract.ControllerGroup,
            RoofSettingsContract.CameraGroup,
            RoofSettingsContract.SecurityGroup,
            RoofSettingsContract.IdentityGroup,
            RoofSettingsContract.LoggingGroup,
            RoofSettingsContract.UiGroup);
        foreach (var group in form.Groups)
        {
            group.Fields.Select(field => field.Label).Should().OnlyHaveUniqueItems("each field in {0} needs its own label", group.Name);
        }

        var watchdog = form.FindField(Watchdog)!;
        watchdog.Label.Should().Be("Safety watchdog timeout");
        watchdog.DisplayValue.Should().Be("120 s (00:02:00)");
        watchdog.EditText.Should().Be("120");
        watchdog.DefaultValue.Should().Be("120 s (00:02:00)");
        watchdog.CanWrite.Should().BeTrue();
        watchdog.ReadOnlyReason.Should().BeNull();
        watchdog.ReadOnlyCode.Should().BeNull();
        form.FindField(Departure)!.NeedsLocalCredential.Should().BeTrue();
        form.FindField(Departure)!.CanWrite.Should().BeFalse("an admin API key is not a local credential");
        form.FindField(Departure)!.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
        form.FindField(Departure)!.ReadOnlyCode.Should().Be(RoofControllerErrorCode.SettingNotPermitted);
        form.FindField("roofcontrolleroptionsv4:safetywatchdogtimeout").Should().BeSameAs(watchdog, "keys are matched ignoring case");
        form.FindGroup("ROOF")!.Name.Should().Be(RoofSettingsContract.RoofGroup);

        var edit = form.Edit(RoofSettingsContract.RoofGroup);
        edit.TrySet(Watchdog, "1h", out var error).Should().BeFalse();
        error.Should().Be("Enter at most 600 s.");
        edit.TrySet(DefaultCamera, "Pier", out error).Should().BeFalse();
        error.Should().Be($"Default camera is in the {RoofSettingsContract.UiGroup} group, not {RoofSettingsContract.RoofGroup}.");
        edit.TrySet("RoofControllerOptionsV4:NoSuchSetting", "1", out error).Should().BeFalse();
        error.Should().Be("There is no setting 'RoofControllerOptionsV4:NoSuchSetting'.");
        edit.TrySet(Departure, "30", out error).Should().BeFalse();
        error.Should().StartWith("Departure release timeout cannot be changed: ");
        edit.TrySet(Watchdog, "2m", out error).Should().BeTrue(error);
        edit.HasChanges.Should().BeFalse("two minutes is the current value");
        edit.TrySet(Watchdog, "5m", out error).Should().BeTrue(error);
        edit.NeedsConfirmation.Should().BeFalse("the watchdog time is not safety-critical");
        edit.TrySet(AtSpeed, "", out error).Should().BeTrue(error);
        edit.Changes.Keys.Should().BeEquivalentTo(Watchdog, AtSpeed);
        edit.SafetyCriticalChanges.Select(field => field.Key).Should().Equal([AtSpeed], "turning the drive run interlock off is safety-critical");
        edit.NeedsLocalCredential.Should().BeFalse();
        edit.NeedsRestart.Should().BeFalse();

        var request = edit.ToRequest();
        request.ExpectedVersion.Should().Be(1);
        request.ConfirmSafetyCriticalChange.Should().BeFalse();
        request.Values!.Keys.Should().BeEquivalentTo(
            form.FindGroup(RoofSettingsContract.RoofGroup)!.Fields.Where(field => field.CanWrite).Select(field => field.Key),
            "the controller needs every setting in the group the caller may change");
        request.Values[Watchdog].GetDouble().Should().Be(300);
        request.Values[AtSpeed].ValueKind.Should().Be(JsonValueKind.Null);
        request.Values[OpenRelay].GetInt32().Should().Be(form.FindField(OpenRelay)!.Value!.Value.GetInt32(), "an unchanged setting keeps its value");

        var refused = await RoofClientApiTests.RefusedAsync(() => client.Settings.UpdateAsync(RoofSettingsContract.RoofGroup, request));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        refused.Code.Should().Be(RoofControllerErrorCode.ConfigurationRejected);
        refused.Detail.Should().Contain("ConfirmSafetyCriticalChange=true");

        var saved = await client.Settings.UpdateAsync(RoofSettingsContract.RoofGroup, edit.ToRequest(confirmSafetyCriticalChange: true));

        saved.Version.Should().Be(2);
        var after = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), saved);
        after.FindField(Watchdog)!.DisplayValue.Should().Be("300 s (00:05:00)");
        after.FindField(AtSpeed)!.DisplayValue.Should().Be(RoofSettingValues.None);
        after.FindField(AtSpeed)!.EditText.Should().BeEmpty();

        var stale = form.Edit(RoofSettingsContract.RoofGroup);
        stale.TrySet(Watchdog, "90", out error).Should().BeTrue(error);
        var conflict = await RoofClientApiTests.RefusedAsync(() => client.Settings.UpdateAsync(RoofSettingsContract.RoofGroup, stale.ToRequest()));
        conflict.Code.Should().Be(RoofControllerErrorCode.ConfigurationVersionConflict, "the form was read before the last change");
    }

    [TestMethod]
    public async Task AChangeThatAppliesAfterARestart_SaysSo()
    {
        using var host = StartHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var edit = (await LoadFormAsync(client)).Edit(RoofSettingsContract.ControllerGroup);

        edit.TrySet(RestartWait, "20", out var error).Should().BeTrue(error);

        edit.NeedsRestart.Should().BeTrue();
        edit.Reset(RestartWait).Should().BeTrue();
        edit.Reset(RestartWait).Should().BeFalse();
        edit.NeedsRestart.Should().BeFalse();
    }

    [TestMethod]
    public async Task ASecret_IsNeverShown_AndIsSentOnlyWhenChanged()
    {
        const string password = "test-camera-password-not-real-12";
        using var host = StartHost(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var form = await LoadFormAsync(client);
        var passwordField = form.FindField(CameraPassword)!;

        passwordField.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);
        passwordField.EditText.Should().BeEmpty();
        passwordField.DefaultValue.Should().BeEmpty();
        form.Edit(RoofSettingsContract.CameraGroup).ToRequest().Values!.Keys.Should().NotContain(CameraUser).And.NotContain(CameraPassword);

        var edit = form.Edit(RoofSettingsContract.CameraGroup);
        edit.TrySet(CameraUser, "test-camera-user", out var error).Should().BeTrue(error);
        edit.Set(CameraPassword, JsonSerializer.SerializeToElement(password));
        var saved = await client.Settings.UpdateAsync(RoofSettingsContract.CameraGroup, edit.ToRequest());

        var after = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), saved);
        after.FindField(CameraPassword)!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);
        after.FindField(CameraUser)!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);
        after.FindField(CameraPassword)!.Value.Should().BeNull("a secret's value is never returned");
        after.FindField(CameraPassword)!.IsChangedBy(RoofSettingValues.Null).Should().BeTrue("sending a secret always changes it");
        after.Edit(RoofSettingsContract.CameraGroup).ToRequest().Values!.Keys.Should().NotContain(CameraUser).And.NotContain(CameraPassword);
        JsonSerializer.Serialize(saved).Should().NotContain(password);
    }

    /// <summary>
    /// A group's secrets are typed and sent together, as the controller takes the camera's user and password only
    /// together while the proxy is on; each must be typed, alike twice, and clearing them clears those that are set.
    /// </summary>
    [TestMethod]
    public async Task AGroupsSecrets_AreSetAndClearedTogether()
    {
        const string user = "test-camera-user-not-real";
        const string password = "test-camera-password-not-real-16";
        using var host = StartHost(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var form = await LoadFormAsync(client);
        var camera = form.FindGroup(RoofSettingsContract.CameraGroup)!;
        camera.Secrets.Select(secret => secret.Key).Should().Equal(CameraUser, CameraPassword);
        RoofSettingsText.SecretsSetTogether(camera).Should().Be("User name and Password are set and cleared together.");
        RoofSettingsText.SecretsSetTogether(form.FindGroup(RoofSettingsContract.RoofGroup)!).Should().BeNull("the roof group has no secrets");

        var edit = form.Edit(RoofSettingsContract.CameraGroup);
        edit.TrySetSecrets([(user, user), ("", "")], out var error).Should().BeFalse();
        error.Should().Be("Type the new Password: User name and Password are set and cleared together. To remove them, use Clear secret.");
        edit.TrySetSecrets([(user, user + "!"), (password, password)], out error).Should().BeFalse();
        error.Should().Be("The two User name values differ.");
        edit.HasChanges.Should().BeFalse("nothing is set until every secret is typed");
        edit.Invoking(e => e.TrySetSecrets([(user, user)], out _)).Should().Throw<ArgumentException>();

        edit.TrySetSecrets([(user, user), (password, password)], out error).Should().BeTrue(error);
        edit.Changes.Keys.Should().BeEquivalentTo(CameraUser, CameraPassword);
        var saved = RoofSettingsForm.Create(
            await client.Settings.GetCatalogueAsync(),
            await client.Settings.UpdateAsync(RoofSettingsContract.CameraGroup, edit.ToRequest()));
        saved.FindField(CameraUser)!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);
        saved.FindField(CameraPassword)!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);

        var cleared = saved.Edit(RoofSettingsContract.CameraGroup);
        cleared.ClearSecrets();
        cleared.Changes.Keys.Should().BeEquivalentTo(CameraUser, CameraPassword);
        var after = RoofSettingsForm.Create(
            await client.Settings.GetCatalogueAsync(),
            await client.Settings.UpdateAsync(RoofSettingsContract.CameraGroup, cleared.ToRequest()));
        after.FindField(CameraUser)!.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);
        after.FindField(CameraPassword)!.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);

        var nothing = after.Edit(RoofSettingsContract.CameraGroup);
        nothing.ClearSecrets();
        nothing.HasChanges.Should().BeFalse("only the secrets that are set are cleared");
    }

    [TestMethod]
    public async Task ASecretLeftEmpty_IsKept_AndIsClearedOnlyWhenAskedTo()
    {
        const string password = "test-camera-password-not-real-16";
        using var host = StartHost(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var edit = (await LoadFormAsync(client)).Edit(RoofSettingsContract.CameraGroup);
        edit.TrySet(CameraUser, "test-camera-user", out var error).Should().BeTrue(error);
        edit.TrySet(CameraPassword, password, out error).Should().BeTrue(error);
        var form = RoofSettingsForm.Create(
            await client.Settings.GetCatalogueAsync(),
            await client.Settings.UpdateAsync(RoofSettingsContract.CameraGroup, edit.ToRequest()));

        // A form shows every secret as an empty box, so text left empty (or typed and then removed) sends nothing.
        var kept = form.Edit(RoofSettingsContract.CameraGroup);
        kept.TrySet(CameraPassword, "", out error).Should().BeTrue(error);
        kept.TrySet(CameraUser, "test-typed-then-removed", out error).Should().BeTrue(error);
        kept.TrySet(CameraUser, "   ", out error).Should().BeTrue(error);
        kept.ToRequest().Values!.Keys.Should().NotContain(CameraUser).And.NotContain(CameraPassword);
        kept.HasChanges.Should().BeFalse();

        var cleared = form.Edit(RoofSettingsContract.CameraGroup);
        cleared.ClearSecret(CameraUser);
        cleared.ClearSecret(CameraPassword);
        cleared.ToRequest().Values![CameraPassword].ValueKind.Should().Be(JsonValueKind.Null);
        cleared.Invoking(e => e.ClearSecret(CameraServer)).Should().Throw<ArgumentException>().WithMessage("*is not a secret.*");
        var saved = await client.Settings.UpdateAsync(RoofSettingsContract.CameraGroup, cleared.ToRequest());

        var after = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), saved);
        after.FindField(CameraPassword)!.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);
        after.FindField(CameraUser)!.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);
    }

    [TestMethod]
    public async Task AnOperator_SeesOnlyTheUiGroup_AndChangesIt()
    {
        using var host = StartHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));
        var form = await LoadFormAsync(client);

        form.Groups.Select(group => group.Name).Should().Equal(RoofSettingsContract.UiGroup);
        form.FindGroup(RoofSettingsContract.UiGroup)!.CanWrite.Should().BeTrue();
        form.Invoking(f => f.Edit(RoofSettingsContract.RoofGroup)).Should().Throw<ArgumentException>()
            .WithMessage($"There is no settings group '{RoofSettingsContract.RoofGroup}'.*");
        form.FindField(Watchdog).Should().BeNull();

        var edit = form.Edit(RoofSettingsContract.UiGroup);
        edit.TrySet(DefaultCamera, "Pier", out var error).Should().BeTrue(error);
        edit.TrySet(KioskTimeout, "10m", out error).Should().BeTrue(error);
        var saved = await client.Settings.UpdateAsync(RoofSettingsContract.UiGroup, edit.ToRequest());

        var after = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), saved);
        after.FindField(DefaultCamera)!.DisplayValue.Should().Be("Pier");
        after.FindField(KioskTimeout)!.DisplayValue.Should().Be("600 s (00:10:00)");
    }

    [TestMethod]
    public async Task AViewer_SeesTheUiGroup_ButCannotChangeIt()
    {
        using var host = StartHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        var form = await LoadFormAsync(client);
        var field = form.FindField(DefaultCamera)!;

        field.CanWrite.Should().BeFalse();
        field.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
        form.FindGroup(RoofSettingsContract.UiGroup)!.CanWrite.Should().BeFalse();
        var edit = form.Edit(RoofSettingsContract.UiGroup);
        edit.TrySet(DefaultCamera, "Pier", out var error).Should().BeFalse();
        error.Should().Be($"Default camera cannot be changed: {field.ReadOnlyReason}");
        edit.Invoking(e => e.Set(DefaultCamera, JsonSerializer.SerializeToElement("Pier"))).Should().Throw<ArgumentException>();
        edit.ToRequest().Values.Should().BeEmpty("a viewer may change nothing");
    }

    [TestMethod]
    public void AFieldTheControllerDidNotReturn_IsReadOnly()
    {
        var setting = Setting(RoofSettingType.Integer, key: "Section:Missing");
        var catalogue = new RoofSettingsCatalogueResponse([new RoofSettingsGroupDescriptor("g", "Group", "A group.", [setting])]);
        var settings = new RoofSettingsResponse(3, null, null, false, null, null, [], []);

        var form = RoofSettingsForm.Create(catalogue, settings);

        form.FindField("Section:Missing")!.CanWrite.Should().BeFalse();
        form.FindField("Section:Missing")!.ReadOnlyReason.Should().Be("The controller did not return this setting.");
        form.FindField("Section:Missing")!.DisplayValue.Should().Be(RoofSettingValues.None);
    }

    [TestMethod]
    public void ASetValue_IsCheckedLikeTypedText()
    {
        var setting = Setting(RoofSettingType.Integer, key: "Section:Relay", minimum: 1, maximum: 4, safety: RoofSettingSafety.Always);
        var catalogue = new RoofSettingsCatalogueResponse([new RoofSettingsGroupDescriptor("g", "Group", "A group.", [setting])]);
        var state = new RoofSettingState("Section:Relay", "g", JsonSerializer.SerializeToElement(2), true, "settings file", true, null, false, null);
        var edit = RoofSettingsForm.Create(catalogue, new RoofSettingsResponse(3, null, null, true, null, null, [], [state])).Edit("g");

        edit.Invoking(e => e.Set("Section:Relay", JsonSerializer.SerializeToElement(5))).Should().Throw<ArgumentException>()
            .WithMessage("Relay: Enter at most 4.*");
        edit.Set("Section:Relay", JsonSerializer.SerializeToElement(2));
        edit.HasChanges.Should().BeFalse("the value did not change");
        edit.Set("Section:Relay", JsonSerializer.SerializeToElement(3));
        edit.NeedsConfirmation.Should().BeTrue("every change of this setting is safety-critical");
        var request = edit.ToRequest(confirmSafetyCriticalChange: true);
        request.ExpectedVersion.Should().Be(3);
        request.ConfirmSafetyCriticalChange.Should().BeTrue();
        request.Values!.Should().ContainSingle().Which.Value.GetInt32().Should().Be(3);
    }

    // ---- Parsing and display --------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("Yes", true)]
    [DataRow("on", true)]
    [DataRow("1", true)]
    [DataRow(" OFF ", false)]
    [DataRow("no", false)]
    [DataRow("0", false)]
    public void ABoolean_TakesTheUsualWords(string text, bool expected)
    {
        RoofSettingValues.TryParse(Setting(RoofSettingType.Boolean), text, out var value, out var error).Should().BeTrue(error);
        value.GetBoolean().Should().Be(expected);
    }

    [TestMethod]
    [DataRow("90", 90d)]
    [DataRow("1.5", 1.5)]
    [DataRow("500ms", 0.5)]
    [DataRow("90s", 90d)]
    [DataRow("90 sec", 90d)]
    [DataRow("5m", 300d)]
    [DataRow("5 min", 300d)]
    [DataRow("2H", 7200d)]
    [DataRow("1d", 86400d)]
    [DataRow("00:02:00", 120d)]
    [DataRow("1.00:00:00", 86400d)]
    [DataRow("0:00:01.5", 1.5)]
    public void ADuration_TakesSecondsAUnitOrAClockTime(string text, double seconds)
    {
        RoofSettingValues.TryParseDuration(text, out var parsed).Should().BeTrue();
        parsed.Should().BeApproximately(seconds, 1e-9);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("-5")]
    [DataRow("5x")]
    [DataRow("5 weeks")]
    [DataRow("1:30")]
    [DataRow("-00:01:00")]
    [DataRow("99:99:99")]
    [DataRow("1e400")]
    public void ADuration_RefusesOtherText(string text)
        => RoofSettingValues.TryParseDuration(text, out _).Should().BeFalse();

    [TestMethod]
    public void EachType_ExplainsWhatItNeeds()
    {
        Refusal(Setting(RoofSettingType.Boolean), "maybe").Should().Be("Enter true or false.");
        Refusal(Setting(RoofSettingType.Boolean), " ").Should().Be("A value is required.");
        Refusal(Setting(RoofSettingType.Integer), "4.5").Should().Be("Enter a whole number.");
        Refusal(Setting(RoofSettingType.Integer, minimum: 1, maximum: 4), "0").Should().Be("Enter at least 1.");
        Refusal(Setting(RoofSettingType.Integer, minimum: 1, maximum: 3600, unit: "s"), "3601").Should().Be("Enter at most 3600 s.");
        Refusal(Setting(RoofSettingType.Number), "NaN").Should().Be("Enter a number.");
        Refusal(Setting(RoofSettingType.Number), "Infinity").Should().Be("Enter a number.");
        Refusal(Setting(RoofSettingType.Number, minimum: 0.5), "0.25").Should().Be("Enter at least 0.5.");
        Refusal(Setting(RoofSettingType.Duration), "soon").Should().Be(
            "Enter a duration in seconds (for example 90 or 1.5), with a unit (500ms, 5m, 2h), or as hh:mm:ss.");
        Refusal(Setting(RoofSettingType.Duration, minimum: 5, maximum: 600, unit: "s"), "1s").Should().Be("Enter at least 5 s.");
        Refusal(Setting(RoofSettingType.Enum, allowed: ["Trace", "Debug", "Information"]), "Warning").Should().Be(
            "Enter one of: Trace, Debug, Information.");
        Refusal(Setting(RoofSettingType.String, maximum: 5), "abcdef").Should().Be("Enter at most 5 characters.");
    }

    [TestMethod]
    public void TypedText_BecomesTheWireValue()
    {
        Parse(Setting(RoofSettingType.Integer), " -3 ").GetInt64().Should().Be(-3);
        Parse(Setting(RoofSettingType.Number), "1.5e1").GetDouble().Should().Be(15);
        Parse(Setting(RoofSettingType.Duration), "5m").GetDouble().Should().Be(300);
        Parse(Setting(RoofSettingType.Enum, allowed: ["Trace", "Debug"]), "debug").GetString().Should().Be("Debug", "the allowed spelling is sent");
        Parse(Setting(RoofSettingType.String), "").GetString().Should().BeEmpty("a string that cannot be null may be empty");
        Parse(Setting(RoofSettingType.String), " Pier ").GetString().Should().Be(" Pier ", "text is sent as typed");
        Parse(Setting(RoofSettingType.String, nullable: true), "  ").ValueKind.Should().Be(JsonValueKind.Null);
        Parse(Setting(RoofSettingType.Duration, nullable: true), "").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public void AWireValue_IsCheckedAgainstTheSetting()
    {
        RoofSettingValues.Validate(Setting(RoofSettingType.Integer), Json("\"4\"")).Should().Be("Enter a whole number.");
        RoofSettingValues.Validate(Setting(RoofSettingType.Integer), Json("4.5")).Should().Be("Enter a whole number.");
        RoofSettingValues.Validate(Setting(RoofSettingType.Duration), Json("\"00:01:00\"")).Should().Be("Enter a duration in seconds.");
        RoofSettingValues.Validate(Setting(RoofSettingType.Number), Json("true")).Should().Be("Enter a number.");
        RoofSettingValues.Validate(Setting(RoofSettingType.Boolean), Json("1")).Should().Be("Enter true or false.");
        RoofSettingValues.Validate(Setting(RoofSettingType.Enum, allowed: ["Debug"]), Json("\"debug\"")).Should().Be(
            "Enter one of: Debug.", "the wire value must use the allowed spelling");
        RoofSettingValues.Validate(Setting(RoofSettingType.String), Json("5")).Should().Be("Enter text.");
        RoofSettingValues.Validate(Setting(RoofSettingType.String), RoofSettingValues.Null).Should().Be("A value is required.");
        RoofSettingValues.Validate(Setting(RoofSettingType.String, nullable: true), RoofSettingValues.Null).Should().BeNull();
        RoofSettingValues.Validate(Setting(RoofSettingType.Integer, minimum: 1, maximum: 4), Json("4")).Should().BeNull();
    }

    [TestMethod]
    public void AValue_IsFormattedToEdit_AndDescribedToShow()
    {
        var duration = Setting(RoofSettingType.Duration, unit: "s");
        RoofSettingValues.Format(duration, null).Should().BeEmpty();
        RoofSettingValues.Format(duration, RoofSettingValues.Null).Should().BeEmpty();
        RoofSettingValues.Format(duration, Json("90.0")).Should().Be("90");
        RoofSettingValues.Format(duration, Json("1.5")).Should().Be("1.5");
        RoofSettingValues.Format(Setting(RoofSettingType.Boolean), Json("false")).Should().Be("false");

        RoofSettingValues.Describe(duration, Json("45")).Should().Be("45 s");
        RoofSettingValues.Describe(duration, Json("0.5")).Should().Be("0.5 s");
        RoofSettingValues.Describe(duration, Json("120")).Should().Be("120 s (00:02:00)");
        RoofSettingValues.Describe(duration, Json("90000")).Should().Be("90000 s (1.01:00:00)");
        RoofSettingValues.Describe(duration, null).Should().Be(RoofSettingValues.None);
        RoofSettingValues.Describe(Setting(RoofSettingType.Integer, unit: "s"), Json("20")).Should().Be("20 s");
        RoofSettingValues.Describe(Setting(RoofSettingType.Integer), Json("20")).Should().Be("20");
        RoofSettingValues.Describe(Setting(RoofSettingType.String, unit: "chars"), Json("\"Pier\"")).Should().Be("Pier", "text has no unit");

        foreach (var text in new[] { "0.5", "90", "1.25", "3600" })
        {
            RoofSettingValues.TryParse(duration, RoofSettingValues.Format(duration, Parse(duration, text)), out var again, out _).Should().BeTrue();
            again.GetDouble().Should().Be(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), "a formatted value parses back to itself");
        }
    }

    [TestMethod]
    [DataRow("RoofControllerOptionsV4:SafetyWatchdogTimeout", "Safety watchdog timeout")]
    [DataRow("RoofControllerOptionsV4:OpenRelayId", "Open relay ID")]
    [DataRow("RoofControllerSecurity:RequireHttps", "Require HTTPS")]
    [DataRow("BlueIris:BaseUrl", "Base URL")]
    [DataRow("RoofControllerSecurity:Identity:SignInAttemptsPerMinute", "Sign in attempts per minute")]
    [DataRow("Logging:LogLevel:Default", "Log level (Default)")]
    [DataRow("Logging:LogLevel:Microsoft.AspNetCore", "Log level (Microsoft.AspNetCore)")]
    [DataRow("RoofControllerUi:KioskScreenTimeout", "Kiosk screen timeout")]
    [DataRow("Pin", "PIN")]
    public void ALabel_IsMadeFromTheKey(string key, string label) => RoofSettingValues.GetLabel(key).Should().Be(label);

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private RoofApiTestHost StartHost(IDictionary<string, string?>? settings = null)
    {
        var roof = new SettingsApiTests.RoofDouble();
        var host = new RoofApiTestHost(
            roof.Mock,
            settings: settings,
            configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000),
            settingsFilePath: Path.Combine(_directory, "config", "appsettings.Local.json"),
            secretsFilePath: Path.Combine(_directory, "secrets", "managed-secrets.json"));
        roof.StartFrom(host);
        return host;
    }

    private static async Task<RoofSettingsForm> LoadFormAsync(RoofControllerClient client)
        => RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), await client.Settings.GetAsync());

    private static RoofSettingDescriptor Setting(
        RoofSettingType type,
        string key = "Section:Value",
        bool nullable = false,
        double? minimum = null,
        double? maximum = null,
        string? unit = null,
        string[]? allowed = null,
        RoofSettingSafety safety = RoofSettingSafety.None)
        => new(key, "g", type, "A setting.", RoofControllerApiContract.AdminRole, RoofControllerApiContract.ViewerRole, safety,
            AppliesAfterRestart: false, LocalOnly: false, Secret: false, nullable, minimum, maximum, allowed, unit, Default: null);

    private static JsonElement Parse(RoofSettingDescriptor setting, string text)
    {
        RoofSettingValues.TryParse(setting, text, out var value, out var error).Should().BeTrue(error);
        return value;
    }

    private static string? Refusal(RoofSettingDescriptor setting, string text)
    {
        RoofSettingValues.TryParse(setting, text, out _, out var error).Should().BeFalse();
        return error;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
