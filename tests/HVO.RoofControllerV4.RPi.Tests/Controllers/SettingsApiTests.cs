using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// The settings API end to end against real settings files: the catalogue each role sees, a change saved and loaded
/// again at a restart, the version check, each role's groups, safety-critical and local-only settings, secrets, hand
/// edits of the file, a file the controller cannot use, and the restart endpoint. The roof service is a double that
/// keeps the options it is given, so no test needs the HAT.
/// </summary>
[TestClass]
public sealed class SettingsApiTests
{
    private const string Settings = "/api/v4.0/Settings";
    private const string Restart = "/api/v4.0/System/Restart";
    private const string Auth = "/api/v4.0/Auth";
    private const string Identity = "/api/v4.0/Identity";

    private const string Watchdog = "RoofControllerOptionsV4:SafetyWatchdogTimeout";
    private const string AtSpeed = "RoofControllerOptionsV4:AtSpeedConfirmationTimeout";
    private const string OpenRelay = "RoofControllerOptionsV4:OpenRelayId";
    private const string CloseRelay = "RoofControllerOptionsV4:CloseRelayId";
    private const string Departure = "RoofControllerOptionsV4:DepartureReleaseTimeout";
    private const string AnonymousStop = "RoofControllerSecurity:AllowAnonymousStop";
    private const string LockoutThreshold = "RoofControllerSecurity:Identity:LockoutThreshold";
    private const string CameraServer = "BlueIris:BaseUrl";
    private const string CameraUser = "BlueIris:UserName";
    private const string CameraPassword = "BlueIris:Password";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";

    private const string LocalAdminKey = "test-local-admin-key-not-a-real-secret-06";
    private const string LocalKioskKey = "test-local-kiosk-key-not-a-real-secret-07";
    private const string RemoteKioskKey = "test-remote-kiosk-key-not-a-real-secret-08";

    private string _directory = null!;

    private string ConfigDirectory => Path.Combine(_directory, "config");

    private string SettingsPath => Path.Combine(ConfigDirectory, "appsettings.Local.json");

    private string SecretsDirectory => Path.Combine(_directory, "secrets");

