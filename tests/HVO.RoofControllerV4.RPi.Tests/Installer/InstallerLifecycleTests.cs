using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// <c>upgrade</c>, <c>rollback</c> and <c>uninstall</c> (#72): an upgrade hands over to the new release's installer, which
/// deploys its controller (the deploy script keeping the one it replaces) and its kiosk (keeping the program it replaces);
/// a rollback puts both back; an uninstall stops the controller with a verified Stop and removes what the record says,
/// keeping the data unless <c>--purge</c>, which needs a backup (or <c>--no-backup</c>) and the machine's name.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerLifecycleTests
{
    private const string CommandLine = "/boot/firmware/cmdline.txt";
    private const string Booted = "console=serial0,115200 console=tty1 root=PARTUUID=0a1b2c3d-02 rootfstype=ext4 fsck.repair=yes rootwait";
    private const string NewVersion = "4.0.1";

    // The release installed has notes too: an upgrade from it shows only the ones after it.
    private static readonly UpgradeNote[] Notes = [new("4.0.0", "The first release."), new(NewVersion, "The kiosk's settings gain a brightness.\nNothing to do by hand.")];

    private static readonly InstallAnswers ControllerAndKiosk = new() { Roles = [InstallRole.Controller, InstallRole.Kiosk] };

    [TestMethod]
    public async Task AnUpgrade_HandsOverToTheNewInstaller_WhichDeploysItsRelease_KeepingWhatItReplaces_AndASecondRunChangesNothing()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, upgradeNotes: Notes);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"4.0.0 is installed here; {NewVersion} replaces it.")
            .And.Contain($"Upgrade notes for {NewVersion}:\n  The kiosk's settings gain a brightness.\n  Nothing to do by hand.", Exactly.Once(), "the installer handed over to does not show them again")
            .And.NotContain("Upgrade notes for 4.0.0:")
            .And.Contain($"Release {NewVersion}: {ReleaseManifest.PageUri(NewVersion)}")
            .And.Contain($"Handing over to release {NewVersion}'s installer.")
            .And.Contain($"Upgrading from 4.0.0 to {NewVersion}.");
        pi.Launches.Should().ContainSingle();
        pi.Launches[0].Path.Should().Be(pi.OnDisk(InstallPaths.SystemInstaller));
        pi.Launches[0].Arguments.Should().Equal("upgrade", "--version", NewVersion);
        pi.Launches[0].Environment.Should().Equal(new Dictionary<string, string> { [LifecycleCommands.ContinuedVariable] = NewVersion });
        pi.Environment.Should().NotContainKey(LifecycleCommands.ContinuedVariable, "only the program it hands over to has it");
        pi.Read(InstallPaths.SystemInstaller).Should().Be(Encoding.UTF8.GetString(FakeMachine.InstallerProgram(NewVersion, "linux-arm64")));

        pi.Deploys.Should().HaveCount(2);
        pi.DeployOptions.Should().Equal(string.Empty, string.Empty);
        pi.Deploys[^1]["IMAGE_REF"].Should().Be($"ghcr.io/hualapaivalley/roof-controller:{NewVersion}@{FakeMachine.ControllerDigestFor(NewVersion)}");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        pi.Containers[ControllerRollbackStep.PreviousContainer].Should().Match<FakeContainer>(kept => kept.Version == "4.0.0" && kept.State == "exited");
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram(NewVersion)));
        pi.Read(KioskSteps.PreviousProgram).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram("4.0.0")));
        pi.KioskStarts.Should().Be(2, "the kiosk is started again to run the new program");

        var record = Record(pi);
        record.Version.Should().Be(NewVersion);
        record.PreviousVersion.Should().Be("4.0.0");
        record.RolledBackFrom.Should().BeNull();
        ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();

        // The one installed now is the new release's.
        pi.InstallerVersion = NewVersion;
        var files = pi.Snapshot(InstallPaths.SystemLog);
        var second = await pi.RunAsync("upgrade");

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain($"{NewVersion} is installed here: checking that everything is as it should be.").And.Contain("Nothing to change");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Deploys.Should().HaveCount(2);
        pi.Launches.Should().ContainSingle();
    }

    [TestMethod]
    public async Task AnUpgrade_RunWithTheNewReleasesOwnInstaller_ShowsItsNotes()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, upgradeNotes: Notes);
        pi.InstallerVersion = NewVersion;

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"Upgrade notes for {NewVersion}:\n  The kiosk's settings gain a brightness.\n  Nothing to do by hand.", Exactly.Once())
            .And.NotContain("Upgrade notes for 4.0.0:")
            .And.Contain($"Upgrading from 4.0.0 to {NewVersion}.");
        pi.Launches.Should().BeEmpty();
        Record(pi).Version.Should().Be(NewVersion);
    }

    [TestMethod]
    public async Task AnUpgradeAcrossSeveralReleases_ShowsEachOnesNotes_OldestFirst()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease("4.2.0", latest: true, upgradeNotes: [new("4.0.0", "Zero."), new("4.1.0", "Renew the certificate first."), new("4.2.0", "Two.")]);

        var run = await pi.RunAsync("upgrade", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Upgrade notes for 4.1.0:\n  Renew the certificate first.\n\nUpgrade notes for 4.2.0:\n  Two.\n")
            .And.NotContain("Upgrade notes for 4.0.0:");
    }

    [TestMethod]
    public async Task AnUpgradePlan_ShowsTheNewRelease_AndChangesNothing()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, upgradeNotes: Notes);
        var files = pi.Snapshot();

        var run = await pi.RunAsync("upgrade", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"Upgrade notes for {NewVersion}:")
            .And.Contain($"Release {NewVersion}'s installer makes the upgrade. As this installer (4.0.0) sees it:")
            .And.Contain(MachineSurveyor.ControllerContainer)
            .And.Contain(NewVersion);
        pi.Snapshot().Should().Equal(files);
        pi.Launches.Should().BeEmpty();
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task AnUpgrade_ToTheReleaseInstalled_ChecksIt_AndChangesNothing()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease("4.0.0", latest: true);
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("4.0.0 is installed here: checking that everything is as it should be.").And.Contain("Nothing to change");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Launches.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUpgrade_NeverGoesBack()
    {
        using var pi = await InstalledPiAsync();
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("upgrade", "--version", "3.9.0");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("4.0.0 is installed here, newer than 3.9.0: an upgrade never goes back.").And.Contain("hvo-roof-install rollback");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
    }

    [TestMethod]
    [DataRow("4.1")]
    [DataRow("v4.0.1")]
    [DataRow("../4.0.1")]
    public async Task AnUpgrade_ToSomethingNotAVersion_IsAUsageError(string version)
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync("upgrade", "--version", version);

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain($"--version {version} is not a release's version");
        pi.Launches.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUpgrade_HandingOverToAnInstallerOfAnotherRelease_StopsRatherThanGoingRound()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        pi.Launch = (path, arguments, environment) => pi.RunInstallerAsync(path, arguments, environment, version: "4.0.0");

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"was put in place as release {NewVersion}'s installer, but it is 4.0.0").And.Contain(ReleaseManifest.PageUri(NewVersion).ToString());
        pi.Launches.Should().ContainSingle("the installer it handed over to does not hand over again");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task AnUpgrade_WithoutRoot_SaysToUseSudo_AndAPlanNeedsNone()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        pi.AsPerson();

        var run = await pi.RunAsync("upgrade");
        var plan = await pi.RunAsync("upgrade", "--plan");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("sudo hvo-roof-install upgrade").And.Contain("--plan");
        plan.ExitCode.Should().Be(0, plan.ToString());
        pi.Launches.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NothingInstalled_IsRefused_ByUpgradeRollbackAndUninstall()
    {
        using var pi = KioskPi();

        foreach (var command in new[] { "upgrade", "rollback", "uninstall" })
        {
            var run = await pi.RunAsync(command);

            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
            run.Error.Should().Contain("Nothing is recorded as installed here");
        }

        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARollback_PutsBackTheReleaseBefore_AndThereIsNothingFurtherBack()
    {
        using var pi = await UpgradedPiAsync();

        var run = await pi.RunAsync("rollback");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"{NewVersion} is installed here; going back to 4.0.0, the release before.").And.Contain("Done rolling back.");
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.State == "running");
        pi.Containers[ControllerRollbackStep.PreviousContainer].Should().Match<FakeContainer>(kept => kept.Version == NewVersion && kept.State == "exited");
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram("4.0.0")));
        pi.Read(InstallPaths.SystemInstaller).Should().Be(Encoding.UTF8.GetString(FakeMachine.InstallerProgram("4.0.0", "linux-arm64")));

        var record = Record(pi);
        record.Version.Should().Be("4.0.0");
        record.PreviousVersion.Should().BeNull();
        record.RolledBackFrom.Should().Be(NewVersion);
        ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();

        pi.InstallerVersion = "4.0.0";
        var again = await pi.RunAsync("rollback");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain($"rolled back from {NewVersion} already");
        pi.DeployOptions.Count(options => options == "--rollback").Should().Be(1);
    }

    [TestMethod]
    public async Task AnUpgradeAfterARollback_GoesForwardAgain()
    {
        using var pi = await UpgradedPiAsync();
        (await pi.RunAsync("rollback")).ExitCode.Should().Be(0);
        pi.InstallerVersion = "4.0.0";

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        var record = Record(pi);
        record.Version.Should().Be(NewVersion);
        record.PreviousVersion.Should().Be("4.0.0");
        record.RolledBackFrom.Should().BeNull();
    }

    [TestMethod]
    public async Task ARollbackPlan_ChangesNothing()
    {
        using var pi = await UpgradedPiAsync();
        var files = pi.Snapshot();

        var run = await pi.RunAsync("rollback", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(ControllerRollbackStep.PreviousContainer);
        pi.Snapshot().Should().Equal(files);
        pi.DeployOptions.Should().NotContain("--rollback");
    }

    [TestMethod]
    public async Task ARollback_WithNothingBefore_IsRefused()
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync("rollback");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("4.0.0 is the first release installed here: there is no release before it to go back to.");
    }

    [TestMethod]
    public async Task ARollback_WithoutTheControllerTheDeployScriptKept_IsBlocked()
    {
        using var pi = await UpgradedPiAsync();
        pi.Containers.Remove(ControllerRollbackStep.PreviousContainer);

        var run = await pi.RunAsync("rollback");

        run.ExitCode.Should().NotBe(0, run.ToString());
        run.ToString().Should().Contain($"there is no {ControllerRollbackStep.PreviousContainer} to go back to");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        Record(pi).Version.Should().Be(NewVersion);
    }

    [TestMethod]
    public async Task ARig_IsUpgradedAndRolledBack_WithItsEmulator()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        (await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }))).ExitCode.Should().Be(0);
        bench.WithRelease(NewVersion, latest: true);

        var upgrade = await bench.RunAsync("upgrade");

        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Digest.Should().Be(FakeMachine.EmulatorDigestFor(NewVersion));
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == NewVersion && running.Emulated);

        bench.InstallerVersion = NewVersion;
        var rollback = await bench.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Digest.Should().Be(FakeMachine.EmulatorDigest);
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.Emulated && running.State == "running");
        bench.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUninstall_StopsTheController_RemovesWhatIsInstalled_AndKeepsTheData()
    {
        using var pi = await UpgradedPiAsync();
        var keys = pi.ApiKeyValues();

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Uninstalling the controller and the kiosk from roofpi; its data is kept:").And.Contain("Done uninstalling.");
        pi.DeployOptions[^1].Should().Be("--stop", "the roof is stopped, verified, before the controller goes");
        pi.Containers.Should().BeEmpty();
        pi.RemovedImages.Should().BeEquivalentTo(
            $"ghcr.io/hualapaivalley/roof-controller:{NewVersion}@{FakeMachine.ControllerDigestFor(NewVersion)}",
            $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{FakeMachine.ControllerDigest}");
        pi.Kiosk.Should().Be((false, false));
        pi.Exists(MachineSurveyor.KioskUnitFile).Should().BeFalse();
        pi.Exists(KioskSteps.BacklightRuleFile).Should().BeFalse();
        pi.Exists(KioskSteps.Program).Should().BeFalse();
        pi.Exists(KioskSteps.PreviousProgram).Should().BeFalse();
        pi.Exists(InstallPaths.SystemInstaller).Should().BeFalse();

        foreach (var kept in new[] { "/etc/hvo-roof/secrets", "/etc/hvo-roof/ca", "/etc/hvo-roof/https", "/var/lib/hvo-roof/identity", KioskSteps.DeviceKeyFile, KioskSteps.SettingsFile })
        {
            pi.Exists(kept).Should().BeTrue($"{kept} is data, kept without --purge");
        }

        var record = Record(pi);
        record.Roles.Should().BeEmpty();
        record.UninstalledAt.Should().Be(FakeMachine.Today);
        ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();

        var again = await pi.RunAsync("uninstall", "--yes");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("It was uninstalled here on 2026-10-01; its data is kept.").And.Contain("uninstall --purge");

        // An older release does not take over the newer one's data.
        pi.InstallerVersion = "4.0.0";
        var older = await pi.RunAsync("--answers", pi.WriteAnswers(ControllerAndKiosk));

        older.ExitCode.Should().Be((int)InstallerExitCode.Refused, older.ToString());
        older.Error.Should().Contain($"{NewVersion} was uninstalled here, keeping its data, which is newer than this installer (4.0.0)");

        // A reinstall finds the data: the controller's keys are the ones it had.
        pi.InstallerVersion = NewVersion;
        var reinstall = await pi.RunAsync("--answers", pi.WriteAnswers(ControllerAndKiosk));

        reinstall.ExitCode.Should().Be(0, reinstall.ToString());
        pi.ApiKeyValues().Should().BeEquivalentTo(keys);
        Record(pi).Should().Match<InstallRecord>(installed => installed.Roles.Count == 2 && installed.UninstalledAt == null);
    }

    [TestMethod]
    public async Task AnUninstall_WhoseControllerDoesNotStop_RemovesNothingElse_AndCanBeRunAgain()
    {
        using var pi = await InstalledPiAsync();
        pi.DeployFailure = "[deploy] The roof did not report Stopped.";

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().NotBe(0, run.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].State.Should().Be("running");
        foreach (var kept in new[] { KioskSteps.Program, MachineSurveyor.KioskUnitFile, KioskSteps.BacklightRuleFile, InstallPaths.SystemInstaller })
        {
            pi.Exists(kept).Should().BeTrue($"{kept} goes only after the controller has stopped");
        }

        pi.Kiosk.Should().Be((true, true), "the kiosk keeps showing the roof while the controller still runs it");
        Record(pi).Roles.Should().BeEquivalentTo([InstallRole.Controller, InstallRole.Kiosk]);

        pi.DeployFailure = null;
        var again = await pi.RunAsync("uninstall", "--yes");

        again.ExitCode.Should().Be(0, again.ToString());
        pi.Containers.Should().BeEmpty();
        pi.Exists(KioskSteps.Program).Should().BeFalse();
        Record(pi).Roles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUninstall_StopsTheControllerFirst_AndRemovesTheRecordAndTheInstallerLast()
    {
        using var pi = await InstalledPiAsync();
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        var plan = PlanBuilder.BuildUninstall(pi.Machine, survey, Record(pi), purge: true);

        var steps = plan.Steps.ToList();
        steps.FindIndex(step => step is ControllerStopStep).Should().BeLessThan(steps.FindIndex(step => step is KioskServiceRemovalStep));
        steps[^1].Target.Should().Be(InstallPaths.SystemInstaller);
        steps.Last(step => step.Kind == StepKind.Folder).Target.Should().Be(Path.GetDirectoryName(InstallPaths.SystemRecord));
    }

    [TestMethod]
    [DataRow("paused")]
    [DataRow("restarting")]
    public async Task AnUninstall_OfAControllerTheDeployScriptCannotStop_IsBlocked_AndChangesNothing(string state)
    {
        using var pi = await InstalledPiAsync();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = state };
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().NotBe(0, run.ToString());
        run.ToString().Should().Contain($"it is {state}, and the deploy script stops only a running controller");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
    }

    [TestMethod]
    public async Task APurge_UnderAControllerItDoesNotStop_IsRefused()
    {
        using var pi = await InstalledPiAsync();
        (await pi.RunAsync("uninstall", "--yes")).ExitCode.Should().Be(0);

        // Someone deployed a controller by hand since: the record says nothing is installed, so nothing stops it.
        pi.Containers[MachineSurveyor.ControllerContainer] = new FakeContainer();
        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("this uninstall does not stop it: --purge would remove its files from under it");
        pi.Exists("/etc/hvo-roof/secrets").Should().BeTrue();
        pi.Exists(InstallPaths.SystemRecord).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("version", "upgrade")]
    [DataRow("previousVersion", "rollback")]
    [DataRow("rolledBackFrom", "upgrade")]
    public async Task ARecord_WhoseVersionsAreNotReleaseVersions_IsRefused(string name, string command)
    {
        using var pi = await UpgradedPiAsync();
        var record = System.Text.Json.Nodes.JsonNode.Parse(pi.Read(InstallPaths.SystemRecord))!;
        record[name] = "4.0.1; rm -rf /";
        pi.Write(InstallPaths.SystemRecord, record.ToJsonString());

        var run = await pi.RunAsync(command);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"is not a valid install record: Its {name}, '4.0.1; rm -rf /', is not a release version");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
    }

    [TestMethod]
    public async Task AnUninstall_AsksFirst_AndWithNoOneToAsk_NeedsYes()
    {
        using var pi = await InstalledPiAsync();
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var unattended = await pi.RunAsync("uninstall");

        unattended.ExitCode.Should().Be((int)InstallerExitCode.Usage, unattended.ToString());
        unattended.Error.Should().Contain("give --yes");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);

        pi.Interactive = true;
        pi.Replies = _ => false;
        var declined = await pi.RunAsync("uninstall");

        declined.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, declined.ToString());
        declined.Error.Should().Contain("Nothing was removed.");
        pi.Questions.Should().ContainSingle().Which.Should().Be("Remove these?");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);

        pi.Replies = _ => true;
        var agreed = await pi.RunAsync("uninstall");

        agreed.ExitCode.Should().Be(0, agreed.ToString());
        pi.Containers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUninstallPlan_ChangesNothing()
    {
        using var pi = await InstalledPiAsync();
        var files = pi.Snapshot();

        var run = await pi.RunAsync("uninstall", "--plan", "--purge");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Uninstalling the controller and the kiosk from roofpi, and removing its data:")
            .And.Contain("/etc/hvo-roof")
            .And.Contain("/var/lib/hvo-roof");
        pi.Snapshot().Should().Equal(files);
        pi.DeployOptions.Should().NotContain("--stop");
        pi.Questions.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("--backup", "/root/b.tar.gz")]
    [DataRow("--no-backup")]
    [DataRow("--confirm", "roofpi")]
    public async Task ThePurgesOptions_WithoutPurge_AreUsageErrors(params string[] options)
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync(["uninstall", "--yes", .. options]);

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("--backup, --no-backup and --confirm go with --purge.");
    }

    [TestMethod]
    public async Task APurge_WithBackupAndNoBackup_IsAUsageError()
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--backup", "/root/b.tar.gz", "--no-backup");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("Give --backup FILE or --no-backup, not both.");
    }

    [TestMethod]
    public async Task APurge_NeedsTheMachinesName_AndABackupOrNoBackup()
    {
        using var pi = await InstalledPiAsync();
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var unnamed = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup");
        var wrong = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "otherpi");
        var noBackup = await pi.RunAsync("uninstall", "--purge", "--yes", "--confirm", "roofpi");

        unnamed.ExitCode.Should().Be((int)InstallerExitCode.Usage, unnamed.ToString());
        unnamed.Error.Should().Contain("--confirm roofpi");
        wrong.ExitCode.Should().Be((int)InstallerExitCode.Refused, wrong.ToString());
        wrong.Error.Should().Contain("otherpi is not this machine's name (roofpi): nothing was removed.");
        noBackup.ExitCode.Should().Be((int)InstallerExitCode.Usage, noBackup.ToString());
        noBackup.Error.Should().Contain("back it up first with --backup FILE, or give --no-backup");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
    }

    [TestMethod]
    public async Task APurge_RemovesTheData_AndTheRecord_AfterTheBackupItOffers()
    {
        using var pi = await InstalledPiAsync();
        var keys = pi.ApiKeyValues();
        pi.Interactive = true;
        pi.Replies = _ => true;
        pi.Typed = _ => "RoofPi";

        var run = await pi.RunAsync("uninstall", "--purge");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Questions.Should().Equal(
            "Remove all of this, data included? It cannot be undone.",
            "Type this machine's name, roofpi, to remove its data:",
            $"Back up the data first, to {BackupCommands.DefaultFolder}?");
        var backup = $"{BackupCommands.DefaultFolder}/hvo-roof-roofpi-20261001T090000Z.tar.gz";
        pi.Exists(backup).Should().BeTrue();
        pi.Mode(backup).Should().Be(Modes.PrivateFile);
        run.Output.Should().Contain($"to {backup}");

        foreach (var removed in new[] { "/etc/hvo-roof", "/var/lib/hvo-roof", KioskSteps.ConfigurationFolder, KioskSteps.ProgramFolder, InstallPaths.SystemRecord, InstallPaths.SystemInstaller })
        {
            pi.Exists(removed).Should().BeFalse($"--purge removes {removed}");
        }

        pi.Containers.Should().BeEmpty();
        ShowsNoSecret(pi, run, keys);
        pi.Unexpected.Should().BeEmpty();

        var again = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");

        again.ExitCode.Should().Be((int)InstallerExitCode.Refused, again.ToString());
        again.Error.Should().Contain("Nothing is recorded as installed here");
    }

    [TestMethod]
    public async Task APurge_WithBackupFile_WritesItThere_First()
    {
        using var pi = await InstalledPiAsync();
        pi.Folder("/srv/keep");

        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--confirm", "roofpi", "--backup", "/srv/keep/roof.tar.gz");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Mode("/srv/keep/roof.tar.gz").Should().Be(Modes.PrivateFile);
        pi.Exists("/etc/hvo-roof").Should().BeFalse();
    }

    [TestMethod]
    public async Task APurge_WithABackupInTheDataItRemoves_IsRefused_BeforeAnythingGoes()
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--confirm", "roofpi", "--backup", "/var/lib/hvo-roof/b.tar.gz");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("keep the backup somewhere else");
        pi.Exists("/etc/hvo-roof/secrets").Should().BeTrue();
        pi.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
    }

    [TestMethod]
    public async Task HvoRoof_IsUninstalledFromThePersonsOwnFolders_WithoutSudo()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");
        (await laptop.RunAsync("--answers", laptop.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Cli], Client = FakeMachine.ClientAnswers }))).ExitCode.Should().Be(0);
        laptop.Exists("/home/roy/.local/bin/hvo-roof").Should().BeTrue();

        var run = await laptop.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "laptop");

        run.ExitCode.Should().Be(0, run.ToString());
        laptop.Exists("/home/roy/.local/bin/hvo-roof").Should().BeFalse();
        laptop.Exists("/home/roy/.local/bin/hvo-roof-install").Should().BeFalse();
        laptop.Exists("/home/roy/.config/hvo-roof/install.json").Should().BeFalse();
        laptop.Unexpected.Should().BeEmpty();
    }

    // The controller and the kiosk, installed from release 4.0.0.
    internal static async Task<FakeMachine> InstalledPiAsync()
    {
        var pi = KioskPi();
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(ControllerAndKiosk));
        run.ExitCode.Should().Be(0, run.ToString());
        return pi;
    }

    // The controller and the kiosk, upgraded from 4.0.0 to NewVersion: the installer in place is NewVersion's.
    private static async Task<FakeMachine> UpgradedPiAsync()
    {
        var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        var run = await pi.RunAsync("upgrade");
        run.ExitCode.Should().Be(0, run.ToString());
        pi.InstallerVersion = NewVersion;
        return pi;
    }

    private static FakeMachine KioskPi() => new FakeMachine().WithPi().WithDisplay().Write(CommandLine, Booted + "\n");

    private static InstallRecord Record(FakeMachine pi) => InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));

    internal static void ShowsNoSecret(FakeMachine pi, InstallerRun run, IReadOnlyList<string>? keys = null)
    {
        var log = pi.Exists(InstallPaths.SystemLog) ? pi.Read(InstallPaths.SystemLog) : string.Empty;
        (keys ?? pi.ApiKeyValues()).Should().NotBeEmpty();
        foreach (var secret in keys ?? pi.ApiKeyValues())
        {
            run.ToString().Should().NotContain(secret);
            log.Should().NotContain(secret);
        }
    }
}
