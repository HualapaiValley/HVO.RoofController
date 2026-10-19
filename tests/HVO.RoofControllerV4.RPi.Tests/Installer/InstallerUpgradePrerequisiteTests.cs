using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Wizard;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Upgrade recovery precedes approved package mutations through every installer entry path (#146).</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerUpgradePrerequisiteTests
{
    private const string NewVersion = "4.0.1";
    private const string DropsOne = """{"identity":{"writes":2,"reads":{"min":2,"max":2}},"settings":{"writes":1,"reads":{"min":1,"max":1}}}""";
    private const string Backup = $"{BackupCommands.DefaultFolder}/hvo-roof-roofpi-20261001T090000Z-before-{NewVersion}.tar.gz";

    [TestMethod]
    [DataRow("command", "state")]
    [DataRow("session", "state")]
    [DataRow("wizard", "state")]
    [DataRow("command", "backup")]
    [DataRow("session", "backup")]
    [DataRow("wizard", "backup")]
    [DataRow("command", "sync")]
    [DataRow("session", "sync")]
    [DataRow("wizard", "sync")]
    public async Task RefusedRecovery_RunsNoApprovedPackageMutation(string entry, string refusal)
    {
        using var pi = await UpgradePiAsync();
        if (refusal == "state") pi.WithRelease(NewVersion, latest: true, stateContract: DropsOne);
        if (refusal == "backup")
        {
            // The step's initial check accepts the stores, but writing the archive refuses this enclosing folder.
            File.SetUnixFileMode(pi.OnDisk(ControllerLayout.System.Configuration), Modes.Folder | UnixFileMode.GroupWrite);
        }
        if (refusal == "sync")
        {
            pi.Folder(BackupCommands.DefaultFolder);
            File.SetUnixFileMode(pi.OnDisk("/var/backups"), Modes.Folder);
            File.SetUnixFileMode(pi.OnDisk(BackupCommands.DefaultFolder), Modes.PrivateFolder);
            pi.FileSyncFailure = "Input/output error";
        }
        var before = pi.Snapshot(InstallPaths.SystemLog);
        var deploys = pi.Deploys.Count;

        var result = await ApplyAsync(pi, entry);

        result.Succeeded.Should().BeFalse(result.Detail);
        result.Detail.Should().Contain(refusal switch
        {
            "state" => "schema 1",
            "backup" => "someone other than root can change",
            _ => "Could not sync"
        });
        pi.Ran.Should().NotContain(command => command.Program == "apt-get", "even apt-get update must wait for recovery preparation");
        pi.AptInstalls.Should().BeEmpty();
        pi.Packages.Should().BeEmpty();
        pi.Deploys.Should().HaveCount(deploys);
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before, "a refused backup or state contract leaves the installation as it was");
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("command")]
    [DataRow("session")]
    [DataRow("wizard")]
    [DataRow("handedover")]
    public async Task ApprovedPackages_WaitForTheDurableBackup_AndThePlanKeepsThatOneBackup(string entry)
    {
        using var pi = await UpgradePiAsync();
        var beforePackages = new List<bool>();
        pi.BeforeCommand = command =>
        {
            if (command.Program == "apt-get")
            {
                beforePackages.Add(pi.Exists(Backup) && pi.FileSyncs.Contains(Backup)
                    && pi.DirectorySyncs.Contains(BackupCommands.DefaultFolder));
            }
        };

        var result = await ApplyAsync(pi, entry);

        result.Succeeded.Should().BeTrue(result.Detail);
        beforePackages.Should().Equal([true, true], "the archive and its folder are synced before apt-get update and install");
        pi.AptInstalls.Should().ContainSingle();
        Directory.EnumerateFiles(pi.OnDisk(BackupCommands.DefaultFolder)).Select(Path.GetFileName).Should().Equal(Path.GetFileName(Backup));
        var backup = await BackupCommands.ReadAsync(pi.Machine, Backup, CancellationToken.None);
        backup.Manifest.RecordVersion.Should().Be("4.0.0");
        backup.Entries.Should().Contain(entry => entry.Path == ControllerLayout.System.IdentityFile);
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Should().Match<InstallRecord>(record =>
            record.Version == NewVersion && record.PreviousVersion == "4.0.0" && record.UpgradeBackup == Backup && record.UpgradingFrom == null);
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnApprovedUpgradePlan_ChangesNeitherPackagesNorBackups()
    {
        using var pi = await UpgradePiAsync();
        var answers = pi.WriteAnswers(InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).ToAnswers());
        var before = pi.Snapshot();

        var result = await pi.RunAsync("--plan", "--setup-prerequisites", "--answers", answers);

        result.ExitCode.Should().Be(0, result.ToString());
        result.Output.Should().Contain(Backup).And.Contain("explicitly approved");
        pi.Ran.Should().NotContain(command => command.Program == "apt-get");
        pi.Snapshot().Should().Equal(before);
    }

    [TestMethod]
    public async Task ARoutineUpgrade_DoesNotApproveMissingPackages()
    {
        using var pi = await UpgradePiAsync();
        var before = pi.Snapshot(InstallPaths.SystemLog);

        var result = await pi.RunAsync("upgrade");

        result.ExitCode.Should().Be((int)InstallerExitCode.Refused, result.ToString());
        result.Error.Should().Contain("--setup-prerequisites");
        pi.Ran.Should().NotContain(command => command.Program == "apt-get");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
    }

    private static async Task<FakeMachine> UpgradePiAsync()
    {
        var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Write(ControllerLayout.System.IdentityFile, """{"schemaVersion":1,"users":[],"apiKeys":[]}""");
        pi.WithRelease(NewVersion, latest: true);
        pi.InstallerVersion = NewVersion;
        pi.Packages!.Clear();
        pi.AptInstalls.Clear();
        pi.Ran.Clear();
        pi.WriteAnswers(InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).ToAnswers());
        return pi;
    }

    [TestMethod]
    public async Task PreparationAndApply_AcrossClockSeconds_KeepTheReviewedBackup()
    {
        using var pi = await UpgradePiAsync();
        var clock = new MovingClock(FakeMachine.Today);
        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, clock);
        session.SetupPrerequisites = true;
        var plan = await session.CheckAsync();
        clock.Now += TimeSpan.FromMinutes(1);

        await Prerequisites.PrepareAsync(session);
        clock.Now += TimeSpan.FromMinutes(1);
        await session.ApplyAsync(plan);

        Directory.EnumerateFiles(pi.OnDisk(BackupCommands.DefaultFolder)).Select(Path.GetFileName).Should().Equal(Path.GetFileName(Backup));
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).UpgradeBackup.Should().Be(Backup);
        pi.AptInstalls.Should().ContainSingle();
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AHandedOverBackup_MustVerifyAndSyncBeforePackages(bool syncFails)
    {
        using var pi = await UpgradePiAsync();
        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, pi.RunsAt);
        session.SetupPrerequisites = true;
        var backup = session.UpgradeBackup()!;
        await backup.ApplyAsync(session.Context, await backup.CheckAsync(session.Context, CancellationToken.None), CancellationToken.None);
        var archive = await BackupCommands.ReadAsync(pi.Machine, Backup, CancellationToken.None);
        session.HandedOverBackup = (Backup, syncFails ? archive.Sha256 : new string('0', 64));
        if (syncFails) pi.FileSyncFailure = "the handed-over archive cannot be synced";

        var prepare = () => Prerequisites.PrepareAsync(session);

        var failure = await prepare.Should().ThrowAsync<InstallerException>();
        failure.Which.Message.Should().Contain(syncFails ? "power loss" : "SHA-256 differs");
        pi.Ran.Should().NotContain(command => command.Program == "apt-get");
        pi.AptInstalls.Should().BeEmpty();
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Version.Should().Be("4.0.0");
    }

    private sealed class MovingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record EntryResult(bool Succeeded, string Detail);

    private static async Task<EntryResult> ApplyAsync(FakeMachine pi, string entry)
    {
        if (entry == "command")
        {
            var result = await pi.RunAsync("--setup-prerequisites", "--answers", "/root/answers.json");
            return new EntryResult(result.ExitCode == 0, result.ToString());
        }
        if (entry is "session" or "handedover")
        {
            var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, pi.RunsAt);
            session.SetupPrerequisites = true;
            if (entry == "handedover")
            {
                var backup = session.UpgradeBackup()!;
                await backup.ApplyAsync(session.Context, await backup.CheckAsync(session.Context, CancellationToken.None), CancellationToken.None);
                session.HandedOverBackup = (Backup, (await BackupCommands.ReadAsync(pi.Machine, Backup, CancellationToken.None)).Sha256);
            }
            var plan = await session.CheckAsync();
            try
            {
                await session.ApplyAsync(plan);
                return new EntryResult(true, string.Empty);
            }
            catch (InstallerException error)
            {
                return new EntryResult(false, error.Message);
            }
        }

        using var wizard = await WizardDriver.StartAsync(pi);
        wizard.Session.Version = NewVersion;
        wizard.NextTo(5);
        var review = (ReviewPage)wizard.Page;
        var setup = review.SubViews.OfType<Button>().Single(button => button.Text == "Approve kiosk library setup");
        setup.SetFocus();
        wizard.Press(Key.Enter);
        wizard.WaitIdle("approved prerequisite preparation");
        if (wizard.Wizard.Message == "Prerequisite setup stopped.")
        {
            var detail = string.Join(Environment.NewLine, review.Describe());
            return new EntryResult(false, detail);
        }
        if (review.Plan!.IsBlocked)
        {
            return new EntryResult(false, string.Join(Environment.NewLine, review.Describe()));
        }
        wizard.Wizard.NextButton.SetFocus();
        wizard.NextTo(7);
        wizard.Page.Should().BeOfType<DonePage>();
        return new EntryResult(wizard.Wizard.Result == InstallerExitCode.Success,
            string.Join(Environment.NewLine, wizard.Page.Describe()) + wizard.Wizard.Failure);
    }
}
