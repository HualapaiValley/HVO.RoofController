using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof config</c> (#45): the settings each role may read (<c>show</c>, <c>get</c>), changing them one group at a
/// time (<c>set</c>) with safety-critical changes held back until confirmed, secrets read without echo
/// (<c>set-secret</c>), and a hand edit of the settings file reviewed, applied or discarded (<c>diff</c>, <c>apply</c>,
/// <c>discard</c>), each against the real settings API and real settings files in a temporary directory.
/// </summary>
[TestClass]
public sealed class RoofCliConfigCommandTests
{
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";
    private const string Watchdog = "RoofControllerOptionsV4:SafetyWatchdogTimeout";
    private const string AtSpeed = "RoofControllerOptionsV4:AtSpeedConfirmationTimeout";
    private const string OpenRelay = "RoofControllerOptionsV4:OpenRelayId";
    private const string ClearFaultRelay = "RoofControllerOptionsV4:ClearFaultRelayId";
    private const string Departure = "RoofControllerOptionsV4:DepartureReleaseTimeout";
    private const string RestartWait = "RoofControllerHostOptionsV4:RestartOnFailureWaitTime";
    private const string RequireHttps = "RoofControllerSecurity:RequireHttps";
    private const string CameraServer = "BlueIris:BaseUrl";
    private const string CameraPassword = "BlueIris:Password";

    private const string WrongKey = "test-wrong-key-not-a-real-secret-99";
    private const string CameraSecret = "test-camera-password-not-real-45";

    private const string UiHandEdit = "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\", \"KioskScreenTimeout\": \"00:02:00\" } }";
    private const string RelayHandEdit = "{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }";
    private const string UnreadableHandEdit = "{ \"RoofControllerUi\": { \"DefaultCamera\": \"half-written";

    private const string HandEditHeadline =
        "The settings file was edited by hand. Until it is applied or discarded, changes through the controller are refused.";

    private const string DiscardQuestion = "Discard the hand edit? The edited file is overwritten.";

    // ---- show ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Show_AsAdmin_ListsEveryGroupWithTheFileAndVersion()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "show");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().StartWith("Settings version 1.").And.Contain($"File: {controller.SettingsPath}");
        foreach (var group in new[] { "roof", "controller", "camera", "security", "identity", "logging", "ui" })
        {
            result.Out.Should().Contain($"[{group}] ", "an admin reads every group");
        }

        Line(result.Out, Watchdog).Should().Contain("120 s (00:02:00)");
        Line(result.Out, OpenRelay).Should().Contain("safety-critical");
        Line(result.Out, Departure).Should().Contain("read-only").And.Contain("safety-critical to turn off").And.Contain("local credential");
        Line(result.Out, RestartWait).Should().Contain("after restart");
        Line(result.Out, CameraPassword).Should().Contain("(not set)").And.Contain("secret");
        result.Out.Should().NotContain("in memory only").And.NotContain("hand edit");
        result.Error.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Show_AsOperatorOnAControllerWithoutAFile_ListsOnlyTheUiGroup_AndSaysChangesAreLostAtARestart()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "show");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain(" (in memory only: lost at a restart).")
            .And.Contain("Warning: Settings changes are held in memory only")
            .And.Contain("[ui] ")
            .And.Contain(DefaultCamera)
            .And.Contain(KioskTimeout);
        result.Out.Should().NotContain("[roof]").And.NotContain(Watchdog).And.NotContain("File:", "only an admin is told where the file is");
        Line(result.Out, DefaultCamera).Should().NotContain("read-only", "an operator may change the ui group");
    }

    [TestMethod]
    public async Task Show_AsViewer_ReadsTheUiGroup_MarkedReadOnly()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("config", "show");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("[ui] ").And.NotContain("[roof]");
        Line(result.Out, DefaultCamera).Should().Contain("read-only");
        Line(result.Out, KioskTimeout).Should().Contain("300 s (00:05:00)").And.Contain("read-only");
    }

    [TestMethod]
    public async Task Show_WithJson_GivesTheVersionTheFileAndEachGroupsSettings()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "show", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var json = result.Json;
        json.GetProperty("version").GetInt64().Should().Be(1);
        json.GetProperty("fileBacked").GetBoolean().Should().BeTrue();
        json.GetProperty("filePath").GetString().Should().Be(controller.SettingsPath);
        json.GetProperty("pendingHandEdit").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("warnings").GetArrayLength().Should().Be(0);
        json.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("name").GetString())
            .Should().Equal("roof", "controller", "camera", "security", "identity", "logging", "ui");
        var watchdog = json.GetProperty("groups")[0].GetProperty("settings").EnumerateArray()
            .Single(setting => setting.GetProperty("key").GetString() == Watchdog);
        watchdog.GetProperty("displayValue").GetString().Should().Be("120 s (00:02:00)");
        watchdog.GetProperty("canWrite").GetBoolean().Should().BeTrue();
        watchdog.GetProperty("group").GetString().Should().Be("roof");
    }

    [TestMethod]
    public async Task Show_WithAGroup_ShowsOnlyThatGroup_IgnoringCase()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "show", "ROOF");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("[roof] ").And.Contain(Watchdog);
        result.Out.Should().NotContain("[ui]").And.NotContain(DefaultCamera);
    }

    [TestMethod]
    public async Task Show_WithAGroupTheCallerMayNotRead_IsAUsageError_ListingTheGroupsItMay()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "show", "roof");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"There is no settings group 'roof' that you may read. Groups: ui.{Environment.NewLine}");
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("show")]
    [DataRow("get", "DefaultCamera")]
    [DataRow("set", "DefaultCamera=Pier")]
    [DataRow("set-secret", "Password")]
    [DataRow("diff")]
    [DataRow("apply")]
    [DataRow("discard", "--force")]
    public async Task EachConfigCommand_WithAWrongKey_IsSignedOut(params string[] command)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);

        var result = await rig.RunAsync(["config", .. command]);

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Contain("Not signed in, or the credential is no longer valid. Sign in again.");
        rig.Prompts.Should().BeEmpty("nothing is asked for before the controller has accepted the credential");
    }

    [TestMethod]
    [DataRow("get")]
    [DataRow("set")]
    [DataRow("show", "roof", "ui")]
    [DataRow("apply", "--force")]
    [DataRow("set-secret", "Password", "--dry-run")]
    [DataRow("set", "DefaultCamera=Pier", "--no-such-option")]
    public async Task EachConfigCommand_WithAMalformedCommandLine_IsAUsageError(params string[] command)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(["config", .. command]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain($"Run 'hvo-roof config {command[0]} --help' for usage.");
    }

    // ---- get -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Get_ByTheLastPartOfTheKey_ShowsTheValueTheDefaultAndWhetherItMayChange()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "get", "safetywatchdogtimeout");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        Row(result.Out, "Setting").Should().Be($"Safety watchdog timeout ({Watchdog})");
        Row(result.Out, "Group").Should().Be("roof");
        Row(result.Out, "Value").Should().Be("120 s (00:02:00)");
        Row(result.Out, "Default").Should().Be("120 s (00:02:00)");
        Row(result.Out, "Change").Should().Be("you may change it");
        Row(result.Out, "About").Should().NotBeEmpty();
        result.Out.Should().NotContain("Notes:", "the watchdog time is not safety-critical and applies at once");
    }

    [TestMethod]
    public async Task Get_WithJson_GivesTheSettingsDescription()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "get", KioskTimeout, "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var json = result.Json;
        json.GetProperty("key").GetString().Should().Be(KioskTimeout);
        json.GetProperty("group").GetString().Should().Be("ui");
        json.GetProperty("value").GetDouble().Should().Be(300);
        json.GetProperty("displayValue").GetString().Should().Be("300 s (00:05:00)");
        json.GetProperty("type").GetString().Should().Be("Duration");
        json.GetProperty("secret").GetBoolean().Should().BeFalse();
        json.GetProperty("canWrite").GetBoolean().Should().BeTrue();
        json.GetProperty("readOnlyReason").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("safety").GetString().Should().Be("None");
        json.GetProperty("appliesAfterRestart").GetBoolean().Should().BeFalse();
        json.GetProperty("localOnly").GetBoolean().Should().BeFalse();
        json.GetProperty("problem").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    [DataRow(TestApiKeys.Admin, "NoSuchSetting")]
    [DataRow(TestApiKeys.Operator, "SafetyWatchdogTimeout")]
    [DataRow(TestApiKeys.Operator, Watchdog)]
    public async Task Get_ASettingTheCallerCannotRead_IsAUsageError_PointingAtShow(string apiKey, string key)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(apiKey);

        var result = await rig.RunAsync("config", "get", key);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"There is no setting '{key}' that you may read. 'hvo-roof config show' lists them.{Environment.NewLine}");
    }

    [TestMethod]
    public async Task Get_ALocalOnlySafetyCriticalSetting_FromARemoteAdmin_SaysWhyItIsReadOnly()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "get", Departure);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        Row(result.Out, "Change").Should().Be(
            $"read-only: {Departure} is local-only: change it at the controller, from the kiosk with an admin PIN or with a local admin API key.");
        Row(result.Out, "Notes").Should().Be("read-only, safety-critical to turn off, local credential");
    }

    [TestMethod]
    public async Task Get_ASettingSetAboveTheSettingsFile_IsReadOnly_NamingWhereToChangeIt()
    {
        using var controller = new ControllerWithFiles(hostSettings: new Dictionary<string, string?> { [DefaultCamera] = "Dome" });
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "get", "DefaultCamera");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        Row(result.Out, "Value").Should().Be("Dome");
        Row(result.Out, "Change").Should().StartWith($"read-only: {DefaultCamera} is set by the ")
            .And.EndWith(", which takes precedence over the settings file; change it there.");
    }

    [TestMethod]
    public async Task Get_ASecret_ShowsOnlyWhetherItIsSet()
    {
        using var controller = new ControllerWithFiles(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("config", "get", "Password");
        var json = await rig.RunAsync("config", "get", CameraPassword, "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        Row(text.Out, "Value").Should().Be("(not set)");
        Row(text.Out, "Default").Should().Be("(none)");
        Row(text.Out, "Notes").Should().Contain("secret");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("secret").GetBoolean().Should().BeTrue();
        json.Json.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        json.Json.GetProperty("default").ValueKind.Should().Be(JsonValueKind.Null);
        json.Json.GetProperty("displayValue").GetString().Should().Be("(not set)");
    }

    // ---- set -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Set_AnOrdinaryChange_IsSaved_AndGetShowsTheNewValue()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier", "KioskScreenTimeout=10m");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("[ui] ")
            .And.Contain($"  Default camera ({DefaultCamera}): (none) -> Pier{Environment.NewLine}")
            .And.Contain($"({KioskTimeout}): 300 s (00:05:00) -> 600 s (00:10:00){Environment.NewLine}")
            .And.Contain("Saved (settings version 2).");
        result.Out.Should().NotContain("in memory only").And.NotContain("restart").And.NotContain("SAFETY-CRITICAL");
        result.Error.Should().BeEmpty();
        File.ReadAllText(controller.SettingsPath).Should().Contain("Pier");

        var after = await rig.RunAsync("config", "get", "DefaultCamera");
        Row(after.Out, "Value").Should().Be("Pier");
        Row(after.Out, "Source").Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task Set_OnAControllerWithoutASettingsFile_SaysTheChangeIsLostAtARestart()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("The controller keeps settings in memory only: this change is lost when it restarts.")
            .And.Contain("Warning: Settings changes are held in memory only");
    }

    [TestMethod]
    public async Task Set_WithJson_ReportsTheVersionAndEachChange()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var json = result.Json;
        json.GetProperty("sent").GetBoolean().Should().BeTrue();
        json.GetProperty("version").GetInt64().Should().Be(2);
        json.GetProperty("needsRestart").GetBoolean().Should().BeFalse();
        json.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var change = json.GetProperty("changes").EnumerateArray().Should().ContainSingle().Which;
        change.GetProperty("key").GetString().Should().Be(DefaultCamera);
        change.GetProperty("from").GetString().Should().Be("(none)");
        change.GetProperty("to").GetString().Should().Be("Pier");
        change.GetProperty("safetyCritical").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task Set_WithDryRun_ShowsTheChange_AndSendsNothing()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier", "--dry-run");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"({DefaultCamera}): (none) -> Pier").And.EndWith($"Not sent (--dry-run).{Environment.NewLine}");
        (await VersionAsync(rig)).Should().Be(1);
        File.Exists(controller.SettingsPath).Should().BeFalse("nothing was saved");
    }

    [TestMethod]
    public async Task Set_TheValueItAlreadyHas_HasNothingToChange()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);

        var text = await rig.RunAsync("config", "set", "KioskScreenTimeout=5m");
        var json = await rig.RunAsync("config", "set", "KioskScreenTimeout=300", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be($"Nothing to change: the settings already have these values.{Environment.NewLine}");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("sent").GetBoolean().Should().BeFalse();
        (await VersionAsync(rig)).Should().Be(1);
    }

    [TestMethod]
    [DataRow("DefaultCamera", "'DefaultCamera' is not key=value.")]
    [DataRow("=Pier", "'=Pier' is not key=value.")]
    [DataRow("NoSuchSetting=1", "There is no setting 'NoSuchSetting' that you may read. 'hvo-roof config show' lists them.")]
    [DataRow("Password=typed-on-the-command-line",
        "BlueIris:Password is a secret. Use 'hvo-roof config set-secret BlueIris:Password', which reads it from the terminal or standard input.")]
    [DataRow("KioskScreenTimeout=soon",
        "RoofControllerUi:KioskScreenTimeout: Enter a duration in seconds (for example 90 or 1.5), with a unit (500ms, 5m, 2h), or as hh:mm:ss.")]
    [DataRow("SafetyWatchdogTimeout=1h", "RoofControllerOptionsV4:SafetyWatchdogTimeout: Enter at most 600 s.")]
    [DataRow("OpenRelayId=two", "RoofControllerOptionsV4:OpenRelayId: Enter a whole number.")]
    [DataRow("AllowAnonymousStop=maybe", "RoofControllerSecurity:AllowAnonymousStop: Enter true or false.")]
    public async Task Set_AValueThatIsNotValid_IsAUsageError_SayingWhatItNeeds(string assignment, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", assignment, "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be(expected + Environment.NewLine);
        result.Out.Should().BeEmpty();
        result.ToString().Should().NotContain("typed-on-the-command-line", "a secret on the command line is not echoed back");
    }

    [TestMethod]
    public async Task Set_SettingsOfTwoGroups_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier", "SafetyWatchdogTimeout=90");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"Change one group at a time. These settings are in: ui, roof.{Environment.NewLine}");
    }

    [TestMethod]
    public async Task Set_AValueTheControllerRejects_IsRefused_WithTheControllersReason()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "RequireHttps=true", "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Out.Should().Contain($"({RequireHttps}): ").And.Contain("-> true  [SAFETY-CRITICAL]");
        result.Error.Should().StartWith("The configuration was rejected as unsafe. [ConfigurationRejected]")
            .And.Contain($"{RequireHttps} cannot be turned on: the controller has no HTTPS listener");
        (await VersionAsync(rig)).Should().Be(1);
    }

    [TestMethod]
    public async Task Set_RelaysTheControllerFindsInvalid_IsRefused_ListingEachReason()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        // The close relay is 2 already: the controller, not the CLI, knows that relays must differ.
        var result = await rig.RunAsync("config", "set", "OpenRelayId=2", "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("Relay identifiers (Open, Close, ClearFault, Stop) must be unique.");
        result.Error.Should().NotContain("  : ", "a problem that belongs to no field is printed without an empty field name");
        controller.Roof.Applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Set_ASafetyCriticalChange_WithoutConfirming_IsShownAndNotSent()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "OpenRelayId=3", "ClearFaultRelayId=1");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Out.Should().StartWith("[roof] ")
            .And.Contain($"({OpenRelay}): 1 -> 3  [SAFETY-CRITICAL]")
            .And.Contain($"({ClearFaultRelay}): 3 -> 1  [SAFETY-CRITICAL]")
            .And.NotContain("Saved");
        result.Error.Should().Be(
            $"Not sent: the change is safety-critical. Review it, then run the command again with --confirm-safety-critical.{Environment.NewLine}");
        controller.Roof.Applied.Should().BeEmpty();
        (await VersionAsync(rig)).Should().Be(1);
    }

    [TestMethod]
    public async Task Set_ASafetyCriticalChange_WithConfirmation_IsSaved()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "OpenRelayId=3", "ClearFaultRelayId=1", "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"({OpenRelay}): 1 -> 3  [SAFETY-CRITICAL]").And.Contain("Saved (settings version 2).");
        controller.Roof.Current.OpenRelayId.Should().Be(3);
        controller.Roof.Current.ClearFaultRelayId.Should().Be(1);
    }

    [TestMethod]
    public async Task Set_ASafetyCriticalChange_WithJson_SaysConfirmationIsRequired()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "OpenRelayId=3", "ClearFaultRelayId=1", "--json");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        var json = result.Json;
        json.GetProperty("sent").GetBoolean().Should().BeFalse();
        json.GetProperty("confirmationRequired").GetBoolean().Should().BeTrue();
        json.GetProperty("changes").EnumerateArray().Should().HaveCount(2)
            .And.OnlyContain(change => change.GetProperty("safetyCritical").GetBoolean());
        result.Error.Should().BeEmpty("with --json the answer is the JSON");
    }

    [TestMethod]
    public async Task Set_TurningAProtectionOff_IsSafetyCritical()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var blocked = await rig.RunAsync("config", "set", "AtSpeedConfirmationTimeout=");
        var sent = await rig.RunAsync("config", "set", "AtSpeedConfirmationTimeout=", "--confirm-safety-critical");

        blocked.Code.Should().Be(RoofExitCode.ConfirmationRequired, blocked.ToString());
        blocked.Out.Should().Contain($"({AtSpeed}): ").And.Contain("-> (none)  [SAFETY-CRITICAL]");
        sent.Code.Should().Be(RoofExitCode.Success, sent.ToString());
        controller.Roof.Current.AtSpeedConfirmationTimeout.Should().BeNull();
    }

    [TestMethod]
    public async Task Set_ASettingThatAppliesAfterARestart_SaysARestartIsNeeded()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "RestartOnFailureWaitTime=20");
        var after = await rig.RunAsync("config", "get", RestartWait);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"({RestartWait}): ").And.Contain("-> 20 s  [applies after a restart]")
            .And.Contain("Part of the change applies after a restart: 'hvo-roof restart'.");
        Row(after.Out, "Notes").Should().Contain("after restart").And.Contain("RESTART PENDING");
        after.Out.Should().Contain("RESTART PENDING");
    }

    [TestMethod]
    public async Task Set_ASettingThatAppliesAfterARestart_WithJson_SaysARestartIsNeeded()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "RestartOnFailureWaitTime=20", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("needsRestart").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("changes")[0].GetProperty("appliesAfterRestart").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task Set_AsViewer_IsRefusedByRole()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Contain($"{DefaultCamera} needs the RoofOperator role.");
    }

    [TestMethod]
    public async Task Set_ALocalOnlySettingFromARemoteAdmin_IsRefused_SayingWhereToChangeIt()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set", "DepartureReleaseTimeout=9");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Contain($"{Departure} is local-only: change it at the controller");
        controller.Roof.Applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Set_WhenAnotherClientChangedTheSettingsSinceTheyWereRead_IsRefusedAsAConflict()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        using var other = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);
        other.UseApiKey(TestApiKeys.Operator);
        CliResult? racer = null;

        var result = await RunWithHookAsync(
            rig,
            controller.Host,
            beforeSave: async () => racer = await other.RunAsync("config", "set", "KioskScreenTimeout=10m"),
            "config", "set", "DefaultCamera=Pier");

        racer.Should().NotBeNull("the other client saved between this one's read and its write");
        racer!.Code.Should().Be(RoofExitCode.Success, racer.ToString());
        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Be(
            $"The configuration changed since it was read. [ConfigurationVersionConflict]{Environment.NewLine}" +
            $"The settings changed since they were read (expected version 1, current version 2). Read them again and retry.{Environment.NewLine}");
        var after = await rig.RunAsync("config", "get", "DefaultCamera");
        Row(after.Out, "Value").Should().Be("(none)", "the refused change was not saved");
    }

    [TestMethod]
    public async Task Set_WithJson_WhenRefused_GivesTheControllersCodeAndDetail()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        using var other = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);
        other.UseApiKey(TestApiKeys.Operator);

        var result = await RunWithHookAsync(
            rig,
            controller.Host,
            beforeSave: () => other.RunAsync("config", "set", "KioskScreenTimeout=10m"),
            "config", "set", "DefaultCamera=Pier", "--json");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be(7);
        error.GetProperty("kind").GetString().Should().Be("Refused");
        error.GetProperty("status").GetInt32().Should().Be(409);
        error.GetProperty("code").GetString().Should().Be("ConfigurationVersionConflict");
        error.GetProperty("detail").GetString().Should().Contain("expected version 1, current version 2");
        result.Error.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Set_WhileAHandEditIsPending_IsRefused_UntilAnAdminAppliesOrDiscardsIt()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "set", "DefaultCamera=Pier");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("edited outside the API; an admin must reload or discard that edit first.");
    }

    [TestMethod]
    public async Task Set_ALocalOnlySettingFromARemoteAdmin_WhileAHandEditIsPending_IsRefusedAsTheControllerRefusesIt()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "set", "DepartureReleaseTimeout=9");

        // The controller refuses the same change for the hand edit first (409), not for where it may be changed (403).
        using var client = rig.CreateContext().Connect();
        var version = (await client.Settings.GetAsync()).Version;
        var sent = async () => await client.Settings.UpdateAsync(
            RoofSettingsContract.RoofGroup,
            new RoofSettingsUpdateRequest
            {
                ExpectedVersion = version,
                Values = new Dictionary<string, JsonElement> { [Departure] = JsonSerializer.SerializeToElement("00:00:09") }
            });
        var refusal = (await sent.Should().ThrowAsync<RoofApiException>()).Which;
        refusal.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        refusal.Code.Should().Be(RoofControllerErrorCode.SettingsHandEditPending);

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("edited outside the API; an admin must reload or discard that edit first.");
        controller.Roof.Applied.Should().BeEmpty();
    }

    // ---- set-secret ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SetSecret_FromStandardInput_IsSentAsSet_AndNeverEchoed()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);

        var result = await rig.RunAsync("config", "set-secret", "Password");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal([("Password: ", true)]);
        result.Out.Should().Contain($"({CameraPassword}): (not set) -> (new value)").And.Contain("Saved (settings version 2).");
        result.ToString().Should().NotContain(CameraSecret);
        var after = await rig.RunAsync("config", "get", CameraPassword);
        Row(after.Out, "Value").Should().Be("(set)");
        after.Out.Should().NotContain(CameraSecret);
        (File.Exists(controller.SettingsPath) ? File.ReadAllText(controller.SettingsPath) : string.Empty)
            .Should().NotContain(CameraSecret, "a secret is kept in the secrets file, not the settings file");
    }

    [TestMethod]
    public async Task SetSecret_AtATerminal_AsksTwice()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);
        rig.Input.Enqueue(CameraSecret);

        var result = await rig.RunAsync("config", "set-secret", CameraPassword);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal([("Password: ", true), ("Again: ", true)]);
        result.ToString().Should().NotContain(CameraSecret);
    }

    [TestMethod]
    public async Task SetSecret_AtATerminal_WithEntriesThatDoNotMatch_IsAUsageError_AndSendsNothing()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);
        rig.Input.Enqueue(CameraSecret + "-typo");

        var result = await rig.RunAsync("config", "set-secret", CameraPassword);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"The two entries do not match.{Environment.NewLine}");
        result.ToString().Should().NotContain(CameraSecret);
        (await VersionAsync(rig)).Should().Be(1);
    }

    [TestMethod]
    public async Task SetSecret_WithNoInput_IsAUsageError_NamingTheSetting()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "set-secret", CameraPassword);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"No value was given for: Password.{Environment.NewLine}");
        (await VersionAsync(rig)).Should().Be(1);
    }

    [TestMethod]
    public async Task SetSecret_WithClear_RemovesTheSecret_WithoutAskingForIt()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);
        var set = await rig.RunAsync("config", "set-secret", CameraPassword);
        set.Code.Should().Be(RoofExitCode.Success, set.ToString());
        rig.Prompts.Clear();

        var result = await rig.RunAsync("config", "set-secret", CameraPassword, "--clear");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().BeEmpty();
        result.Out.Should().Contain($"({CameraPassword}): (set) -> (not set)").And.Contain("Saved (settings version 3).");
        Row((await rig.RunAsync("config", "get", CameraPassword)).Out, "Value").Should().Be("(not set)");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "text")]
    [DataRow(true, DisplayName = "--json")]
    public async Task SetSecret_OverASecretThatIsSet_IsSentAndReplacesIt(bool json)
    {
        const string rotated = "test-camera-password-rotated-45";
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);
        (await rig.RunAsync("config", "set-secret", CameraPassword)).Code.Should().Be(RoofExitCode.Success);
        rig.Input.Enqueue(rotated);

        var result = json
            ? await rig.RunAsync("config", "set-secret", CameraPassword, "--json")
            : await rig.RunAsync("config", "set-secret", CameraPassword);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        if (json)
        {
            result.Json.GetProperty("sent").GetBoolean().Should().BeTrue();
            result.Json.GetProperty("version").GetInt64().Should().Be(3);
            var change = result.Json.GetProperty("changes").EnumerateArray().Should().ContainSingle().Which;
            change.GetProperty("from").GetString().Should().Be("(set)");
            change.GetProperty("to").GetString().Should().Be("(new value)");
        }
        else
        {
            result.Out.Should().Contain($"({CameraPassword}): (set) -> (new value)").And.Contain("Saved (settings version 3).");
        }

        result.ToString().Should().NotContain(rotated).And.NotContain(CameraSecret);
        File.ReadAllText(controller.SecretsPath).Should().Contain(rotated).And.NotContain(CameraSecret, "the new secret replaced the old one");
    }

    [TestMethod]
    public async Task SetSecret_WithJson_ShowsOnlyThatItIsSet()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);

        var result = await rig.RunAsync("config", "set-secret", CameraPassword, "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var change = result.Json.GetProperty("changes").EnumerateArray().Should().ContainSingle().Which;
        change.GetProperty("from").GetString().Should().Be("(not set)");
        change.GetProperty("to").GetString().Should().Be("(new value)");
        result.ToString().Should().NotContain(CameraSecret);
    }

    [TestMethod]
    public async Task SetSecret_OneCameraCredentialAlone_IsRefusedWithTheControllersReason_WithoutEchoingIt()
    {
        using var controller = new ControllerWithFiles(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(CameraSecret);

        var result = await rig.RunAsync("config", "set-secret", CameraPassword);

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().StartWith("The request was invalid.")
            .And.Contain("The camera proxy is misconfigured (set both BlueIris:UserName and BlueIris:Password, or neither).");
        result.ToString().Should().NotContain(CameraSecret);
        Row((await rig.RunAsync("config", "get", CameraPassword)).Out, "Value").Should().Be("(not set)");
    }

    [TestMethod]
    public async Task SetSecret_TheCameraUserAndPasswordTogether_AreSetInOneChange()
    {
        using var controller = new ControllerWithFiles(new Dictionary<string, string?> { [CameraServer] = "http://192.168.0.4:81" });
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("test-camera-user");
        rig.Input.Enqueue(CameraSecret);

        var result = await rig.RunAsync("config", "set-secret", "UserName", "Password");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("(BlueIris:UserName): (not set) -> (new value)").And.Contain($"({CameraPassword}): (not set) -> (new value)");
        result.ToString().Should().NotContain(CameraSecret).And.NotContain("test-camera-user");
    }

    [TestMethod]
    public async Task SetSecret_ForASettingThatIsNotSecret_PointsAtSet_WithoutAskingForAValue()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("config", "set-secret", "DefaultCamera");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be($"{DefaultCamera} is not a secret. Use 'hvo-roof config set {DefaultCamera}=VALUE'.{Environment.NewLine}");
        rig.Prompts.Should().BeEmpty();
    }

    // ---- diff ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Diff_WithNothingPending_SaysTheFileMatches()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("config", "diff");
        var json = await rig.RunAsync("config", "diff", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be($"No hand edit is pending: the settings file matches what the controller uses.{Environment.NewLine}");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("pending").GetBoolean().Should().BeFalse();
        json.Json.GetProperty("handEdit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task Diff_ShowsEachChangeOfAHandEdit_AndShowPointsAtIt()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var diff = await rig.RunAsync("config", "diff");
        var show = await rig.RunAsync("config", "show", "ui");

        diff.Code.Should().Be(RoofExitCode.Success, diff.ToString());
        diff.Out.Should().StartWith(HandEditHeadline)
            .And.Contain($"  {DefaultCamera}: (none) -> Yard{Environment.NewLine}")
            .And.Contain($"  {KioskTimeout}: 300 s (00:05:00) -> 120 s (00:02:00){Environment.NewLine}")
            .And.NotContain("SAFETY-CRITICAL").And.NotContain("To confirm it");
        show.Out.Should().Contain("A hand edit of the settings file is pending: 'hvo-roof config diff'.")
            .And.Contain("Warning: The settings file was edited outside the API. Review the pending edit, then reload or discard it.");
        Line(show.Out, DefaultCamera).Should().Contain("read-only", "changes wait until the edit is applied or discarded");
    }

    [TestMethod]
    public async Task Diff_WithJson_GivesTheEditsChanges()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "diff", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("pending").GetBoolean().Should().BeTrue();
        var edit = result.Json.GetProperty("handEdit");
        edit.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("key").GetString())
            .Should().BeEquivalentTo(DefaultCamera, KioskTimeout);
        edit.GetProperty("requiresConfirmation").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task Diff_ASafetyCriticalHandEdit_SaysApplyingItNeedsConfirming()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, RelayHandEdit);

        var result = await rig.RunAsync("config", "diff");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"  {OpenRelay}: 1 -> 2  [SAFETY-CRITICAL]")
            .And.Contain("Applying it is safety-critical and needs confirming.")
            .And.EndWith($"To confirm it: 'hvo-roof config apply --confirm-safety-critical'.{Environment.NewLine}");
    }

    [TestMethod]
    public async Task Diff_AFileTheControllerCannotUse_SaysWhy()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UnreadableHandEdit);

        var result = await rig.RunAsync("config", "diff");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().StartWith(HandEditHeadline).And.Contain("The file cannot be used: ").And.Contain("is not valid JSON");
        result.Out.Should().NotContain("half-written", "no value from a file that cannot be used is shown");
    }

    [TestMethod]
    public async Task Diff_AsOperator_WhileAHandEditIsPending_IsRefusedByRole_WithoutSayingTheFileMatches()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Operator);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "diff");

        // The controller shows a hand edit only to admins: an operator is told so, and that one is pending.
        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Be(
            "Reviewing, applying or discarding a hand edit of the settings file needs the admin role. A hand edit is pending: ask an admin to review it."
            + Environment.NewLine);
        result.Out.Should().NotContain("the settings file matches what the controller uses");
    }

    [TestMethod]
    public async Task Diff_AsViewer_WithNothingPending_IsRefusedByRole()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("config", "diff", "--json");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Json.GetProperty("error").GetProperty("message").GetString()
            .Should().Be("Reviewing, applying or discarding a hand edit of the settings file needs the admin role.");
    }

    // ---- apply -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Apply_WithNothingPending_SaysSo()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("config", "apply");
        var json = await rig.RunAsync("config", "apply", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be($"No hand edit is pending.{Environment.NewLine}");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("sent").GetBoolean().Should().BeFalse();
        json.Json.GetProperty("pending").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task Apply_AnOrdinaryHandEdit_TakesEffect()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "apply");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"Applied the hand edit (settings version 2).{Environment.NewLine}");
        Row((await rig.RunAsync("config", "get", DefaultCamera)).Out, "Value").Should().Be("Yard");
        (await rig.RunAsync("config", "diff")).Out.Should().StartWith("No hand edit is pending");
    }

    [TestMethod]
    public async Task Apply_ASafetyCriticalHandEdit_WithoutConfirming_IsShownAndNotApplied()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, RelayHandEdit);

        var result = await rig.RunAsync("config", "apply");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Out.Should().StartWith(HandEditHeadline).And.Contain($"  {OpenRelay}: 1 -> 2  [SAFETY-CRITICAL]");
        result.Error.Should().Be(
            $"Not applied: the edit is safety-critical. Review it, then run the command again with --confirm-safety-critical.{Environment.NewLine}");
        controller.Roof.Applied.Should().BeEmpty();
        (await rig.RunAsync("config", "diff", "--json")).Json.GetProperty("pending").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task Apply_ASafetyCriticalHandEdit_WithJson_SaysConfirmationIsRequired()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, RelayHandEdit);

        var result = await rig.RunAsync("config", "apply", "--json");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Json.GetProperty("sent").GetBoolean().Should().BeFalse();
        result.Json.GetProperty("confirmationRequired").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("handEdit").GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        controller.Roof.Applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Apply_ASafetyCriticalHandEdit_WithConfirmation_IsApplied()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, RelayHandEdit);

        var result = await rig.RunAsync("config", "apply", "--confirm-safety-critical", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("sent").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("version").GetInt64().Should().Be(2);
        controller.Roof.Current.OpenRelayId.Should().Be(2);
        controller.Roof.Current.CloseRelayId.Should().Be(1);
    }

    [TestMethod]
    public async Task Apply_AHandEditTheControllerCannotUse_IsRefused_WithItsReason()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UnreadableHandEdit);

        var result = await rig.RunAsync("config", "apply", "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().StartWith("The configuration was rejected as unsafe. [ConfigurationRejected]").And.Contain("is not valid JSON");
        (await rig.RunAsync("config", "diff", "--json")).Json.GetProperty("pending").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task Apply_AsViewer_WhileAHandEditIsPending_IsRefusedByRole()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Viewer);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "apply");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        (await AdminDiffPendingAsync(controller)).Should().BeTrue("the edit is still pending");
    }

    // ---- discard ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Discard_WithoutForce_WithoutATerminal_IsNotConfirmed()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);

        var result = await rig.RunAsync("config", "discard");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Error.Should().Be($"{DiscardQuestion} Not confirmed: run the command again with --force.{Environment.NewLine}");
        rig.Prompts.Should().BeEmpty();
        (await AdminDiffPendingAsync(controller)).Should().BeTrue();
        File.ReadAllText(controller.SettingsPath).Should().Be(UiHandEdit, "the edited file is left alone");
    }

    [TestMethod]
    public async Task Discard_WithJson_WithoutForce_DoesNotAsk_EvenAtATerminal()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("config", "discard", "--json");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Error.Should().Contain("Not confirmed: run the command again with --force.");
        rig.Prompts.Should().BeEmpty();
        (await AdminDiffPendingAsync(controller)).Should().BeTrue();
    }

    [TestMethod]
    public async Task Discard_AtATerminal_AnsweredYes_DiscardsTheEdit()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("config", "discard");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal([($"{DiscardQuestion} [y/N] ", false)]);
        result.Out.Should().Be($"Discarded the hand edit (settings version 2).{Environment.NewLine}");
        File.ReadAllText(controller.SettingsPath).Should().NotContain("Yard", "the file is written back as the controller has it");
        Row((await rig.RunAsync("config", "get", DefaultCamera)).Out, "Value").Should().Be("(none)");
    }

    [TestMethod]
    public async Task Discard_AtATerminal_AnsweredNo_SendsNothing()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UiHandEdit);
        rig.Input.Enqueue("n");

        var result = await rig.RunAsync("config", "discard");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Error.Should().Be($"Not confirmed; nothing was sent.{Environment.NewLine}");
        (await AdminDiffPendingAsync(controller)).Should().BeTrue();
    }

    [TestMethod]
    public async Task Discard_WithForce_DiscardsAFileTheControllerCannotUse()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.WriteAllText(controller.SettingsPath, UnreadableHandEdit);

        var result = await rig.RunAsync("config", "discard", "--force", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("sent").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("version").GetInt64().Should().Be(2);
        rig.Prompts.Should().BeEmpty();
        (await AdminDiffPendingAsync(controller)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Discard_WithNothingPending_SaysSo_WithoutAsking()
    {
        using var controller = new ControllerWithFiles();
        using var rig = new CliRig(controller.Host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("config", "discard");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"No hand edit is pending.{Environment.NewLine}");
        result.Error.Should().BeEmpty();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>The value of a <c>Label: value</c> row, however the rows are padded.</summary>
    private static string Row(string output, string label)
    {
        var line = Lines(output).FirstOrDefault(candidate => candidate.StartsWith(label + ":", StringComparison.Ordinal));
        line.Should().NotBeNull("the output has a {0} row:\n{1}", label, output);
        return line![(label.Length + 1)..].Trim();
    }

    /// <summary>The table row of the setting <paramref name="key"/>.</summary>
    private static string Line(string output, string key)
    {
        var line = Lines(output).FirstOrDefault(candidate => candidate.StartsWith(key + " ", StringComparison.Ordinal));
        line.Should().NotBeNull("the output has a row for {0}:\n{1}", key, output);
        return line!;
    }

    private static IEnumerable<string> Lines(string output) => output.Split('\n').Select(line => line.TrimEnd('\r'));

    private static async Task<long> VersionAsync(CliRig rig)
    {
        var result = await rig.RunAsync("config", "show", "--json");
        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        return result.Json.GetProperty("version").GetInt64();
    }

    /// <summary>Whether an admin is shown a pending hand edit.</summary>
    private static async Task<bool> AdminDiffPendingAsync(ControllerWithFiles controller)
    {
        using var admin = new CliRig(controller.Host);
        admin.UseApiKey(TestApiKeys.Admin);
        var result = await admin.RunAsync("config", "diff", "--json");
        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        return result.Json.GetProperty("pending").GetBoolean();
    }

    /// <summary>
    /// Runs one command line as <see cref="CliRig.RunAsync"/> does, but runs <paramref name="beforeSave"/> just before the
    /// command posts a settings change (the harness has no hook between a command's read and its write).
    /// </summary>
    private static async Task<CliResult> RunWithHookAsync(CliRig rig, RoofApiTestHost host, Func<Task> beforeSave, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var standard = rig.CreateHost(output, error);
        var hooked = new RoofCliHost
        {
            Out = standard.Out,
            Error = standard.Error,
            GetEnvironmentVariable = standard.GetEnvironmentVariable,
            ReadLine = standard.ReadLine,
            IsInteractive = standard.IsInteractive,
            Time = standard.Time,
            CreateHandler = () => new BeforeSaveHandler(ClientTestSupport.CreateHandler(() => host.Server), beforeSave),
            WebSocketFactory = standard.WebSocketFactory,
            StatusFeed = standard.StatusFeed
        };
        var code = await RoofCli.RunAsync([.. args, "--credentials-file", rig.CredentialsPath], hooked);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    /// <summary>Runs a callback once, before the first <c>POST Settings/{group}</c> goes to the controller.</summary>
    private sealed class BeforeSaveHandler(HttpMessageHandler inner, Func<Task> beforeSave) : DelegatingHandler(inner)
    {
        private int _fired;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.Contains("/Settings/", StringComparison.OrdinalIgnoreCase) == true
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await beforeSave().ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A controller with real settings and secrets files in its own temporary directory (deleted afterwards) and a roof
    /// double that keeps the options it is given.
    /// </summary>
    private sealed class ControllerWithFiles : IDisposable
    {
        public ControllerWithFiles(
            IDictionary<string, string?>? settings = null,
            IReadOnlyDictionary<string, string?>? hostSettings = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "hvo-roof-cli-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Roof = new SettingsApiTests.RoofDouble();
            Host = new RoofApiTestHost(
                Roof.Mock,
                settings: settings,
                configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: SettingsPath,
                secretsFilePath: SecretsPath,
                hostSettings: hostSettings);
            Roof.StartFrom(Host);
        }

        public string Root { get; }

        public string SettingsPath => Path.Combine(Root, "config", "appsettings.Local.json");

        public string SecretsPath => Path.Combine(Root, "secrets", "managed-secrets.json");

        public SettingsApiTests.RoofDouble Roof { get; }

        public RoofApiTestHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