    private string SecretsPath => Path.Combine(SecretsDirectory, "managed-secrets.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-settings-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // A test may leave a directory read-only.
            foreach (var directory in Directory.GetDirectories(_directory, "*", SearchOption.AllDirectories).Prepend(_directory))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_directory, recursive: true);
    }

    // ---- The catalogue ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCatalogue_ShowsEachRoleTheGroupsItMayRead_WithEachSettingsRules()
    {
        using var host = StartHost(new RoofDouble());
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        using var @operator = host.CreateApiClient(TestApiKeys.Operator);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        using var anonymous = host.CreateApiClient();

        (await anonymous.GetAsync($"{Settings}/Catalogue")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var client in new[] { viewer, @operator })
        {
            var visible = await ApiJson.ReadAsync<RoofSettingsCatalogueResponse>(await client.GetAsync($"{Settings}/Catalogue"));
            visible.Groups.Select(group => group.Name).Should().Equal(RoofSettingsContract.UiGroup);
        }

        var catalogue = await ApiJson.ReadAsync<RoofSettingsCatalogueResponse>(await admin.GetAsync($"{Settings}/Catalogue"));
        catalogue.Groups.Select(group => group.Name).Should().Equal(
            RoofSettingsContract.RoofGroup,
            RoofSettingsContract.ControllerGroup,
            RoofSettingsContract.CameraGroup,
            RoofSettingsContract.SecurityGroup,
            RoofSettingsContract.IdentityGroup,
            RoofSettingsContract.LoggingGroup,
            RoofSettingsContract.UiGroup);
        var settings = catalogue.Groups.SelectMany(group => group.Settings).ToDictionary(setting => setting.Key);
        settings[OpenRelay].Safety.Should().Be(RoofSettingSafety.Always);
        settings[OpenRelay].WriteRole.Should().Be(RoofControllerApiContract.AdminRole);
        settings["RoofControllerOptionsV4:OperatorLeaseTimeout"].Safety.Should().Be(RoofSettingSafety.WhenTurnedOff);
        settings["RoofControllerOptionsV4:OperatorLeaseTimeout"].Nullable.Should().BeTrue();
        settings[Departure].LocalOnly.Should().BeTrue();
        settings[CameraPassword].Secret.Should().BeTrue();
        settings[CameraPassword].Default.Should().BeNull("a secret's value is never answered, not even its default");
        settings[Watchdog].Default!.Value.GetDouble().Should().Be(120, "the Development environment's shipped default");
        settings[Watchdog].Minimum.Should().Be(5);
        settings[Watchdog].Maximum.Should().Be(600);
        settings[Watchdog].Unit.Should().Be("s");
        settings["RoofControllerHostOptionsV4:RestartOnFailureWaitTime"].AppliesAfterRestart.Should().BeTrue();
        settings[DefaultCamera].WriteRole.Should().Be(RoofControllerApiContract.OperatorRole);
        settings[DefaultCamera].ReadRole.Should().Be(RoofControllerApiContract.ViewerRole);
    }

    // ---- Changing settings -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AChange_IsSavedToTheSettingsFile_AndSurvivesARestart()
    {
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using (var host = StartHost(roof, logs))
        {
            using var admin = host.CreateApiClient(TestApiKeys.Admin);
            var before = await GetAsync(admin);
            before.Version.Should().Be(1);
            before.FileBacked.Should().BeTrue();
            before.FilePath.Should().Be(SettingsPath);
            before.PendingHandEdit.Should().BeNull();
            before.Warnings.Should().BeEmpty();

            var request = await ReadGroupAsync(admin, RoofSettingsContract.RoofGroup, values =>
            {
                values[Watchdog] = Json("300");
                values[AtSpeed] = Json("null");
            }, confirm: true);
            var response = await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, request);

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var after = await ApiJson.ReadAsync<RoofSettingsResponse>(response);
            after.Version.Should().Be(2);
            after.SavedBy.Should().Be("test-admin");
            after.SavedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            State(after, Watchdog).Value!.Value.GetDouble().Should().Be(300);
            State(after, Watchdog).Source.Should().Be("settings file");
            State(after, AtSpeed).Value.Should().BeNull();
            State(after, AtSpeed).IsSet.Should().BeFalse();
            roof.Applied.Should().ContainSingle().Which.IncludeLocalOnly.Should().BeFalse("an admin API key is not a local credential");
            roof.Current.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromMinutes(5));
            logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
                && entry.Message.StartsWith("AUDIT settings changed by test-admin (version 1 -> 2, safety-critical: True)", StringComparison.Ordinal)
                && entry.Message.Contains(Watchdog, StringComparison.Ordinal));
        }

        var file = ReadFile(SettingsPath);
        file[RoofSettingsFile.MetadataProperty]!["Version"]!.GetValue<long>().Should().Be(2);
        file[RoofSettingsFile.MetadataProperty]!["SavedBy"]!.GetValue<string>().Should().Be("test-admin");
        file.Select(property => property.Key).Should().BeEquivalentTo(RoofSettingsFile.MetadataProperty, "RoofControllerOptionsV4");
        var roofSection = file["RoofControllerOptionsV4"]!.AsObject();
        roofSection.Select(property => property.Key).Should().BeEquivalentTo(
            ["SafetyWatchdogTimeout", "AtSpeedConfirmationTimeout"],
            "the file holds only what differs from the shipped defaults");
        roofSection["SafetyWatchdogTimeout"]!.GetValue<string>().Should().Be("00:05:00");
        roofSection["AtSpeedConfirmationTimeout"].Should().BeNull("null turns off a check the shipped defaults turn on");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(SettingsPath).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        Directory.GetFiles(ConfigDirectory).Should().Equal([SettingsPath], "the temporary file is renamed over the file");

        var restarted = new RoofDouble();
        using (var host = StartHost(restarted))
        {
            var options = host.Services.GetRequiredService<IOptions<RoofControllerOptionsV4>>().Value;
            options.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromMinutes(5));
            options.AtSpeedConfirmationTimeout.Should().BeNull();
            using var admin = host.CreateApiClient(TestApiKeys.Admin);
            var settings = await GetAsync(admin);
            settings.Version.Should().Be(2);
            settings.SavedBy.Should().Be("test-admin");
            State(settings, Watchdog).Value!.Value.GetDouble().Should().Be(300);
        }
    }

    [TestMethod]
    public async Task AChangeFromAStaleRead_IsRefused_AndOneWithoutAVersionIsInvalid()
    {
        using var host = StartHost(new RoofDouble());
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var first = await ReadGroupAsync(admin, RoofSettingsContract.UiGroup, values => values[DefaultCamera] = Json("\"Roof\""));
        var second = await ReadGroupAsync(admin, RoofSettingsContract.UiGroup, values => values[DefaultCamera] = Json("\"Yard\""));

        (await PostGroupAsync(admin, RoofSettingsContract.UiGroup, first)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.UiGroup, second));
        stale.Status.Should().Be(409);
        stale.Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationVersionConflict));
        stale.Detail.Should().Contain("expected version 1, current version 2");
        (await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.UiGroup, second with { ExpectedVersion = 99 })))
            .Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationVersionConflict));

        using var noVersion = new StringContent("{\"values\":{}}", Encoding.UTF8, "application/json");
        (await admin.PostAsync($"{Settings}/{RoofSettingsContract.UiGroup}", noVersion)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var settings = await GetAsync(admin);
        settings.Version.Should().Be(2);
        State(settings, DefaultCamera).Value!.Value.GetString().Should().Be("Roof");
    }

    [TestMethod]
    public async Task ARequestThatLeavesOutASetting_NamesAnotherGroupsOrIsOutOfRange_IsInvalid_AndNothingIsSaved()
    {
        var roof = new RoofDouble();
        using var host = StartHost(roof);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var request = await ReadGroupAsync(admin, RoofSettingsContract.RoofGroup);

        var missing = request with { Values = request.Values!.Where(entry => entry.Key != Watchdog).ToDictionary() };
        (await ErrorsAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, missing)))
            .Should().Contain($"{Watchdog} is required: send every setting of the group you may change.");

        var unknown = request with { Values = new(request.Values!) { ["RoofControllerOptionsV4:NoSuchSetting"] = Json("1") } };
        (await ErrorsAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, unknown)))
            .Should().Contain(error => error.Contains("is not a setting of the roof group", StringComparison.Ordinal));

        var otherGroup = request with { Values = new(request.Values!) { [DefaultCamera] = Json("\"Roof\"") } };
        (await ErrorsAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, otherGroup)))
            .Should().Contain(error => error.Contains("is not a setting of the roof group", StringComparison.Ordinal));

        var outOfRange = request with { Values = new(request.Values!) { [Watchdog] = Json("1") } };
        (await ErrorsAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, outOfRange)))
            .Should().Contain(error => error.Contains("must be between 5 and 600 seconds", StringComparison.Ordinal));

        var noSuchGroup = await ProblemAsync(await PostGroupAsync(admin, "nosuchgroup", request));
        noSuchGroup.Status.Should().Be(404);
        noSuchGroup.Code.Should().Be(nameof(RoofControllerErrorCode.SettingNotFound));

        File.Exists(SettingsPath).Should().BeFalse();
        roof.Applied.Should().BeEmpty();
        (await GetAsync(admin)).Version.Should().Be(1);
    }

    [TestMethod]
    public async Task EachRole_ChangesOnlyTheGroupsItMayWrite_AndReadsOnlyTheGroupsItMayRead()
    {
        using var host = StartHost(new RoofDouble());
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        using var @operator = host.CreateApiClient(TestApiKeys.Operator);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var operatorView = await GetAsync(@operator);
        operatorView.Settings.Select(state => state.Group).Distinct().Should().Equal(RoofSettingsContract.UiGroup);
        operatorView.FilePath.Should().BeNull("only an admin is told where the file is");
        operatorView.Settings.Should().OnlyContain(state => state.CanWrite);
        (await GetAsync(viewer)).Settings.Should().OnlyContain(state => !state.CanWrite && state.Group == RoofSettingsContract.UiGroup);

        var uiChange = await ReadGroupAsync(@operator, RoofSettingsContract.UiGroup, values => values[DefaultCamera] = Json("\"Pier\""));
        (await PostGroupAsync(viewer, RoofSettingsContract.UiGroup, uiChange)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var changed = await PostGroupAsync(@operator, RoofSettingsContract.UiGroup, uiChange);
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        State(await ApiJson.ReadAsync<RoofSettingsResponse>(changed), DefaultCamera).Value!.Value.GetString().Should().Be("Pier");

        var identity = new RoofSettingsUpdateRequest
        {
            ExpectedVersion = 2,
            Values = new Dictionary<string, JsonElement> { [LockoutThreshold] = Json("3") }
        };
        var refused = await ProblemAsync(await PostGroupAsync(@operator, RoofSettingsContract.IdentityGroup, identity));
        refused.Status.Should().Be(403);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.SettingNotPermitted));
        refused.Detail.Should().Be($"Changing the identity settings needs the {RoofControllerApiContract.AdminRole} role.");

        var adminChange = await ReadGroupAsync(admin, RoofSettingsContract.IdentityGroup, values => values[LockoutThreshold] = Json("3"));
        var accepted = await PostGroupAsync(admin, RoofSettingsContract.IdentityGroup, adminChange);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        State(await ApiJson.ReadAsync<RoofSettingsResponse>(accepted), LockoutThreshold).Value!.Value.GetInt32().Should().Be(3);
    }

    [TestMethod]
    [DataRow("relays swapped")]
    [DataRow("at-speed check off")]
    [DataRow("anonymous stop changed")]
    public async Task ASafetyCriticalChange_IsRefusedUntilConfirmed_AndIsAuditedAsSuch(string change)
    {
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using var host = StartHost(roof, logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var group = change == "anonymous stop changed" ? RoofSettingsContract.SecurityGroup : RoofSettingsContract.RoofGroup;
        var request = await ReadGroupAsync(admin, group, values =>
        {
            switch (change)
            {
                case "relays swapped":
                    values[OpenRelay] = Json("2");
                    values[CloseRelay] = Json("1");
                    break;
                case "at-speed check off":
                    values[AtSpeed] = Json("null");
                    break;
                default:
                    values[AnonymousStop] = Json(values[AnonymousStop].GetBoolean() ? "false" : "true");
                    break;
            }
        });

        var refused = await ProblemAsync(await PostGroupAsync(admin, group, request));

        refused.Status.Should().Be(409);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationRejected));
        refused.Detail.Should().Contain("is safety-critical").And.Contain("ConfirmSafetyCriticalChange=true");
        roof.Applied.Should().BeEmpty();
        File.Exists(SettingsPath).Should().BeFalse();
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("Settings change by test-admin rejected: safety-critical change not confirmed", StringComparison.Ordinal));

        var confirmed = await PostGroupAsync(admin, group, request with { ConfirmSafetyCriticalChange = true });

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofSettingsResponse>(confirmed)).Version.Should().Be(2);
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("AUDIT settings changed by test-admin (version 1 -> 2, safety-critical: True)", StringComparison.Ordinal));
        if (change == "relays swapped")
        {
            roof.Current.OpenRelayId.Should().Be(2);
            roof.Current.CloseRelayId.Should().Be(1);
        }
    }

    [TestMethod]
    public async Task AnOrdinaryChange_IsAuditedAtInformation_WithTheValues()
    {
        var logs = new RecordingLoggerProvider();
        using var host = StartHost(new RoofDouble(), logs);
        using var @operator = host.CreateApiClient(TestApiKeys.Operator);

        var request = await ReadGroupAsync(@operator, RoofSettingsContract.UiGroup, values => values[KioskTimeout] = Json("600"));
        (await PostGroupAsync(@operator, RoofSettingsContract.UiGroup, request)).StatusCode.Should().Be(HttpStatusCode.OK);

        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Information
            && entry.Message.StartsWith("AUDIT settings changed by test-operator (version 1 -> 2, safety-critical: False)", StringComparison.Ordinal)
            && entry.Message.Contains($"{KioskTimeout}: 300 s -> 600 s", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ALocalOnlySetting_IsChangedOnlyWithALocalCredential()
    {
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using var host = StartHost(roof, logs, extraSettings: LocalKeys());
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        using var localAdmin = host.CreateApiClient(LocalAdminKey);

        var remoteView = State(await GetAsync(admin), Departure);
        remoteView.CanWrite.Should().BeFalse();
        remoteView.ReadOnlyReason.Should().Contain("is local-only");
        var remote = await ReadGroupAsync(admin, RoofSettingsContract.RoofGroup);
        remote.Values.Should().NotContainKey(Departure);
        var refused = await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup,
            remote with { Values = new(remote.Values!) { [Departure] = Json("9") } }));
        refused.Status.Should().Be(403);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.SettingNotPermitted));
        refused.Detail.Should().Contain($"{Departure} is local-only");
        roof.Applied.Should().BeEmpty();

        State(await GetAsync(localAdmin), Departure).CanWrite.Should().BeTrue();
        var local = await ReadGroupAsync(localAdmin, RoofSettingsContract.RoofGroup, values => values[Departure] = Json("9"));
        var accepted = await PostGroupAsync(localAdmin, RoofSettingsContract.RoofGroup, local);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        roof.Applied.Should().ContainSingle().Which.IncludeLocalOnly.Should().BeTrue();
        roof.Current.DepartureReleaseTimeout.Should().Be(TimeSpan.FromSeconds(9));

        // An admin's PIN at the controller's own kiosk is a local credential; at another kiosk it is not.
        await AddUserAsync(host, "lena", RoofControllerApiContract.AdminRole);
        using var atTheController = await PinSessionAsync(host, LocalKioskKey, "lena");
        var pin = await ReadGroupAsync(atTheController, RoofSettingsContract.RoofGroup, values => values[Departure] = Json("10"));
        var pinAccepted = await PostGroupAsync(atTheController, RoofSettingsContract.RoofGroup, pin);
        pinAccepted.StatusCode.Should().Be(HttpStatusCode.OK, await pinAccepted.Content.ReadAsStringAsync());
        roof.Current.DepartureReleaseTimeout.Should().Be(TimeSpan.FromSeconds(10));
        logs.Entries.Should().Contain(entry => entry.Message.StartsWith("AUDIT settings changed by lena (PIN at test-local-kiosk)", StringComparison.Ordinal));

        using var elsewhere = await PinSessionAsync(host, RemoteKioskKey, "lena");
        State(await GetAsync(elsewhere), Departure).CanWrite.Should().BeFalse();
        var fromElsewhere = await ReadGroupAsync(elsewhere, RoofSettingsContract.RoofGroup);
        (await ProblemAsync(await PostGroupAsync(elsewhere, RoofSettingsContract.RoofGroup,
            fromElsewhere with { Values = new(fromElsewhere.Values!) { [Departure] = Json("11") } })))
            .Code.Should().Be(nameof(RoofControllerErrorCode.SettingNotPermitted));
        roof.Current.DepartureReleaseTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task ASecret_IsWriteOnly_KeptInTheManagedSecretsFile_AndNeverAnswered()
    {
        const string password = "test-camera-password-not-real-09";
        var logs = new RecordingLoggerProvider();
        using var host = StartHost(new RoofDouble(), logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var request = await ReadGroupAsync(admin, RoofSettingsContract.CameraGroup, values =>
        {
            values[CameraUser] = Json("\"test-camera-user\"");
            values[CameraPassword] = Json(JsonSerializer.Serialize(password));
        });
        var response = await PostGroupAsync(admin, RoofSettingsContract.CameraGroup, request);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain(password);
        var settings = JsonSerializer.Deserialize<RoofSettingsResponse>(text, ApiJson.Options)!;
        settings.Version.Should().Be(2);
        foreach (var key in new[] { CameraUser, CameraPassword })
        {
            State(settings, key).Value.Should().BeNull("a secret is write-only");
            State(settings, key).IsSet.Should().BeTrue();
            State(settings, key).Source.Should().Be("managed secrets");
        }

        (await (await admin.GetAsync(Settings)).Content.ReadAsStringAsync()).Should().NotContain(password);
        (await (await admin.GetAsync($"{Settings}/Catalogue")).Content.ReadAsStringAsync()).Should().NotContain(password);
        File.ReadAllText(SettingsPath).Should().NotContain(password).And.NotContain("Password");
        File.ReadAllText(SecretsPath).Should().Contain(password);
        logs.Entries.Should().NotContain(entry => entry.Message.Contains(password, StringComparison.Ordinal));
        logs.Entries.Should().Contain(entry => entry.Message.Contains($"{CameraPassword}: (not set) -> (set)", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(SecretsPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(SecretsDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The same secret again is still a change, so the answer never tells whether a guess matched.
        var again = await PostGroupAsync(admin, RoofSettingsContract.CameraGroup, request with { ExpectedVersion = 2 });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiJson.ReadAsync<RoofSettingsResponse>(again)).Version.Should().Be(3);

        var clear = await ReadGroupAsync(admin, RoofSettingsContract.CameraGroup, values => values[CameraPassword] = Json("null"));
        var cleared = await PostGroupAsync(admin, RoofSettingsContract.CameraGroup, clear);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        State(await ApiJson.ReadAsync<RoofSettingsResponse>(cleared), CameraPassword).IsSet.Should().BeFalse();
        File.ReadAllText(SecretsPath).Should().NotContain(password);
    }

    [TestMethod]
    public async Task MovingTheCameraToAnotherServer_NeedsTheCredentialsInTheSameRequest()
    {
        const string password = "test-camera-password-not-real-10";
        var logs = new RecordingLoggerProvider();
        using var host = StartHost(new RoofDouble(), logs, extraSettings: new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        await ChangeCameraAsync(values => values[CameraServer] = Json("\"http://192.168.0.5:81\""), "no credentials are set");
        await ChangeCameraAsync(values =>
        {
            values[CameraUser] = Json("\"test-camera-user\"");
            values[CameraPassword] = Json(JsonSerializer.Serialize(password));
        });
        await ChangeCameraAsync(values => values[CameraServer] = Json("\"HTTP://192.168.0.5:81/\""), "the server stays the same");

        foreach (var server in new[] { "http://camera.example.net:81", "http://192.168.0.5:82", "https://192.168.0.5:81" })
        {
            var version = (await GetAsync(admin)).Version;
            var refused = await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.CameraGroup,
                await ReadGroupAsync(admin, RoofSettingsContract.CameraGroup, values => values[CameraServer] = Json(JsonSerializer.Serialize(server)))));

            refused.Status.Should().Be(409, server);
            refused.Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationRejected));
            refused.Detail.Should().StartWith($"Moving the camera proxy to another server ({CameraServer}) would send it the Blue Iris user")
                .And.Contain($"Send {CameraUser} and {CameraPassword} with it (null clears them).");
            var after = await GetAsync(admin);
            after.Version.Should().Be(version, "nothing is saved");
            State(after, CameraServer).Value!.Value.GetString().Should().Be("HTTP://192.168.0.5:81/");
        }

        await ChangeCameraAsync(values => values[CameraUser] = Json("\"test-camera-user\""), "one credential alone does not move the server");
        await ChangeCameraAsync(values =>
        {
            values[CameraServer] = Json("\"http://192.168.0.6:81\"");
            values[CameraUser] = Json("\"test-camera-user-2\"");
            values[CameraPassword] = Json("\"test-camera-password-not-real-11\"");
        }, "new credentials are sent with the move");
        await ChangeCameraAsync(values =>
        {
            values[CameraServer] = Json("\"http://192.168.0.7:81\"");
            values[CameraUser] = Json("null");
            values[CameraPassword] = Json("null");
        }, "the move clears the credentials");
        await ChangeCameraAsync(values => values[CameraServer] = Json("\"http://192.168.0.8:81\""), "no credentials are set any more");
        logs.Entries.Should().NotContain(entry => entry.Message.Contains(password, StringComparison.Ordinal));

        async Task ChangeCameraAsync(Action<Dictionary<string, JsonElement>> change, string because = "")
        {
            var request = await ReadGroupAsync(admin, RoofSettingsContract.CameraGroup, change);
            var response = await PostGroupAsync(admin, RoofSettingsContract.CameraGroup, request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{because}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    [TestMethod]
    public async Task AChange_WhileTheRoofMoves_IsRefused_AndNothingIsSaved()
    {
        var roof = new RoofDouble { Moving = true };
        using var host = StartHost(roof);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var request = await ReadGroupAsync(admin, RoofSettingsContract.RoofGroup, values => values[Watchdog] = Json("300"));
        var refused = await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, request));

        refused.Status.Should().Be(409);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.OperationInProgress));
        File.Exists(SettingsPath).Should().BeFalse();
        (await GetAsync(admin)).Version.Should().Be(1);
        roof.Current.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromMinutes(2));
    }

    [TestMethod]
    public async Task AChangeThatCannotBeSaved_IsRefused_AndTheRoofIsPutBack()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
            return;
        }

        var roof = new RoofDouble();
        using var host = StartHost(roof);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var request = await ReadGroupAsync(admin, RoofSettingsContract.RoofGroup, values => values[Watchdog] = Json("300"));
        File.SetUnixFileMode(ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (RoofSettingsFile.CheckWritable(SettingsPath, RoofSettingsFileKind.Settings) is null)
        {
            Assert.Inconclusive("The directory is still writable (the tests run as root).");
        }

        var refused = await ProblemAsync(await PostGroupAsync(admin, RoofSettingsContract.RoofGroup, request));

        refused.Status.Should().Be(503);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.SettingsStoreUnavailable));
        refused.Detail.Should().StartWith("The settings could not be saved, so nothing was changed.");
        roof.Applied.Should().HaveCount(2);
        roof.Applied[1].IncludeLocalOnly.Should().BeTrue("the roof is put back as it was, local-only settings included");
        roof.Current.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromMinutes(2));
        (await GetAsync(admin)).Version.Should().Be(1);
    }

    [TestMethod]
    public async Task ASettingAHigherLayerSets_IsReadOnly_AndTheRestOfItsGroupCanStillChange()
    {
        using var host = StartHost(new RoofDouble(), hostSettings: new Dictionary<string, string?> { [DefaultCamera] = "Pinned" });
        using var @operator = host.CreateApiClient(TestApiKeys.Operator);

        var state = State(await GetAsync(@operator), DefaultCamera);
        state.Value!.Value.GetString().Should().Be("Pinned");
        state.Source.Should().Be("command line");
        state.CanWrite.Should().BeFalse();
        state.ReadOnlyReason.Should().Contain("is set by the command line, which takes precedence over the settings file");

        var request = await ReadGroupAsync(@operator, RoofSettingsContract.UiGroup, values => values[KioskTimeout] = Json("600"));
        request.Values.Should().NotContainKey(DefaultCamera);
        var refused = await ProblemAsync(await PostGroupAsync(@operator, RoofSettingsContract.UiGroup,
            request with { Values = new(request.Values!) { [DefaultCamera] = Json("\"Yard\"") } }));
        refused.Status.Should().Be(403);
        refused.Detail.Should().Contain("set by the command line");

        var accepted = await PostGroupAsync(@operator, RoofSettingsContract.UiGroup, request);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        State(await ApiJson.ReadAsync<RoofSettingsResponse>(accepted), KioskTimeout).Value!.Value.GetDouble().Should().Be(600);
    }

    [TestMethod]
    public async Task WithoutASettingsFile_ChangesAreHeldInMemory_AndTheAnswerSaysSo()
    {
        using var host = StartHost(new RoofDouble(), files: false);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var before = await GetAsync(admin);
        before.FileBacked.Should().BeFalse();
        before.FilePath.Should().BeNull();
        before.Warnings.Should().Contain(warning => warning.StartsWith("Settings changes are held in memory only", StringComparison.Ordinal));

        var request = await ReadGroupAsync(admin, RoofSettingsContract.UiGroup, values => values[DefaultCamera] = Json("\"Roof\""));
        var response = await PostGroupAsync(admin, RoofSettingsContract.UiGroup, request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofSettingsResponse>(response)).Version.Should().Be(2);
        host.Services.GetRequiredService<IOptionsMonitor<RoofControllerUiOptions>>().CurrentValue.DefaultCamera.Should().Be("Roof");
        Directory.GetFiles(ConfigDirectory).Should().BeEmpty();
    }

    // ---- A file the controller cannot use ----------------------------------------------------------------------------

    [TestMethod]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"half-written", "is not valid JSON (line 1")]
    [DataRow("[ \"half-written\" ]", "must hold a JSON object")]
    [DataRow("{ \"BlueIris\": { \"Password\": \"half-written\" } }", "sets BlueIris:Password, which is a secret")]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"half-written\", \"defaultcamera\": \"x\" } }", "is not valid")]
    public void ATornOrCorruptSettingsFile_IsRefusedWhenTheConfigurationIsLoaded_NamingTheFileButNoValue(string content, string expected)
    {
        File.WriteAllText(SettingsPath, content);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [RoofSettingsConfiguration.FilePathKey] = SettingsPath });

        var problem = FluentActions.Invoking(() => configuration.AddRoofSettingsFiles("Production", _directory))
            .Should().Throw<RoofSettingsFileException>().Which.Message;

        problem.Should().Contain($"'{SettingsPath}'").And.Contain(expected).And.NotContain("half-written");
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"half-written", "is not valid JSON")]
    [DataRow("{ \"RoofControllerOptionsV4\": { \"SafetyWatchdogTimeout\": \"00:00:01\" } }", "SafetyWatchdogTimeout must be between 5 and 600 seconds")]
    public void TheController_WithASettingsFileItCannotUse_ExitsWithCode1_BeforeTheHostIsBuilt(string content, string expected)
    {
        // The class of tests that replace the console runs alone ([DoNotParallelize]).
        File.WriteAllText(SettingsPath, content);
        var error = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        int exitCode;
        try
        {
            exitCode = Program.Main(["--contentRoot", _directory, "--environment", "Production", $"--{RoofSettingsConfiguration.FilePathKey}", SettingsPath]);
        }
        finally
        {
            Console.SetError(error);
        }

        exitCode.Should().Be(1);
        output.ToString().Should().StartWith("The roof controller did not start: ")
            .And.Contain(SettingsPath)
            .And.Contain(expected)
            .And.NotContain("half-written");
    }

    // ---- Hand edits --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AHandEdit_IsShownAsPending_BlocksChanges_UntilAnAdminReloadsIt()
    {
        File.WriteAllText(SettingsPath, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Roof\" } }");
        var logs = new RecordingLoggerProvider();
        using var host = StartHost(new RoofDouble(), logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        using var @operator = host.CreateApiClient(TestApiKeys.Operator);
        (await GetAsync(admin)).PendingHandEdit.Should().BeNull();

        File.WriteAllText(SettingsPath, """
            // Edited by hand on the controller.
            {
              "RoofControllerUi": { "DefaultCamera": "Yard", "KioskScreenTimeout": "00:02:00", },
            }
            """);

        var pending = await GetAsync(admin);
        var edit = pending.PendingHandEdit!;
        edit.Should().NotBeNull();
        edit.Changes.Select(change => change.Key).Should().BeEquivalentTo(DefaultCamera, KioskTimeout);
        edit.Problems.Should().BeEmpty();
        edit.FileProblem.Should().BeNull();
        edit.RequiresConfirmation.Should().BeFalse();
        edit.RequiresLocalCredential.Should().BeFalse();
        pending.Warnings.Should().Contain("The settings file was edited outside the API. Review the pending edit, then reload or discard it.");
        State(pending, DefaultCamera).Value!.Value.GetString().Should().Be("Roof", "a hand edit takes effect only when reloaded");
        State(pending, DefaultCamera).CanWrite.Should().BeFalse();
        host.Services.GetRequiredService<IOptionsMonitor<RoofControllerUiOptions>>().CurrentValue.DefaultCamera.Should().Be("Roof");

        var operatorView = await GetAsync(@operator);
        operatorView.PendingHandEdit.Should().BeNull("only an admin reviews an edit");
        State(operatorView, DefaultCamera).ReadOnlyReason.Should().Contain("edited outside the API");
        var blocked = await ProblemAsync(await PostGroupAsync(@operator, RoofSettingsContract.UiGroup, new RoofSettingsUpdateRequest
        {
            ExpectedVersion = 1,
            Values = new Dictionary<string, JsonElement> { [DefaultCamera] = Json("\"Pier\""), [KioskTimeout] = Json("300") }
        }));
        blocked.Status.Should().Be(409);
        blocked.Code.Should().Be(nameof(RoofControllerErrorCode.SettingsHandEditPending));
        (await @operator.PostAsJsonAsync($"{Settings}/Reload", new RoofSettingsHandEditRequest { Token = edit.Token }, ApiJson.Options))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemAsync(await ReloadAsync(admin, "not-the-token"))).Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationVersionConflict));

        var reloaded = await ReloadAsync(admin, edit.Token);

        reloaded.StatusCode.Should().Be(HttpStatusCode.OK, await reloaded.Content.ReadAsStringAsync());
        var after = await ApiJson.ReadAsync<RoofSettingsResponse>(reloaded);
        after.Version.Should().Be(2);
        after.PendingHandEdit.Should().BeNull();
        State(after, DefaultCamera).Value!.Value.GetString().Should().Be("Yard");
        State(after, KioskTimeout).Value!.Value.GetDouble().Should().Be(120);
        host.Services.GetRequiredService<IOptionsMonitor<RoofControllerUiOptions>>().CurrentValue.DefaultCamera.Should().Be("Yard");
        var file = File.ReadAllText(SettingsPath);
        file.Should().NotContain("Edited by hand", "comments are lost when the API saves the file");
        var saved = ReadFile(SettingsPath);
        saved[RoofSettingsFile.MetadataProperty]!["Version"]!.GetValue<long>().Should().Be(2);
        logs.Entries.Should().Contain(entry => entry.Message.StartsWith("AUDIT settings hand edit reloaded by test-admin (version 1 -> 2", StringComparison.Ordinal)
            && entry.Message.Contains($"{DefaultCamera}: \"Roof\" -> \"Yard\"", StringComparison.Ordinal));

        var next = await ReadGroupAsync(@operator, RoofSettingsContract.UiGroup, values => values[DefaultCamera] = Json("\"Pier\""));
        (await PostGroupAsync(@operator, RoofSettingsContract.UiGroup, next)).StatusCode.Should().Be(HttpStatusCode.OK, "the edit no longer blocks changes");
    }

    [TestMethod]
    public async Task ASafetyCriticalHandEdit_NeedsConfirmation_ToBeReloadedOrLoadedByARestart()
    {
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using var host = StartHost(roof, logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        File.WriteAllText(SettingsPath, "{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }");

        var edit = (await GetAsync(admin)).PendingHandEdit!;
        edit.RequiresConfirmation.Should().BeTrue();
        var refused = await ProblemAsync(await ReloadAsync(admin, edit.Token));
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationRejected));
        refused.Detail.Should().Contain(OpenRelay).And.Contain(CloseRelay);
        var restart = await ProblemAsync(await admin.PostAsJsonAsync(Restart, new RoofRestartRequest(), ApiJson.Options));
        restart.Status.Should().Be(409);
        restart.Code.Should().Be(nameof(RoofControllerErrorCode.RestartRefused));
        roof.Mock.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
        roof.Applied.Should().BeEmpty();

        var confirmed = await ReloadAsync(admin, edit.Token, confirm: true);

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        roof.Current.OpenRelayId.Should().Be(2);
        roof.Current.CloseRelayId.Should().Be(1);
        roof.Applied.Should().ContainSingle().Which.IncludeLocalOnly.Should().BeTrue("the file's own values are the controller's configuration");
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("AUDIT settings hand edit reloaded by test-admin (version 1 -> 2, safety-critical: True)", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ALocalOnlyHandEdit_IsReloadedOnlyWithALocalCredential()
    {
        var roof = new RoofDouble();
        using var host = StartHost(roof, extraSettings: LocalKeys());
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        using var localAdmin = host.CreateApiClient(LocalAdminKey);
        File.WriteAllText(SettingsPath, "{ \"RoofControllerOptionsV4\": { \"DepartureReleaseTimeout\": \"00:00:09\" } }");

        var edit = (await GetAsync(admin)).PendingHandEdit!;
        edit.RequiresLocalCredential.Should().BeTrue();
        (await ProblemAsync(await ReloadAsync(admin, edit.Token))).Code.Should().Be(nameof(RoofControllerErrorCode.SettingNotPermitted));
        (await ProblemAsync(await admin.PostAsync(Restart, content: null))).Code.Should().Be(nameof(RoofControllerErrorCode.RestartRefused));
        roof.Applied.Should().BeEmpty();

        var reloaded = await ReloadAsync(localAdmin, edit.Token);

        reloaded.StatusCode.Should().Be(HttpStatusCode.OK, await reloaded.Content.ReadAsStringAsync());
        roof.Current.DepartureReleaseTimeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    [TestMethod]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"half-written", "is not valid JSON")]
    [DataRow("""{ "RoofControllerUi": { "DefaultCamera": "half-written" }, "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:80" } } } }""",
        "sets Kestrel:Endpoints:Http:Url, which is not in the settings catalogue")]
    public async Task AnUnreadableHandEdit_CannotBeReloadedOrRestartedInto_AndCanBeDiscarded(string content, string expected)
    {
        File.WriteAllText(SettingsPath, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Roof\" } }");
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using var host = StartHost(roof, logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        File.WriteAllText(SettingsPath, content);

        var edit = (await GetAsync(admin)).PendingHandEdit!;
        edit.FileProblem.Should().Contain(expected).And.NotContain("half-written").And.NotContain("0.0.0.0");
        edit.Changes.Should().BeEmpty("nothing in a file that cannot be used is shown as a change");
        (await ProblemAsync(await ReloadAsync(admin, edit.Token))).Code.Should().Be(nameof(RoofControllerErrorCode.ConfigurationRejected));
        var restart = await ProblemAsync(await admin.PostAsync(Restart, content: null));
        restart.Code.Should().Be(nameof(RoofControllerErrorCode.RestartRefused));
        restart.Detail.Should().StartWith("The controller would not start:");
        roof.Mock.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());

        var discarded = await admin.PostAsJsonAsync($"{Settings}/Discard", new RoofSettingsHandEditRequest { Token = edit.Token }, ApiJson.Options);

        discarded.StatusCode.Should().Be(HttpStatusCode.OK, await discarded.Content.ReadAsStringAsync());
        var after = await ApiJson.ReadAsync<RoofSettingsResponse>(discarded);
        after.Version.Should().Be(2);
        after.PendingHandEdit.Should().BeNull();
        State(after, DefaultCamera).CanWrite.Should().BeTrue();
        var saved = ReadFile(SettingsPath);
        saved[RoofSettingsFile.MetadataProperty]!["Version"]!.GetValue<long>().Should().Be(2);
        saved["RoofControllerUi"]!["DefaultCamera"]!.GetValue<string>().Should().Be("Roof", "the settings in effect are written back");
        logs.Entries.Should().Contain(entry => entry.Message.StartsWith("AUDIT settings hand edit discarded by test-admin (version 1 -> 2): a file that could not be read", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AHandEditWithAValueTheControllerCannotUse_IsNotReloaded()
    {
        var roof = new RoofDouble();
        using var host = StartHost(roof);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        File.WriteAllText(SettingsPath, "{ \"RoofControllerOptionsV4\": { \"SafetyWatchdogTimeout\": \"00:00:01\" } }");

        var edit = (await GetAsync(admin)).PendingHandEdit!;
        edit.Problems.Should().Contain(problem => problem.Message.Contains("must be between 5 and 600 seconds", StringComparison.Ordinal));
        (await ErrorsAsync(await ReloadAsync(admin, edit.Token, confirm: true)))
            .Should().Contain(error => error.Contains("must be between 5 and 600 seconds", StringComparison.Ordinal));
        (await ProblemAsync(await admin.PostAsync(Restart, content: null))).Code.Should().Be(nameof(RoofControllerErrorCode.RestartRefused));
        roof.Applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReloadAndDiscard_WithoutAPendingEdit_AreRefused()
    {
        using var host = StartHost(new RoofDouble());
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var reload = await ProblemAsync(await ReloadAsync(admin, "any"));
        reload.Code.Should().Be(nameof(RoofControllerErrorCode.InvalidRequest));
        reload.Detail.Should().Be("There is no pending hand edit.");
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Settings}/Discard", new RoofSettingsHandEditRequest { Token = "any" }, ApiJson.Options)))
            .Code.Should().Be(nameof(RoofControllerErrorCode.InvalidRequest));
    }

    // ---- Restart -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ARestart_StopsTheRoof_AnswersAccepted_AndAsksToExitWithCode75()
    {
        var logs = new RecordingLoggerProvider();
        var roof = new RoofDouble();
        using var host = StartHost(roof, logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var response = await admin.PostAsync(Restart, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofRestartResponse>(response)).ExitCode.Should().Be(RoofSettingsContract.RestartExitCode);
        roof.Mock.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
        var signal = host.Services.GetRequiredService<RoofRestartSignal>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await WaitUntilAsync(() => signal.Requested && lifetime.ApplicationStopping.IsCancellationRequested, "the restart to be requested");
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message == "AUDIT controller restart requested by test-admin: roof stopped and verified; exiting with code 75");
    }

    [TestMethod]
    public async Task ARestart_IsRefused_WhenTheRoofStopCannotBeVerified()
    {
        var roof = new RoofDouble();
        roof.Mock.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back.")));
        using var host = StartHost(roof);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var refused = await ProblemAsync(await admin.PostAsync(Restart, content: null));

        refused.Status.Should().Be(409);
        refused.Code.Should().Be(nameof(RoofControllerErrorCode.RestartRefused));
        refused.Detail.Should().StartWith("The roof stop could not be verified, so the controller was not restarted: The relays could not be read back.");
        host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeFalse();
    }

    [TestMethod]
    public async Task ARestart_NeedsAnAdmin()
    {
        var roof = new RoofDouble();
        using var host = StartHost(roof);
        using var anonymous = host.CreateApiClient();
        (await anonymous.PostAsync(Restart, content: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var key in new[] { TestApiKeys.Viewer, TestApiKeys.Operator })
        {
            using var client = host.CreateApiClient(key);
            (await client.PostAsync(Restart, content: null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        roof.Mock.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private RoofApiTestHost StartHost(
        RoofDouble roof,
        RecordingLoggerProvider? logs = null,
        bool files = true,
        IReadOnlyDictionary<string, string?>? hostSettings = null,
        IDictionary<string, string?>? extraSettings = null)
    {
        var host = new RoofApiTestHost(
            roof.Mock,
            settings: extraSettings,
            configureServices: services =>
            {
                // A cheap hash so the tests run quickly; production uses the ASP.NET Core default.
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            settingsFilePath: files ? SettingsPath : null,
            secretsFilePath: files ? SecretsPath : null,
            hostSettings: hostSettings);
        roof.StartFrom(host);
        return host;
    }

    private static Dictionary<string, string?> LocalKeys() => new()
    {
        ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-local-admin",
        ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.AdminRole,
        ["RoofControllerSecurity:ApiKeys:4:Key"] = LocalAdminKey,
        ["RoofControllerSecurity:ApiKeys:4:Local"] = "true",
        ["RoofControllerSecurity:ApiKeys:5:Name"] = "test-local-kiosk",
        ["RoofControllerSecurity:ApiKeys:5:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:5:Key"] = LocalKioskKey,
        ["RoofControllerSecurity:ApiKeys:5:Kiosk"] = "true",
        ["RoofControllerSecurity:ApiKeys:5:Local"] = "true",
        ["RoofControllerSecurity:ApiKeys:6:Name"] = "test-remote-kiosk",
        ["RoofControllerSecurity:ApiKeys:6:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:6:Key"] = RemoteKioskKey,
        ["RoofControllerSecurity:ApiKeys:6:Kiosk"] = "true"
    };

    private static async Task AddUserAsync(RoofApiTestHost host, string name, string role)
    {
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var response = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = name, Role = role, Pin = TestSecrets.Pin });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> PinSessionAsync(RoofApiTestHost host, string kioskKey, string name)
    {
        using var kiosk = host.CreateApiClient(kioskKey);
        var response = await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = name, Pin = TestSecrets.Pin });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(response);
        var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, session.Token);
        return client;
    }

    private static async Task<RoofSettingsResponse> GetAsync(HttpClient client)
    {
        var response = await client.GetAsync(Settings);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofSettingsResponse>(response);
    }

    /// <summary>A request for <paramref name="group"/> with every setting the caller may change, as it is now.</summary>
    private static async Task<RoofSettingsUpdateRequest> ReadGroupAsync(
        HttpClient client,
        string group,
        Action<Dictionary<string, JsonElement>>? change = null,
        bool confirm = false)
    {
        var settings = await GetAsync(client);
        var values = settings.Settings
            .Where(state => state.Group == group && state.CanWrite && !RoofSettingsCatalogue.Find(state.Key)!.Secret)
            .ToDictionary(state => state.Key, state => state.Value ?? Json("null"));
        change?.Invoke(values);
        return new RoofSettingsUpdateRequest { ExpectedVersion = settings.Version, ConfirmSafetyCriticalChange = confirm, Values = values };
    }

    private static Task<HttpResponseMessage> PostGroupAsync(HttpClient client, string group, RoofSettingsUpdateRequest request)
        => client.PostAsJsonAsync($"{Settings}/{group}", request, ApiJson.Options);

    private static Task<HttpResponseMessage> ReloadAsync(HttpClient client, string token, bool confirm = false)
        => client.PostAsJsonAsync($"{Settings}/Reload", new RoofSettingsHandEditRequest { Token = token, ConfirmSafetyCriticalChange = confirm }, ApiJson.Options);

    private static RoofSettingState State(RoofSettingsResponse settings, string key) => settings.Settings.Single(state => state.Key == key);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonObject ReadFile(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static async Task<(int Status, string? Code, string? Detail)> ProblemAsync(HttpResponseMessage response)
    {
        var body = await ApiJson.ReadElementAsync(response);
        body.GetProperty("type").GetString().Should().StartWith("urn:hvo:roof-controller:", body.ToString());
        return ((int)response.StatusCode, body.GetProperty("code").GetString(), body.GetProperty("detail").GetString());
    }

    private static async Task<IReadOnlyList<string>> ErrorsAsync(HttpResponseMessage response)
    {
        var body = await ApiJson.ReadElementAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        return body.GetProperty("errors").EnumerateObject()
            .SelectMany(property => property.Value.EnumerateArray().Select(error => error.GetString()!))
            .ToList();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The roof service's configuration: keeps what it is given, refuses while the roof moves, and keeps its local-only
    /// settings unless the caller may change them, as <see cref="IRoofControllerServiceV4.ApplyConfiguration"/> does.
    /// </summary>
    private sealed class RoofDouble
    {
        private readonly object _gate = new();
        private readonly List<(RoofControllerOptionsV4 Options, bool IncludeLocalOnly)> _applied = [];
        private RoofControllerOptionsV4 _current = new();

        public RoofDouble()
        {
            Mock = RoofServiceMock.Create();
            Mock.Setup(service => service.GetConfigurationSnapshot()).Returns(() => Current);
            Mock.Setup(service => service.ApplyConfiguration(It.IsAny<RoofControllerOptionsV4>(), It.IsAny<bool>()))
                .Returns<RoofControllerOptionsV4, bool>(Apply);
        }

        public Mock<IRoofControllerServiceV4> Mock { get; }

        public bool Moving { get; init; }

        public RoofControllerOptionsV4 Current
        {
            get
            {
                lock (_gate)
                {
                    return _current with { };
                }
            }
        }

        public IReadOnlyList<(RoofControllerOptionsV4 Options, bool IncludeLocalOnly)> Applied
        {
            get
            {
                lock (_gate)
                {
                    return _applied.ToList();
                }
            }
        }

        /// <summary>Starts from the options the host bound, as the service does.</summary>
        public void StartFrom(RoofApiTestHost host)
        {
            var options = host.Services.GetRequiredService<IOptions<RoofControllerOptionsV4>>().Value;
            lock (_gate)
            {
                _current = options with { };
            }
        }

        private Result<RoofControllerOptionsV4> Apply(RoofControllerOptionsV4 options, bool includeLocalOnly)
        {
            lock (_gate)
            {
                _applied.Add((options with { }, includeLocalOnly));
                if (Moving)
                {
                    return Result<RoofControllerOptionsV4>.Failure(new RoofControllerException(
                        RoofControllerErrorCode.OperationInProgress, "The roof is moving; its settings cannot change until it stops."));
                }

                var next = options with { };
                if (!includeLocalOnly)
                {
                    next.AllowIgnoringLimitSwitchesOnPhysicalHardware = _current.AllowIgnoringLimitSwitchesOnPhysicalHardware;
                    next.DriveStopConfirmationTimeout = _current.DriveStopConfirmationTimeout;
                    next.DepartureReleaseTimeout = _current.DepartureReleaseTimeout;
                }

                _current = next;
                return Result<RoofControllerOptionsV4>.Success(next with { });
            }
        }
    }
}
