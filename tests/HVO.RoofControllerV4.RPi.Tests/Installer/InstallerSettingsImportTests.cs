using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The controller's settings from a backup (#69): copied into the settings folder as they are, only while the controller
/// has none, and the controller redeployed to read them. Never over the settings a controller has, and never recorded.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerSettingsImportTests
{
    private const string Backup = "/root/backup/appsettings.Local.json";
    private const string SettingsFile = "/etc/hvo-roof/config/appsettings.Local.json";

    // As the controller saves it: comments are allowed, and the metadata is kept as it is.
    private const string Saved = """
        {
          // Saved by the controller.
          "HvoRoofSettings": { "Version": 7, "SavedAtUtc": "2026-08-01T12:00:00Z", "SavedBy": "roy" },
          "RoofControllerSettings": { "OpenRelayId": 2 },
        }

        """;

    private static InstallAnswers Answers(string? backup = Backup)
        => new() { Roles = [InstallRole.Controller], Controller = new ControllerSettings { ImportSettingsFrom = backup } };

    [TestMethod]
    public async Task ABackup_StartsANewController_AsItIs_AndIsNotRecorded()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(Backup, Saved);
        var answers = pi.WriteAnswers(Answers());

        var plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain(SettingsFile).And.Contain("the controller's settings, from /root/backup/appsettings.Local.json").And.Contain("copied from");

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Read(SettingsFile).Should().Be(Saved);
        pi.Mode(SettingsFile).Should().Be(Modes.File, "the controller's settings file holds no secret");
        pi.Read(InstallPaths.SystemLog).Should().Contain($"Imported the controller's settings from {Backup} to {SettingsFile}.");
        pi.Deploys.Should().ContainSingle();
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Controller!.ImportSettingsFrom.Should().BeNull("an import is done once");

        var second = await pi.RunAsync("--answers", answers);
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change").And.Contain($"imported from {Backup}");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ABackup_ImportedIntoARunningControllerWithNoSettings_RedeploysIt()
    {
        using var pi = new FakeMachine().WithPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(Answers(backup: null)))).ExitCode.Should().Be(0);
        pi.Write(Backup, Saved);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers(), "import.json"));

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("redeployed to read the imported settings");
        pi.Read(SettingsFile).Should().Be(Saved);
        pi.Deploys.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task SettingsImportedByARunThatStoppedBeforeTheRedeploy_AreReadOnTheNextRun()
    {
        using var pi = new FakeMachine().WithPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(Answers(backup: null)))).ExitCode.Should().Be(0);
        pi.Write(Backup, Saved);
        pi.Write(SettingsFile, Saved);
        File.SetLastWriteTimeUtc(pi.OnDisk(SettingsFile), DateTime.UtcNow.AddMinutes(1));

        var plan = await pi.RunAsync("--plan", "--answers", pi.WriteAnswers(Answers(), "import.json"));

        plan.Output.Should().Contain($"imported from {Backup}").And.Contain("redeployed to read the imported settings");
    }

    [TestMethod]
    public async Task AControllerWithSettings_KeepsThem()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(Backup, Saved);
        pi.Write(SettingsFile, "{ \"RoofControllerSettings\": { \"OpenRelayId\": 3 } }\n");

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers()));

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("not imported: the controller has settings already");
        pi.Read(SettingsFile).Should().Contain("\"OpenRelayId\": 3");
    }

    [TestMethod]
    [DataRow(null, "there is no file /root/backup/appsettings.Local.json to import the controller's settings from")]
    [DataRow("[ 1, 2 ]", "/root/backup/appsettings.Local.json is not the controller's settings: it must hold a JSON object, like appsettings.json")]
    [DataRow("{\n  \"RoofControllerSettings\": { \"OpenRelayId\": 2 \n", "/root/backup/appsettings.Local.json is not the controller's settings: it is not valid JSON (line 3)")]
    public async Task ABackupThatIsNotTheControllersSettings_StopsTheInstall_BeforeAnythingChanges(string? content, string problem)
    {
        using var pi = new FakeMachine().WithPi();
        if (content is not null)
        {
            pi.Write(Backup, content);
        }

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers()));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.ToString().Should().Contain(problem);
        pi.Exists("/etc/hvo-roof").Should().BeFalse("nothing was changed");
    }

    [TestMethod]
    [DataRow("{ \"RoofControllerSecurity\": { \"ApiKeys\": [ { \"Name\": \"old\", \"Role\": \"RoofAdmin\", \"Key\": \"VALUE-FROM-THE-BACKUP\" } ] } }", "RoofControllerSecurity:ApiKeys:0:Key")]
    [DataRow("{ \"BlueIris\": { \"BaseUrl\": \"http://camera\", \"PASSWORD\": \"VALUE-FROM-THE-BACKUP\" } }", "BlueIris:PASSWORD")]
    [DataRow("{ \"BlueIris\": { \"UserName\": \"VALUE-FROM-THE-BACKUP\" } }", "BlueIris:UserName")]
    [DataRow("{ \"Mqtt:Password\": \"VALUE-FROM-THE-BACKUP\" }", "Mqtt:Password")]
    public async Task ABackupThatSetsASecret_IsRefused_NamingTheSettingButNotItsValue(string content, string setting)
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(Backup, content);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers()));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.ToString().Should().Contain($"{Backup} sets {setting}, which is a secret: the controller refuses a settings file with one")
            .And.Contain("put it in a file in /etc/hvo-roof/secrets instead")
            .And.NotContain("VALUE-FROM-THE-BACKUP");
        pi.Exists("/etc/hvo-roof").Should().BeFalse("nothing was changed");
    }

    [TestMethod]
    public async Task ABackupWhoseSettingsOnlyNameKeys_IsImported()
    {
        using var pi = new FakeMachine().WithPi();
        const string content = "{ \"RoofControllerSecurity\": { \"ApiKeys\": [ { \"Name\": \"old\", \"Role\": \"RoofAdmin\" } ], \"KeyPerFile\": true } }";
        pi.Write(Backup, content);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers()));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Read(SettingsFile).Should().Be(content);
    }

    [TestMethod]
    public async Task ABackupGivenWithoutItsFullPath_IsRefused()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers("backup/appsettings.Local.json")));

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("The settings to import (backup/appsettings.Local.json) must be given by their full path");
    }
}
