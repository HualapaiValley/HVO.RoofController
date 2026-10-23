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
    private const string OlderRelease = "/root/release-3.9.0";

    // Stands for the answers file's path in a test's arguments.
    private const string AnswersFile = "ANSWERS";

    // The folder of the machine's record, which an upgrade syncs before its first change.
    private static readonly string RecordFolder = Path.GetDirectoryName(InstallPaths.SystemRecord)!;

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
        // With the backup this installer took before it was replaced (#130), which the new one keeps in place of another.
        var backup = Record(pi).UpgradeBackup!;
        pi.Launches[0].Environment.Should().Equal(new Dictionary<string, string>
        {
            [LifecycleCommands.ContinuedVariable] = NewVersion,
            [LifecycleCommands.UpgradeBackupVariable] = backup,
            [LifecycleCommands.UpgradeBackupSha256Variable] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pi.OnDisk(backup))))
        });
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
        record.UpgradingFrom.Should().BeNull("the upgrade finished");
        pi.Read(InstallPaths.SystemLog).Should().Contain($"version {NewVersion}, its upgrade from 4.0.0 not finished yet", "the record said the new release before its first change");
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
    [DataRow("3.9.0", "upgrade", "--version", "3.9.0")]
    [DataRow("3.9.0", "upgrade")]
    [DataRow("3.9.0", "upgrade", "--release", OlderRelease)]
    [DataRow("4.0.0-rc.1", "upgrade", "--version", "4.0.0-rc.1")]
    [DataRow("3.9.0", "upgrade", "--plan")]
    [DataRow("3.9.0", "upgrade", "--plan", "--release", OlderRelease)]
    public async Task AnUpgrade_NeverGoesBack(string older, params string[] arguments)
    {
        // 3.9.0 is GitHub's latest release, and the release in a folder (--release, which install.sh --from gives).
        using var pi = await InstalledPiAsync();
        pi.WithRelease("3.9.0", latest: true);
        pi.Write($"{OlderRelease}/release.json", FakeMachine.ReleaseJson("3.9.0", FakeMachine.ControllerDigestFor("3.9.0"), FakeMachine.EmulatorDigestFor("3.9.0")));
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync(arguments);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"4.0.0 is installed here, newer than {older}: an upgrade never goes back.").And.Contain("sudo hvo-roof-install rollback");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Launches.Should().BeEmpty();
        pi.Deploys.Should().ContainSingle("only the install deployed the controller");
    }

    [TestMethod]
    public async Task AReleaseCandidate_UpgradesToItsRelease()
    {
        using var pi = KioskPi().WithRelease("4.0.1-rc.1");
        pi.InstallerVersion = "4.0.1-rc.1";
        var install = await pi.RunAsync("--setup-prerequisites", "--answers", pi.WriteAnswers(ControllerAndKiosk));
        install.ExitCode.Should().Be(0, install.ToString());
        pi.WithRelease(NewVersion, latest: true);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"4.0.1-rc.1 is installed here; {NewVersion} replaces it.");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.PreviousVersion == "4.0.1-rc.1");
    }

    // As root, and as a person planning the machine's roles without sudo (#131): the machine's record says what is
    // installed, whatever the person's own says, or when they have none.
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(true, "3.9.0")]
    [DataRow(true, "4.1.1")]
    public async Task AnOlderInstaller_DoesNotInstallOverANewerRelease(bool asPerson, string? personsRelease)
    {
        using var pi = await UpgradedPiAsync();
        if (asPerson)
        {
            pi.AsPerson();
            PersonsRecord(pi, personsRelease);
        }

        pi.InstallerVersion = "4.0.0";
        var answers = pi.WriteAnswers(ControllerAndKiosk);
        var files = pi.Snapshot(InstallPaths.SystemLog);

        // Without sudo only a plan of the machine's roles is open to the person.
        var runs = new List<InstallerRun>();
        if (!asPerson)
        {
            runs.Add(await pi.RunAsync("--setup-prerequisites", "--answers", answers));
        }

        runs.Add(await pi.RunAsync("--plan", "--answers", answers));
        foreach (var run in runs)
        {
            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
            run.Error.Should().Contain($"{NewVersion} is installed here, newer than this installer (4.0.0). To go back to the release before it: sudo hvo-roof-install rollback")
                .And.NotContain("4.1.1 is installed here", "the person's hvo-roof is not what this run installs");
        }

        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Deploys.Should().HaveCount(2, "the install and the upgrade deployed the controller, and nothing since");
    }

    // The machine's 4.0.0 and the person's hvo-roof 4.1.1 (#131): 4.0.1's installer plans the machine's upgrade, without
    // sudo, by the machine's record.
    [TestMethod]
    public async Task APersonsPlanForTheMachinesRoles_IsAnUpgrade_WhenTheirOwnReleaseIsNewer()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        pi.AsPerson();
        PersonsRecord(pi, "4.1.1");
        pi.InstallerVersion = NewVersion;
        var answers = pi.WriteAnswers(ControllerAndKiosk);
        var files = pi.Snapshot();

        var plan = await pi.RunAsync("--plan", "--answers", answers);

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain($"Upgrading from 4.0.0 to {NewVersion}.");
        plan.Error.Should().NotContain("newer than this installer");
        pi.Snapshot().Should().Equal(files);
    }

    // A run of both scopes is refused for that alone, but each record is checked against its own roles (#131).
    [TestMethod]
    public async Task EachScopesRecord_IsCheckedForItsOwnRoles()
    {
        using var pi = await UpgradedPiAsync();
        pi.AsPerson();
        PersonsRecord(pi, "4.1.1");

        async Task<IReadOnlyList<string>> ProblemsAsync(string installer, params InstallRole[] roles)
        {
            var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, installer, FakeMachine.Clock);
            session.Answers = new InstallAnswers { Roles = roles };
            return session.VersionProblems();
        }

        (await ProblemsAsync(NewVersion, InstallRole.Controller, InstallRole.Kiosk)).Should().BeEmpty();
        (await ProblemsAsync(NewVersion, InstallRole.Cli)).Should().ContainSingle()
            .Which.Should().Be($"4.1.1 is installed here, newer than this installer ({NewVersion}). To go back to the release before it: hvo-roof-install rollback");
        (await ProblemsAsync(NewVersion, InstallRole.Controller, InstallRole.Cli)).Should().ContainSingle().Which.Should().StartWith("4.1.1 is installed here");
        (await ProblemsAsync("4.0.0", InstallRole.Controller, InstallRole.Cli)).Should().Equal(
            $"{NewVersion} is installed here, newer than this installer (4.0.0). To go back to the release before it: sudo hvo-roof-install rollback",
            "4.1.1 is installed here, newer than this installer (4.0.0). To go back to the release before it: hvo-roof-install rollback");
    }

    // 4.0.0 to 4.0.1, stopped part way (#131): the controller is deployed before the kiosk, whose download then fails.
    [TestMethod]
    public async Task AnUpgradeThatStoppedPartWay_IsFinishedByUpgradingAgain_AndRolledBackOneRelease()
    {
        using var pi = await StoppedUpgradePiAsync();
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion, "the controller is deployed before the kiosk");
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram("4.0.0")));
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0" && record.PreviousVersion == "4.0.0");
        pi.WithRelease(NewVersion, latest: true, upgradeNotes: Notes);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here: finishing it.")
            .And.Contain($"Upgrade notes for {NewVersion}:")
            .And.Contain($"Finishing the upgrade from 4.0.0 to {NewVersion}.")
            .And.Contain($"the upgrade from 4.0.0 to {NewVersion} finishes");
        pi.Launches.Should().ContainSingle("the installer in place is the new release's");
        pi.Deploys.Should().HaveCount(2, "the controller runs the new release already");
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram(NewVersion)));
        pi.Read(KioskSteps.PreviousProgram).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram("4.0.0")));
        var record = Record(pi);
        record.Version.Should().Be(NewVersion);
        record.PreviousVersion.Should().Be("4.0.0");
        record.UpgradingFrom.Should().BeNull();
        record.RolledBackFrom.Should().BeNull();

        var rollback = await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        rollback.Output.Should().Contain($"{NewVersion} is installed here; going back to 4.0.0, the release before.");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.State == "running");
        Record(pi).Should().Match<InstallRecord>(back => back.Version == "4.0.0" && back.PreviousVersion == null && back.RolledBackFrom == NewVersion);
        pi.InstallerVersion = "4.0.0";
        var again = await pi.RunAsync("rollback");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain($"rolled back from {NewVersion} already");
        pi.DeployOptions.Count(options => options == "--rollback").Should().Be(1);
        pi.Unexpected.Should().BeEmpty();
    }

    // Each way back but rollback, by the installer in place (the new release's) and by the release before's own, as its
    // install.sh would run it: the record says the new release, so even an installer from before #131 refuses.
    [TestMethod]
    [DataRow(NewVersion, "upgrade", "--version", "4.0.0")]
    [DataRow(NewVersion, "upgrade", "--plan", "--version", "4.0.0")]
    [DataRow("4.0.0", "upgrade", "--version", "4.0.0")]
    [DataRow("4.0.0", "--setup-prerequisites", "--answers", AnswersFile)]
    [DataRow("4.0.0", "--plan", "--answers", AnswersFile)]
    [DataRow("4.0.0", "--plan")]
    public async Task AnUpgradeThatStoppedPartWay_IsNeverTakenBack_ButByRollback(string installer, params string[] arguments)
    {
        using var pi = await StoppedUpgradePiAsync();
        var answers = pi.WriteAnswers(ControllerAndKiosk);
        pi.InstallerVersion = installer;
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync([.. arguments.Select(argument => argument == AnswersFile ? answers : argument)]);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here, and parts of {NewVersion} may be in place")
            .And.Contain("sudo hvo-roof-install rollback");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Launches.Should().ContainSingle("only the upgrade that stopped handed over");
        pi.Deploys.Should().HaveCount(2, "the install and the upgrade that stopped deployed the controller, and nothing since");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
    }

    // 4.0.0 uninstalled with its data kept, then 4.0.1 installed over it and stopped after its controller (#131): the record
    // says 4.0.1, so 4.0.0's installer does not install over it, and 4.0.1's finishes it as the first install it is.
    [TestMethod]
    public async Task AReinstallThatStoppedPartWay_IsNotTakenBack_AndIsFinishedByItsRelease()
    {
        using var pi = await InstalledPiAsync();
        var uninstall = await pi.RunAsync("uninstall", "--yes");
        uninstall.ExitCode.Should().Be(0, uninstall.ToString());
        pi.WithRelease(NewVersion);
        pi.FileDownloads.Remove(KioskTarballUri(NewVersion));
        pi.InstallerVersion = NewVersion;
        var answers = pi.WriteAnswers(ControllerAndKiosk);

        var stopped = await pi.RunAsync("--setup-prerequisites", "--answers", answers);

        stopped.ExitCode.Should().Be((int)InstallerExitCode.Failed, stopped.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0" && record.UninstalledAt != null && record.PreviousVersion == null);

        pi.InstallerVersion = "4.0.0";
        var files = pi.Snapshot(InstallPaths.SystemLog);
        var older = await pi.RunAsync("--setup-prerequisites", "--answers", answers);

        older.ExitCode.Should().Be((int)InstallerExitCode.Refused, older.ToString());
        older.Error.Should().Contain($"The install of {NewVersion} over 4.0.0's data did not finish here, and parts of it may be in place, newer than this installer (4.0.0).");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);

        pi.WithRelease(NewVersion);
        pi.InstallerVersion = NewVersion;
        var finished = await pi.RunAsync("--setup-prerequisites", "--answers", answers);

        finished.ExitCode.Should().Be(0, finished.ToString());
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram(NewVersion)));
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == null && record.UninstalledAt == null && record.PreviousVersion == null);
        pi.Unexpected.Should().BeEmpty();
    }

    // 4.0.0, then 4.0.1, then 4.0.2 stopped after its controller (#131): a rollback goes back to 4.0.1, the release that
    // upgrade started from, and leaves the kiosk it never reached.
    [TestMethod]
    public async Task ARollback_AfterAnUpgradeThatStoppedPartWay_GoesBackToTheReleaseItStartedFrom()
    {
        using var pi = await UpgradedPiAsync();
        pi.WithRelease("4.0.2", latest: true);
        pi.FileDownloads.Remove(KioskTarballUri("4.0.2"));
        var stopped = await pi.RunAsync("upgrade");
        stopped.ExitCode.Should().Be((int)InstallerExitCode.Failed, stopped.ToString());
        pi.InstallerVersion = "4.0.2";
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.2");

        var run = await pi.RunAsync("rollback");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"The upgrade from {NewVersion} to 4.0.2 did not finish here; going back to {NewVersion}, the release before.");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == NewVersion && running.State == "running");
        pi.Containers[ControllerRollbackStep.PreviousContainer].Should().Match<FakeContainer>(kept => kept.Version == "4.0.2" && kept.State == "exited");
        pi.Read(KioskSteps.Program).Should().Be(Encoding.UTF8.GetString(FakeMachine.KioskProgram(NewVersion)));
        pi.Read(InstallPaths.SystemInstaller).Should().Be(Encoding.UTF8.GetString(FakeMachine.InstallerProgram(NewVersion, "linux-arm64")));
        var record = Record(pi);
        record.Version.Should().Be(NewVersion);
        record.PreviousVersion.Should().BeNull();
        record.RolledBackFrom.Should().Be("4.0.2");
        record.UpgradingFrom.Should().BeNull();

        pi.InstallerVersion = NewVersion;
        var again = await pi.RunAsync("rollback");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("rolled back from 4.0.2 already");
        pi.DeployOptions.Count(options => options == "--rollback").Should().Be(1);
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        pi.Unexpected.Should().BeEmpty();
    }

    // The mark is synced to disk before the upgrade's first change (#131). A power loss once the new controller is deployed
    // loses each rename into the record's folder since that folder's last sync, and still leaves the record saying the new
    // release: 4.0.0's installer does not take the controller back.
    [TestMethod]
    public async Task AnUpgradesMark_IsSyncedBeforeItsFirstChange_SoAPowerLossLeavesItInPlace()
    {
        using var pi = await InstalledPiAsync();
        pi.TrackDurability(InstallPaths.SystemRecord);
        pi.WithRelease(NewVersion, latest: true);
        pi.FileDownloads.Remove(KioskTarballUri(NewVersion));

        // The new release's installer, run by the person: the mark is the run's first change.
        pi.InstallerVersion = NewVersion;
        pi.DirectorySyncs.Clear();
        var files = pi.Snapshot(InstallPaths.SystemLog, InstallPaths.SystemRecord);
        IReadOnlyDictionary<string, string>? atSync = null;
        var deploysAtSync = -1;
        pi.DuringDirectorySync = folder =>
        {
            if (atSync is null && folder == RecordFolder)
            {
                atSync = pi.Snapshot(InstallPaths.SystemLog, InstallPaths.SystemRecord);
                deploysAtSync = pi.Deploys.Count;
            }
        };
        string? durableAtDeploy = null;
        pi.DuringDeploy = () => durableAtDeploy ??= pi.DurableText(InstallPaths.SystemRecord);

        var stopped = await pi.RunAsync("upgrade");

        stopped.ExitCode.Should().Be((int)InstallerExitCode.Failed, stopped.ToString());
        pi.DirectorySyncs.Where(folder => folder == RecordFolder).Should().ContainSingle("the run stopped before its last step");
        pi.DirectorySyncs.TakeWhile(folder => folder != RecordFolder).Should().Contain(BackupCommands.DefaultFolder, "the stores' backup is made durable first (#130)");
        WithoutBackups(atSync!).Should().Equal(files, "nothing but the record changed before its folder was synced, but for the stores' backup");
        atSync!.Keys.Should().Contain(path => path.StartsWith(BackupCommands.DefaultFolder + "/", StringComparison.Ordinal), "the stores are backed up before the mark (#130)");
        deploysAtSync.Should().Be(1, "only the install had deployed the controller");
        durableAtDeploy.Should().NotBeNull();
        InstallRecord.Parse(durableAtDeploy!).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);

        pi.PowerLoss();

        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0");
        pi.InstallerVersion = "4.0.0";
        var back = await pi.RunAsync("upgrade", "--version", "4.0.0");

        back.ExitCode.Should().Be((int)InstallerExitCode.Refused, back.ToString());
        back.Error.Should().Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here");
        pi.Deploys.Should().HaveCount(2, "the install and the upgrade deployed the controller, and nothing since");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
    }

    // When the record's folder cannot be synced (#131), the run stops before its first change: the mark may be on disk or
    // not, but nothing else changed. A run again does not go on from the unsynced mark it finds either. Once the folder
    // syncs, the upgrade goes ahead.
    [TestMethod]
    public async Task AnUpgrade_WhoseRecordCannotBeSynced_ChangesNothingElse_UntilItCanBe()
    {
        using var pi = await InstalledPiAsync();
        pi.TrackDurability(InstallPaths.SystemRecord);
        pi.WithRelease(NewVersion, latest: true);
        pi.InstallerVersion = NewVersion;

        // The record's folder alone: the stores' backup (#130) is made durable before it.
        var failing = true;
        pi.DuringDirectorySync = folder => pi.DirectorySyncFailure = failing && folder == RecordFolder ? "Input/output error" : null;
        var files = pi.Snapshot(InstallPaths.SystemLog, InstallPaths.SystemRecord);

        foreach (var attempt in new[] { "the first run", "a run with the unsynced mark in place" })
        {
            var run = await pi.RunAsync("upgrade");

            run.ExitCode.Should().Be((int)InstallerExitCode.Failed, $"{attempt}: {run}");
            run.Error.Should().Contain($"Before its first change, the installer could not make sure that {InstallPaths.SystemRecord} says {NewVersion} after a power loss")
                .And.Contain("Input/output error")
                .And.Contain("Nothing else was changed.");
            Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0", attempt);
            WithoutBackups(pi.Snapshot(InstallPaths.SystemLog, InstallPaths.SystemRecord)).Should().Equal(files, attempt);
            pi.Deploys.Should().ContainSingle("only the install deployed the controller");
            pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.0");
        }

        pi.Read(InstallPaths.SystemLog).Should().Contain("Failed: Before its first change");
        pi.PowerLoss();
        Record(pi).Should().Match<InstallRecord>(record => record.Version == "4.0.0" && record.UpgradingFrom == null, "the mark was never synced");

        failing = false;
        var synced = await pi.RunAsync("upgrade");

        synced.ExitCode.Should().Be(0, synced.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        InstallRecord.Parse(pi.DurableText(InstallPaths.SystemRecord)!).Should().Match<InstallRecord>(
            record => record.Version == NewVersion && record.PreviousVersion == "4.0.0" && record.UpgradingFrom == null);
        pi.Unexpected.Should().BeEmpty();
    }

    // A snapshot without the backups folder: an upgrade backs up the stores (#130) before it marks its record (#131), and
    // that copy is not a change to what is installed.
    private static Dictionary<string, string> WithoutBackups(IReadOnlyDictionary<string, string> snapshot)
        => snapshot.Where(entry => !entry.Key.StartsWith("/var/backups", StringComparison.Ordinal)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    // The machine's own sync (#131): the folder opened and synced, with fsync on Linux and F_FULLFSYNC on macOS. A folder
    // that cannot be opened is an IOException, which stops the run.
    [TestMethod]
    public void TheMachinesSync_SyncsAFolder_AndFailsForOneThatIsNotThere()
    {
        var machine = InstallerMachine.Current();
        var folder = Directory.CreateTempSubdirectory("hvo-roof-sync-").FullName;
        try
        {
            machine.WriteDurably(Path.Join(folder, "install.json"), "{}\n", Modes.File);

            File.ReadAllText(Path.Join(folder, "install.json")).Should().Be("{}\n");
            var missing = Path.Join(folder, "missing");
            FluentActions.Invoking(() => machine.SyncDirectory(missing)).Should().Throw<IOException>()
                .WithMessage($"Could not sync the folder {missing} to disk (open: *");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // A rig on a Mac (#131): its record's folder is synced with F_FULLFSYNC, which has the drive write its cache out, and
    // nothing less. When that fails, an I/O error or a file system without it, the upgrade stops before its first change,
    // as one whose sync fails on Linux does: fsync, which can leave the mark in the drive's cache, is never tried in its
    // place, so a power loss cannot bring the older record back under the newer controller.
    [TestMethod]
    [DataRow(5, DisplayName = "EIO")]
    [DataRow(45, DisplayName = "ENOTSUP")]
    [DataRow(22, DisplayName = "EINVAL")]
    public async Task ARigOnAMac_WhoseRecordsFolderCannotBeFullySynced_IsNotUpgraded(int errno)
    {
        using var mac = await InstalledMacRigAsync();
        var record = InstallPaths.UserRecord(mac.Machine);
        mac.TrackDurability(record);
        mac.WithRelease(NewVersion, latest: true);
        mac.InstallerVersion = NewVersion;
        mac.FullFsyncError = errno;
        mac.SyncCalls.Clear();
        var deploys = mac.Deploys.Count;

        var run = await mac.RunAsync("upgrade");

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"Before its first change, the installer could not make sure that {record} says {NewVersion} after a power loss")
            .And.Contain("(F_FULLFSYNC: ")
            .And.Contain("Nothing else was changed.");
        if (errno == 5)
        {
            run.Error.Should().Contain("(F_FULLFSYNC: Input/output error).").And.NotContain("its file system");
        }
        else
        {
            run.Error.Should().Contain("its file system cannot have the drive write its cache out, which a Mac needs for a change to survive a power loss");
        }

        mac.Read(InstallPaths.Log(mac.Machine)).Should().Contain("Failed: Before its first change");
        mac.SyncCalls.Should().Equal(["open", "F_FULLFSYNC", "close"], "fsync is never tried in F_FULLFSYNC's place");
        mac.Deploys.Should().HaveCount(deploys, "nothing but the record changed");
        mac.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.0");
        mac.PowerLoss();
        InstallRecord.Parse(mac.Read(record)).Should().Match<InstallRecord>(found => found.Version == "4.0.0" && found.UpgradingFrom == null, "the mark never reached the disk");
    }

    // A rig on a Mac whose record's folder F_FULLFSYNC syncs (#131) is upgraded, and its record says so after a power loss.
    [TestMethod]
    public async Task ARigOnAMac_IsUpgraded_WithItsRecordsFolderFullySynced()
    {
        using var mac = await InstalledMacRigAsync();
        var record = InstallPaths.UserRecord(mac.Machine);
        mac.TrackDurability(record);
        mac.WithRelease(NewVersion, latest: true);
        mac.InstallerVersion = NewVersion;
        mac.SyncCalls.Clear();

        var run = await mac.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        mac.SyncCalls.Should().Contain("F_FULLFSYNC").And.NotContain("fsync");
        mac.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
        mac.PowerLoss();
        InstallRecord.Parse(mac.Read(record)).Should().Match<InstallRecord>(
            found => found.Version == NewVersion && found.PreviousVersion == "4.0.0" && found.UpgradingFrom == null);
        mac.Unexpected.Should().BeEmpty();
    }

    // 4.0.0 to 4.0.1 stopped before its controller, and 4.0.2 the latest (#131). While that upgrade is unfinished no other
    // release goes over it, by upgrade, the install, the wizard or --plan: rollback must still go back to 4.0.0, whose
    // controller the deploy script left running and would keep.
    [TestMethod]
    [DataRow(NewVersion, "upgrade")]
    [DataRow(NewVersion, "upgrade", "--version", "4.0.2")]
    [DataRow(NewVersion, "upgrade", "--plan")]
    [DataRow("4.0.2", "upgrade")]
    [DataRow("4.0.2", "--setup-prerequisites", "--answers", AnswersFile)]
    [DataRow("4.0.2", "--plan", "--answers", AnswersFile)]
    [DataRow("4.0.2", "--plan")]
    public async Task AnUpgradeThatStoppedBeforeItsController_IsNotReplacedByAnotherRelease(string installer, params string[] arguments)
    {
        using var pi = await StoppedBeforeControllerPiAsync();
        var answers = pi.WriteAnswers(ControllerAndKiosk);
        pi.InstallerVersion = installer;
        var files = pi.Snapshot(InstallPaths.SystemLog);
        var (deploys, launches) = (pi.Deploys.Count, pi.Launches.Count);

        var run = await pi.RunAsync([.. arguments.Select(argument => argument == AnswersFile ? answers : argument)]);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here: finish it, or go back, before ")
            .And.Contain($"Finish it with sudo hvo-roof-install upgrade --version {NewVersion}")
            .And.Contain("go back to 4.0.0 with sudo hvo-roof-install rollback (it needs only 4.0.0's release)");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Deploys.Should().HaveCount(deploys);
        pi.Launches.Should().HaveCount(launches);
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.0");
    }

    // The same, with 4.0.1's release gone since (#131): finishing the upgrade cannot be done, and says to roll back, which
    // needs only 4.0.0's release. After the rollback 4.0.2 goes ahead, keeping 4.0.0 for its own rollback, which works.
    [TestMethod]
    public async Task AnUpgradeThatStoppedBeforeItsController_IsRolledBack_WhenItsReleaseIsGone_ThenAnotherGoesAhead()
    {
        using var pi = await StoppedBeforeControllerPiAsync();
        pi.Downloads.Remove(ReleaseManifest.DownloadUri(NewVersion).ToString());
        var deploys = pi.Deploys.Count;

        var newer = await pi.RunAsync("upgrade");
        var finish = await pi.RunAsync("upgrade", "--version", NewVersion);

        newer.ExitCode.Should().Be((int)InstallerExitCode.Refused, newer.ToString());
        finish.ExitCode.Should().Be((int)InstallerExitCode.Failed, finish.ToString());
        finish.Error.Should().Contain($"The installer could not get release.json for {NewVersion}")
            .And.Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here, and finishing it needs release {NewVersion}. "
                + "Without it, go back to 4.0.0 with sudo hvo-roof-install rollback, which needs only 4.0.0's release.");
        pi.Deploys.Should().HaveCount(deploys);

        var rollback = await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        rollback.Output.Should().Contain($"The upgrade from 4.0.0 to {NewVersion} did not finish here; going back to 4.0.0, the release before.");
        pi.DeployOptions.Should().NotContain("--rollback", "4.0.0's controller runs still");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.State == "running");
        Record(pi).Should().Match<InstallRecord>(back => back.Version == "4.0.0" && back.RolledBackFrom == NewVersion && back.UpgradingFrom == null && back.PreviousVersion == null);
        pi.Read(InstallPaths.SystemInstaller).Should().Be(Encoding.UTF8.GetString(FakeMachine.InstallerProgram("4.0.0", "linux-arm64")));

        pi.InstallerVersion = "4.0.0";
        var upgrade = await pi.RunAsync("upgrade");

        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.2");
        pi.Containers[ControllerRollbackStep.PreviousContainer].Should().Match<FakeContainer>(kept => kept.Version == "4.0.0" && kept.State == "exited");
        Record(pi).Should().Match<InstallRecord>(record => record.Version == "4.0.2" && record.PreviousVersion == "4.0.0" && record.UpgradingFrom == null);

        pi.InstallerVersion = "4.0.2";
        var back = await pi.RunAsync("rollback");

        back.ExitCode.Should().Be(0, back.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.State == "running");
        Record(pi).Should().Match<InstallRecord>(record => record.Version == "4.0.0" && record.RolledBackFrom == "4.0.2");
        pi.Unexpected.Should().BeEmpty();
    }

    // The mark itself never replaces another release's unfinished upgrade (#131), whichever way a run reached it.
    [TestMethod]
    public async Task TheMark_NeverReplacesAnotherReleasesUnfinishedUpgrade()
    {
        using var pi = await StoppedBeforeControllerPiAsync();
        var record = pi.Read(InstallPaths.SystemRecord);
        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, "4.0.2", FakeMachine.Clock);
        var step = new RecordStep(InstallScope.System, InstallPaths.SystemRecord, (_, existing) => existing!);

        FluentActions.Invoking(() => step.MarkUpgradeStarted(session.Context)).Should().Throw<InstallerRefusedException>()
            .WithMessage($"The upgrade from 4.0.0 to {NewVersion} did not finish here: finish it, or go back, before installing 4.0.2*");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
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
        pi.Exists(KioskSteps.TerminalUnitFile).Should().BeFalse();
        pi.Exists(KioskSteps.TerminalHandoffFile).Should().BeFalse();
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
        var older = await pi.RunAsync("--setup-prerequisites", "--answers", pi.WriteAnswers(ControllerAndKiosk));

        older.ExitCode.Should().Be((int)InstallerExitCode.Refused, older.ToString());
        older.Error.Should().Contain($"{NewVersion} was uninstalled here, keeping its data, which is newer than this installer (4.0.0)");

        // A reinstall finds the data: the controller's keys are the ones it had.
        pi.InstallerVersion = NewVersion;
        var reinstall = await pi.RunAsync("--setup-prerequisites", "--answers", pi.WriteAnswers(ControllerAndKiosk));

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
        var run = await pi.RunAsync("--setup-prerequisites", "--answers", pi.WriteAnswers(ControllerAndKiosk));
        run.ExitCode.Should().Be(0, run.ToString());
        return pi;
    }

    // A rig on a Mac, installed as roy.
    private static async Task<FakeMachine> InstalledMacRigAsync()
    {
        var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        var run = await mac.RunAsync("--answers", mac.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }));
        run.ExitCode.Should().Be(0, run.ToString());
        return mac;
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

    // 4.0.0 upgraded to NewVersion, stopped part way: the new controller deployed, then the kiosk's download failed. The
    // installer in place is NewVersion's.
    private static async Task<FakeMachine> StoppedUpgradePiAsync()
    {
        var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        pi.FileDownloads.Remove(KioskTarballUri(NewVersion));
        var run = await pi.RunAsync("upgrade");
        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        pi.InstallerVersion = NewVersion;
        return pi;
    }

    // 4.0.0 upgraded to NewVersion, stopped before its controller: the deploy script failed (its pull, say), so 4.0.0's
    // controller runs still. The installer in place is NewVersion's, and 4.0.2 is the latest release.
    private static async Task<FakeMachine> StoppedBeforeControllerPiAsync()
    {
        var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        pi.DeployFailure = "[deploy] ERROR: docker pull failed: manifest unknown";
        var run = await pi.RunAsync("upgrade");
        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        pi.DeployFailure = null;
        pi.InstallerVersion = NewVersion;
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.0");
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.UpgradingFrom == "4.0.0" && record.PreviousVersion == "4.0.0");
        pi.WithRelease("4.0.2", latest: true);
        return pi;
    }

    private static string KioskTarballUri(string version) => ReleaseManifest.DownloadUri(version, FakeMachine.KioskAssetName(version)).ToString();

    // The person's own record: hvo-roof of release version, or none.
    private static void PersonsRecord(FakeMachine pi, string? version)
    {
        if (version is not null)
        {
            pi.Write(InstallPaths.UserRecord(pi.Machine), new InstallRecord { Scope = InstallScope.User, Roles = [InstallRole.Cli], Version = version, InstallerVersion = version }.ToJson());
        }
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
