using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The state contracts (#130): each release declares what it writes and reads of the controller's stores (release.json's
/// <c>stateContracts</c> and the image's label); <c>upgrade</c> and <c>rollback</c> refuse (3, nothing changed) a release
/// that cannot read the stores here; an upgrade backs up the small stores before it changes anything; a rollback across a
/// breaking change goes ahead only with <c>--restore-backup</c>, which puts back from that backup the stores the release
/// before cannot read, after the controller's verified Stop.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerStateContractTests
{
    private const string NewVersion = "4.0.1";

    // Identity schema 1 only, as 4.0.x: what a release.json without stateContracts gives too.
    private const string SchemaOne = """{"identity":{"writes":1,"reads":{"min":1,"max":1}},"settings":{"writes":1,"reads":{"min":1,"max":1}}}""";

    // Moves the identity store to schema 2, which a reader of schema 1 alone cannot read: a breaking change from 4.0.0.
    private const string MovesToTwo = """{"identity":{"writes":2,"reads":{"min":1,"max":2}},"settings":{"writes":1,"reads":{"min":1,"max":1}}}""";

    // Reads identity schema 2 alone: it cannot read a store of schema 1.
    private const string DropsOne = """{"identity":{"writes":2,"reads":{"min":2,"max":2}},"settings":{"writes":1,"reads":{"min":1,"max":1}}}""";

    private const string Backup = $"{BackupCommands.DefaultFolder}/hvo-roof-roofpi-20261001T090000Z-before-{NewVersion}.tar.gz";

    private static readonly string IdentityFile = ControllerLayout.System.IdentityFile;

    private const string IdentityBefore = """{"users":[{"name":"before-the-upgrade","role":"RoofAdmin"}],"apiKeys":[]}""";

    private const string IdentityMoved = """{"schemaVersion":2,"users":[{"name":"after-the-upgrade","role":"RoofAdmin"}],"apiKeys":[],"linuxPrincipals":[]}""";

    private const string DockerDown = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?";

    // A folder the backup holds whole, which someone other than root could then change: a backup refuses it.
    private const UnixFileMode GroupWritable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [TestMethod]
    public void AReleaseJson_WithStateContracts_GivesTheControllersContract()
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(stateContract: MovesToTwo, identitySchema: 2), "4.0.0");

        release.StateContract.For(StateContract.Identity).Should().Be(new StoreContract(2, 1, 2));
        release.StateContract.For(StateContract.Settings).Should().Be(new StoreContract(1, 1, 1));
        release.IdentityStoreSchema.Should().Be(2);
    }

    [TestMethod]
    [DataRow(null, 1)]
    [DataRow(2, 2)]
    [DataRow(4, 4)]
    public void AReleaseJson_WithoutStateContracts_GivesTheContractItsIdentitySchemaSays(int? identitySchema, int expected)
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(identitySchema: identitySchema), "4.0.0");

        release.StateContract.For(StateContract.Identity).Should().Be(new StoreContract(expected, 1, expected));
        release.StateContract.For(StateContract.Settings).Should().Be(new StoreContract(1, 1, 1));
    }

    [TestMethod]
    [DataRow("[]", "its stateContracts are not an object naming the controller's")]
    [DataRow("""{"hatEmulator":{}}""", "its stateContracts are not an object naming the controller's")]
    [DataRow("""{"controller":[]}""", "the controller's state contract is not a JSON object")]
    [DataRow("""{"controller":{"settings":{"writes":1,"reads":{"min":1,"max":1}}}}""", "the controller's state contract has no identity store")]
    [DataRow("""{"controller":{"identity":{"writes":1}}}""", "the identity store's state contract is not writes and reads")]
    [DataRow("""{"controller":{"identity":{"writes":1,"reads":{"min":2,"max":2}}}}""", "the identity store's state contract is not writes and reads")]
    [DataRow("""{"controller":{"identity":{"writes":3,"reads":{"min":1,"max":2}}}}""", "the identity store's state contract is not writes and reads")]
    [DataRow("""{"controller":{"identity":{"writes":0,"reads":{"min":0,"max":0}}}}""", "the identity store's state contract is not writes and reads")]
    [DataRow("""{"controller":{"identity":{"writes":"1","reads":{"min":1,"max":1}}}}""", "the identity store's state contract is not writes and reads")]
    [DataRow("""{"controller":{"identity":{"writes":2,"reads":{"min":1,"max":2}}}}""", "identityStoreSchema is 3, but the controller's state contract reads identity schemas up to 2")]
    public void AReleaseJson_WhoseStateContractsAreNotAContract_IsRefused(string contracts, string reason)
    {
        var json = JsonNode.Parse(FakeMachine.ReleaseJson(identitySchema: 3))!;
        json["stateContracts"] = JsonNode.Parse(contracts);

        var parse = () => ReleaseManifest.Parse(json.ToJsonString(), "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage($"*{reason}*");
    }

    [TestMethod]
    public void ABreakingChange_IsAStoreTheTargetMayWriteThatTheReleaseInstalledDoesNotRead()
    {
        var installed = StateContract.Legacy(2);

        StateContract.Legacy(3).BreakingFrom(installed).Should().ContainSingle()
            .Which.Should().Be((StateContract.Identity, new StoreContract(3, 1, 3), new StoreContract(2, 1, 2)));
        StateContract.Legacy(2).BreakingFrom(installed).Should().BeEmpty();

        // Added first: read by one release before another writes it, so a rollback to that one stays a swap.
        var addsThree = new StateContract(new Dictionary<string, StoreContract> { [StateContract.Identity] = new(2, 1, 3) });
        addsThree.BreakingFrom(installed).Should().BeEmpty();
        StateContract.Legacy(3).BreakingFrom(addsThree).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(SchemaOne, null, 1)]
    [DataRow(null, "2", 2)]
    [DataRow(null, null, 1)]
    public void AControllerImage_DeclaresItsContractInItsLabels(string? contracts, string? identitySchema, int identityMax)
        => StateContract.FromLabels(contracts, identitySchema)!.For(StateContract.Identity)!.ReadsMax.Should().Be(identityMax);

    [TestMethod]
    [DataRow("not json", null)]
    [DataRow("""{"identity":{"writes":1}}""", null)]
    [DataRow(null, "two")]
    [DataRow(null, "0")]
    public void AControllerImage_WhoseLabelsCannotBeRead_DeclaresNothing(string? contracts, string? identitySchema)
        => StateContract.FromLabels(contracts, identitySchema).Should().BeNull();

    [TestMethod]
    public void TheStoresSchemas_AreReadFromTheirFiles_AndAStoreNeverSavedIsReadByEveryRelease()
    {
        using var pi = new FakeMachine();
        var layout = ControllerLayout.System;
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(stateContract: DropsOne, identitySchema: 2), "4.0.0");

        StateContractGuard.Read(pi.Machine, layout).Should().Equal(new StoreState(StateContract.Identity, null), new StoreState(StateContract.Settings, null));
        StateContractGuard.Incompatible(StateContractGuard.Read(pi.Machine, layout), release).Should().BeEmpty();

        // As the controller reads them: comments and trailing commas are allowed in the settings files.
        pi.Write(layout.SettingsFile, "// changed through the API\n{ \"RoofControllerUi\": { \"DefaultCamera\": \"north\", }, }");
        pi.Write(IdentityFile, IdentityBefore);
        StateContractGuard.Read(pi.Machine, layout).Should().Equal(new StoreState(StateContract.Identity, 1), new StoreState(StateContract.Settings, 1));
        StateContractGuard.Incompatible(StateContractGuard.Read(pi.Machine, layout), release).Should().ContainSingle()
            .Which.Should().Be("the identity store here is schema 1, and release 4.0.0 reads identity schema 2");

        pi.Write(StateContractGuard.SecretsFile(layout), "[]");
        StateContractGuard.Read(pi.Machine, layout)[1].Problem.Should().Be("/var/lib/hvo-roof/settings-secrets/secrets.json is not a settings file: it is not a JSON object");
    }

    [TestMethod]
    public async Task ACompatibleUpgrade_BacksUpTheStoresBeforeItChangesAnything_AndTheRecordNamesTheBackup()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        var backupAtDeploy = false;
        pi.DuringDeploy = () => backupAtDeploy = pi.Exists(Backup);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"The stores here: the identity store, schema 1; the settings files, none saved. Release {NewVersion} reads them.", Exactly.Once())
            .And.NotContain("breaking state change")
            .And.Contain(Backup);
        backupAtDeploy.Should().BeTrue("the stores are backed up before the deploy script replaces the controller");
        pi.Launches.Should().ContainSingle().Which.Environment.Should().Contain(LifecycleCommands.UpgradeBackupVariable, Backup);
        Directory.EnumerateFiles(pi.OnDisk(BackupCommands.DefaultFolder)).Should().ContainSingle("the installer handed over to keeps the backup taken before it, and takes none");
        pi.Mode(Backup).Should().Be(Modes.PrivateFile);
        pi.Mode(BackupCommands.DefaultFolder).Should().Be(Modes.PrivateFolder);
        var backup = await BackupCommands.ReadAsync(pi.Machine, Backup, CancellationToken.None);
        backup.Manifest.RecordVersion.Should().Be("4.0.0");
        backup.Entries.Should().Contain(entry => entry.Path == IdentityFile)
            .And.NotContain(entry => entry.Path.StartsWith("/var/log/", StringComparison.Ordinal), "logs are not backed up");
        Record(pi).Should().Match<InstallRecord>(record => record.Version == NewVersion && record.PreviousVersion == "4.0.0" && record.UpgradeBackup == Backup);
        InstallerLifecycleTests.ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUpgradePlan_ShowsTheBackup_AndTakesNone()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        pi.InstallerVersion = NewVersion;
        var files = pi.Snapshot();

        var run = await pi.RunAsync("upgrade", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(Backup).And.Contain("backed up before the upgrade (no logs)");
        pi.Snapshot().Should().Equal(files);
    }

    [TestMethod]
    public async Task AnUpgradeToAReleaseThatCannotReadTheStores_IsRefused_BeforeTheInstallerIsReplaced()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: DropsOne);
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"Release {NewVersion} cannot read the data here: the identity store here is schema 1, and release {NewVersion} reads identity schema 2.")
            .And.Contain("Nothing was changed.");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Launches.Should().BeEmpty("the new release's installer is not put in place or run");
        pi.Deploys.Should().ContainSingle("only the install's");
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUpgradeByTheReleasesOwnInstaller_ThatCannotReadTheStores_IsRefused_AndTakesNoBackup()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: DropsOne);
        pi.InstallerVersion = NewVersion;
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("upgrade");
        var plan = await pi.RunAsync("upgrade", "--plan");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("cannot read the data here");
        plan.ExitCode.Should().Be((int)InstallerExitCode.Refused, plan.ToString());
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task AnUpgradeThatMakesABreakingChange_SaysSo_AndBacksUpFirst()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: MovesToTwo);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(
            $"Release {NewVersion} makes a breaking state change: it may move the identity store to schema 2, which 4.0.0 does not read (it reads schema 1). "
            + "The upgrade backs up the stores first. Once a store has moved, a rollback to 4.0.0 puts it back from that backup (sudo hvo-roof-install rollback --restore-backup), "
            + "and what changed in it after the upgrade is lost.", Exactly.Once());
        pi.Exists(Backup).Should().BeTrue();
        Record(pi).UpgradeBackup.Should().Be(Backup);
    }

    [TestMethod]
    public async Task AnUpgradeRunAgain_AfterTheControllerWasReplaced_KeepsTheBackupFromBeforeIt()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);

        // As if the first run stopped after the deploy script replaced the controller, before the record: the new
        // controller has moved its store on since.
        var record = JsonNode.Parse(pi.Read(InstallPaths.SystemRecord))!;
        record["version"] = "4.0.0";
        record.AsObject().Remove("previousVersion");
        record.AsObject().Remove("upgradeBackup");
        pi.Write(InstallPaths.SystemRecord, record.ToJsonString());
        pi.Write(IdentityFile, IdentityMoved);
        pi.RunsAt = new FixedClock(FakeMachine.Today.AddMinutes(5));

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"taken before an earlier run of this upgrade: {Backup}");
        Directory.EnumerateFiles(pi.OnDisk(BackupCommands.DefaultFolder)).Select(Path.GetFileName).Should().Equal(Path.GetFileName(Backup));
        Record(pi).UpgradeBackup.Should().Be(Backup);
    }

    [TestMethod]
    public async Task ARollback_ThatTheReleaseBeforeCannotRead_IsRefused_AndNamesTheBackup()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        var files = pi.Snapshot(InstallPaths.SystemLog);

        foreach (var arguments in new[] { new[] { "rollback" }, ["rollback", "--plan"] })
        {
            var run = await pi.RunAsync(arguments);

            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
            run.Error.Should().Contain("Rollback is refused before replacing the controller: release 4.0.0 cannot read the data here: the identity store here is schema 2, and release 4.0.0 reads identity schema 1.")
                .And.Contain($"The upgrade to {NewVersion} backed the stores up first, to {Backup}.")
                .And.Contain("losing every change made to them since the upgrade")
                .And.Contain("sudo hvo-roof-install rollback --restore-backup.")
                .And.Contain("Nothing was changed.");
        }

        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.DeployOptions.Should().NotContain("--rollback").And.NotContain("--stop");
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be(NewVersion);
    }

    [TestMethod]
    public async Task ARollback_WithNoBackupFromBeforeTheUpgrade_SaysHowToGoOn()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        File.Delete(pi.OnDisk(Backup));

        var run = await pi.RunAsync("rollback");
        var restore = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"The backup the upgrade to {NewVersion} took, {Backup}, is not there.").And.Contain("rollback --restore-backup FILE");
        restore.ExitCode.Should().Be((int)InstallerExitCode.Refused, restore.ToString());
        restore.Error.Should().Contain($"{Backup} is not there.");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_StopsTheController_PutsBackTheStoreTheReleaseBeforeCannotRead_ThenSwaps()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        pi.Write(ControllerLayout.System.SettingsFile, """{"RoofControllerUi":{"DefaultCamera":"set after the upgrade"}}""");
        var settings = pi.Read(ControllerLayout.System.SettingsFile);
        var restoredBeforeTheSwap = false;
        pi.DuringDeploy = () => restoredBeforeTheSwap = pi.DeployOptions.Count > 0 && pi.Read(IdentityFile) == IdentityBefore;

        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Release 4.0.0 cannot read the data here: the identity store here is schema 2")
            .And.Contain($"the identity store put back from {Backup}, the backup taken before the upgrade to {NewVersion}. Every change made to it since then is lost")
            .And.Contain("access removed since comes back")
            .And.Contain("Done rolling back.");
        pi.DeployOptions.TakeLast(2).Should().Equal("--stop", "--rollback");
        restoredBeforeTheSwap.Should().BeTrue("the store is put back with the controller stopped, before the deploy script's --rollback");
        pi.Read(IdentityFile).Should().Be(IdentityBefore);
        var kept = $"{IdentityFile}.rolled-back-from-{NewVersion}-20261001T090000Z";
        pi.Read(kept).Should().Be(IdentityMoved);
        pi.Mode(kept).Should().Be(Modes.PrivateFile);
        pi.Read(ControllerLayout.System.SettingsFile).Should().Be(settings, "release 4.0.0 reads the settings files as they are");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.State == "running");
        Record(pi).Should().Match<InstallRecord>(record => record.Version == "4.0.0" && record.RolledBackFrom == NewVersion && record.PreviousVersion == null && record.UpgradeBackup == null);
        pi.Exists(Backup).Should().BeTrue("the backup is kept");
        InstallerLifecycleTests.ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_AsksFirst_AndWithNoOneToAsk_NeedsYes()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var unattended = await pi.RunAsync("rollback", "--restore-backup");
        pi.Interactive = true;
        pi.Replies = _ => false;
        var declined = await pi.RunAsync("rollback", "--restore-backup");
        var plan = await pi.RunAsync("rollback", "--restore-backup", "--plan");

        unattended.ExitCode.Should().Be((int)InstallerExitCode.Usage, unattended.ToString());
        unattended.Error.Should().Contain("give --yes");
        declined.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, declined.ToString());
        declined.Error.Should().Contain("Nothing was changed.");
        pi.Questions.Should().ContainSingle().Which.Should().Be($"Put back the identity store from {Backup}, losing the changes since the upgrade?");
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain(IdentityFile).And.Contain("--stop").And.Contain(ControllerRollbackStep.PreviousContainer);
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.DeployOptions.Should().NotContain("--stop").And.NotContain("--rollback");
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_OfABackupNotOfTheReleaseBefore_IsRefused()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        pi.Folder("/root");
        (await pi.RunAsync("backup", "--output", "/root/now.tar.gz")).ExitCode.Should().Be(0);

        var run = await pi.RunAsync("rollback", "--restore-backup", "/root/now.tar.gz", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"/root/now.tar.gz is a backup of {NewVersion}, not of 4.0.0, the release the rollback goes back to");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
        pi.DeployOptions.Should().NotContain("--stop");
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_ThatTheReleaseBeforeReads_IsASwap_AndPutsNothingBack()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, """{"users":[{"name":"after-the-upgrade","role":"RoofAdmin"}],"apiKeys":[]}""");

        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Release 4.0.0 reads the stores here as they are: nothing is put back from a backup, and the rollback is a swap.");
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.DeployOptions.Should().NotContain("--stop");
        pi.Read(IdentityFile).Should().Contain("after-the-upgrade");
        Record(pi).UpgradeBackup.Should().BeNull();
    }

    [TestMethod]
    public async Task YesWithoutRestoreBackup_IsAUsageError()
    {
        using var pi = await UpgradedPiAsync(SchemaOne);

        var run = await pi.RunAsync("rollback", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("--yes goes with --restore-backup.");
    }

    // #136 F1: the stores are put back only once Docker says the controller is stopped or not there.

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_WhenDockerStopsAnsweringOnceItIsPlanned_StopsNothing_AndPutsNothingBack()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        var record = pi.Read(InstallPaths.SystemRecord);
        var deploys = pi.DeployOptions.Count;
        var planned = false;

        // The plan is checked and shown with the controller running; then Docker stops answering, and the controller may
        // well run on.
        pi.OnOutputLine = line =>
        {
            if (Regex.IsMatch(line, @"^\d+ to create, \d+ to change, "))
            {
                planned = true;
                pi.DockerError = DockerDown;
            }
        };

        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        planned.Should().BeTrue("the plan was checked and shown first");
        pi.Read(IdentityFile).Should().Be(IdentityMoved, "nothing is put back while the controller may be running");
        Directory.EnumerateFiles(pi.OnDisk(ControllerLayout.System.Identity), "*.rolled-back-from-*").Should().BeEmpty();
        pi.DeployOptions.Skip(deploys).Should().BeEmpty("neither --stop nor --rollback ran");
        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"Docker could not say whether {ControllerRollbackStep.SwapContainer} is there or running");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == NewVersion && running.State == "running");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
    }

    [TestMethod]
    public async Task AStore_IsPutBack_OnlyWhenDockerSaysTheControllerIsStopped()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        var context = (await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, pi.RunsAt)).Context;
        var step = new StateRestoreStep(IdentityFile, "the identity store", Encoding.UTF8.GetBytes(IdentityBefore), Modes.PrivateFile, null, NewVersion);
        var check = await step.CheckAsync(context, CancellationToken.None);
        var apply = () => step.ApplyAsync(context, check, CancellationToken.None);

        check.Change.Should().Be(StepChange.Change);
        await apply.Should().ThrowAsync<InstallerException>().WithMessage($"{MachineSurveyor.ControllerContainer} is running: * put back only with the controller stopped*");
        pi.DockerError = DockerDown;
        await apply.Should().ThrowAsync<InstallerException>()
            .WithMessage($"{IdentityFile} is put back only with the controller stopped, and Docker could not say whether {MachineSurveyor.ControllerContainer} is there or running*Nothing was put back.");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
        Directory.EnumerateFiles(pi.OnDisk(ControllerLayout.System.Identity), "*.rolled-back-from-*").Should().BeEmpty();

        // Docker says it is stopped: it is put back.
        pi.DockerError = null;
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };
        await apply();
        pi.Read(IdentityFile).Should().Be(IdentityBefore);
    }

    [TestMethod]
    public async Task TheControllersStop_IsBlocked_WhenDockerCannotSayWhetherItRuns()
    {
        using var pi = await UpgradedPiAsync(SchemaOne);
        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, pi.RunsAt);
        var stop = PlanBuilder.BuildUninstall(pi.Machine, session.Survey, session.Record!, purge: false).Steps.OfType<ControllerStopStep>().Single();

        pi.DockerError = DockerDown;
        var check = await stop.CheckAsync(session.Context, CancellationToken.None);

        check.Change.Should().Be(StepChange.Blocked, "a controller Docker cannot answer for may be running: it is not taken for none");
        check.Detail.Should().StartWith("Docker could not say whether").And.Contain(DockerDown.TrimEnd('?'));

        // Docker saying there is none is: nothing to stop.
        pi.DockerError = null;
        pi.Containers.Remove(MachineSurveyor.ControllerContainer);
        (await stop.CheckAsync(session.Context, CancellationToken.None)).Should().Be(StepCheck.Unchanged("not there"));
    }

    // #136 F5: a store is put back only once Docker says both controller containers are stopped or not there (the one
    // kept for a rollback too, which --stop leaves alone), and that no swap was left halfway, whatever the plan found.

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_WhenTheKeptControllerStartsOnceItIsPlanned_PutsNothingBack()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);

        // Both controllers are stopped when the plan is checked, so it stops neither; then someone starts the one kept.
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };
        pi.Containers[ControllerRollbackStep.PreviousContainer].State.Should().Be("exited");
        var deploys = pi.DeployOptions.Count;
        var before = pi.Snapshot(InstallPaths.SystemLog);
        var planned = false;
        pi.OnOutputLine = line =>
        {
            if (Regex.IsMatch(line, @"^\d+ to create, \d+ to change, "))
            {
                planned = true;
                pi.Containers[ControllerRollbackStep.PreviousContainer] = pi.Containers[ControllerRollbackStep.PreviousContainer] with { State = "running" };
            }
        };

        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        planned.Should().BeTrue("the plan was checked and shown first");
        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain(
            $"{ControllerRollbackStep.PreviousContainer} is running: {IdentityFile} is put back only with the controller stopped, and the one kept for a rollback too. Nothing was put back");
        pi.DeployOptions.Skip(deploys).Should().BeEmpty("the controller was stopped already, so neither --stop nor --rollback ran");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
        Directory.EnumerateFiles(pi.OnDisk(ControllerLayout.System.Identity), "*.rolled-back-from-*").Should().BeEmpty();
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before, "no store, no file kept beside one, and not the record");
        pi.Containers[MachineSurveyor.ControllerContainer].State.Should().Be("exited");
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_WhenTheKeptControllerStartsAfterTheStop_PutsNothingBack()
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        var deploys = pi.DeployOptions.Count;
        var before = pi.Snapshot(InstallPaths.SystemLog);
        var started = false;
        pi.OnOutputLine = line =>
        {
            // The deploy script's --stop has stopped the controller: then someone starts the one kept for a rollback.
            if (!started && line.EndsWith($" {MachineSurveyor.ControllerContainer}: done.", StringComparison.Ordinal) && pi.DeployOptions.Skip(deploys).SequenceEqual(["--stop"]))
            {
                started = true;
                pi.Containers[ControllerRollbackStep.PreviousContainer] = pi.Containers[ControllerRollbackStep.PreviousContainer] with { State = "running" };
            }
        };

        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        started.Should().BeTrue("the controller was stopped first");
        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"{ControllerRollbackStep.PreviousContainer} is running: {IdentityFile} is put back only with the controller stopped");
        pi.DeployOptions.Skip(deploys).Should().Equal(["--stop"], "the rollback stops before --rollback");
        pi.Containers[MachineSurveyor.ControllerContainer].State.Should().Be("exited", "the controller stays stopped");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
        Directory.EnumerateFiles(pi.OnDisk(ControllerLayout.System.Identity), "*.rolled-back-from-*").Should().BeEmpty();
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before, "no store, no file kept beside one, and not the record");
    }

    [TestMethod]
    [DataRow(MachineSurveyor.ControllerContainer, "paused")]
    [DataRow(MachineSurveyor.ControllerContainer, "restarting")]
    [DataRow(ControllerRollbackStep.PreviousContainer, "running")]
    [DataRow(ControllerRollbackStep.PreviousContainer, "paused")]
    [DataRow(ControllerRollbackStep.PreviousContainer, "restarting")]
    [DataRow(ControllerRollbackStep.PreviousContainer, "removing")]
    [DataRow(ControllerRollbackStep.PreviousContainer, "unknown")]
    [DataRow(ControllerRollbackStep.SwapContainer, "exited")]
    public async Task AStore_IsNotPutBack_WhileEitherControllerMayRun_OrASwapWasLeftHalfway(string name, string state)
    {
        using var pi = await UpgradedPiAsync(MovesToTwo);
        pi.Write(IdentityFile, IdentityMoved);
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };
        var context = (await InstallerSession.StartAsync(pi.Machine, InstallLog.None, NewVersion, pi.RunsAt)).Context;
        var step = new StateRestoreStep(IdentityFile, "the identity store", Encoding.UTF8.GetBytes(IdentityBefore), Modes.PrivateFile, null, NewVersion);
        var check = await step.CheckAsync(context, CancellationToken.None);
        pi.Containers[name] = (pi.Containers.GetValueOrDefault(name) ?? pi.Containers[ControllerRollbackStep.PreviousContainer]) with { State = state };

        var apply = () => step.ApplyAsync(context, check, CancellationToken.None);

        (await apply.Should().ThrowAsync<StepBlockedException>()).Which.Message.Should().Contain(
            name == ControllerRollbackStep.SwapContainer ? $"{ControllerRollbackStep.SwapContainer} is there" : $"{name} is {state}: {IdentityFile} is put back only with the controller stopped");
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
        Directory.EnumerateFiles(pi.OnDisk(ControllerLayout.System.Identity), "*.rolled-back-from-*").Should().BeEmpty();
    }

    // #136 F2: a store the backup cannot hold refuses the upgrade; a rollback removes a store only when the backup says
    // it was not there.

    [TestMethod]
    public async Task AnUpgrade_WithAStoreTheBackupCannotHold_IsRefused_BeforeAnythingChanges()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        File.Delete(pi.OnDisk(IdentityFile));
        pi.Write(IdentityFile + ".real", IdentityBefore);
        File.CreateSymbolicLink(pi.OnDisk(IdentityFile), "identity.json.real");
        var before = State(pi);

        foreach (var installer in new[] { "4.0.0", NewVersion })
        {
            pi.InstallerVersion = installer;
            var run = await pi.RunAsync("upgrade");

            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, $"{installer}: {run}");
            (run.Output + run.Error).Should().Contain($"{IdentityFile} is there, but it is a symbolic link, and a backup holds only files and folders");
            Unchanged(pi, before);
        }

        pi.Launches.Should().BeEmpty("the installer is not replaced");
        pi.Exists(BackupCommands.DefaultFolder).Should().BeFalse("no backup is written");
    }

    [TestMethod]
    public async Task ARollbackWithRestoreBackup_NeverRemovesAStoreTheBackupLeftOut()
    {
        // A backup made while the identity store was a link: it leaves the link out, and does not say the store was not there.
        using var pi = await InstalledPiAsync();
        File.Delete(pi.OnDisk(IdentityFile));
        pi.Write(IdentityFile + ".real", IdentityBefore);
        File.CreateSymbolicLink(pi.OnDisk(IdentityFile), "identity.json.real");
        pi.Folder("/root");
        (await pi.RunAsync("backup", "--output", "/root/before.tar.gz")).ExitCode.Should().Be(0);
        var leftOut = await BackupCommands.ReadAsync(pi.Machine, "/root/before.tar.gz", CancellationToken.None);
        leftOut.Entries.Should().NotContain(entry => entry.Path == IdentityFile);
        leftOut.Manifest.Absent.Should().NotContain(IdentityFile);
        File.Delete(pi.OnDisk(IdentityFile));
        pi.Write(IdentityFile, IdentityBefore);
        pi.WithRelease(NewVersion, latest: true, stateContract: MovesToTwo);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        pi.Write(IdentityFile, IdentityMoved);
        var before = State(pi);

        var run = await pi.RunAsync("rollback", "--restore-backup", "/root/before.tar.gz", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"/root/before.tar.gz does not hold {IdentityFile}, and does not say it was not there when the backup was made: it cannot put back the identity store. Nothing was changed.");
        Unchanged(pi, before);
        pi.Read(IdentityFile).Should().Be(IdentityMoved);
    }

    [TestMethod]
    public async Task AnUpgradesBackup_SaysWhichStoresWereNotThere_AndARollbackMovesOneSavedSinceAside()
    {
        using var pi = await InstalledPiAsync();
        File.Delete(pi.OnDisk(IdentityFile));
        pi.WithRelease(NewVersion, latest: true, stateContract: MovesToTwo);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        (await BackupCommands.ReadAsync(pi.Machine, Backup, CancellationToken.None)).Manifest.Absent.Should().Contain(IdentityFile);

        // The new release saved its identity store, at a schema 4.0.0 does not read.
        pi.Write(IdentityFile, IdentityMoved);
        var run = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Exists(IdentityFile).Should().BeFalse("4.0.0 never saved it");
        pi.Read($"{IdentityFile}.rolled-back-from-{NewVersion}-20261001T090000Z").Should().Be(IdentityMoved);
        pi.Containers[MachineSurveyor.ControllerContainer].Version.Should().Be("4.0.0");
    }

    // #136 F3: a store that is a folder, or that root cannot read, refuses an upgrade and a rollback; only a plan without
    // sudo leaves a file it may not read to the run as root.

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnUpgrade_WhoseStoreIsAFolder_OrUnreadableAsRoot_IsRefused_AndChangesNothing(bool folder)
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        string problem;
        if (folder)
        {
            File.Delete(pi.OnDisk(IdentityFile));
            Directory.CreateDirectory(pi.OnDisk(IdentityFile));
            problem = $"{IdentityFile} is a folder, not a file: it is not a store the controller reads";
        }
        else
        {
            File.SetUnixFileMode(pi.OnDisk(IdentityFile), UnixFileMode.None);
            problem = $"{IdentityFile} cannot be read, even as root (permission denied)";
        }

        try
        {
            var before = State(pi, folder ? [] : [IdentityFile]);
            foreach (var installer in new[] { "4.0.0", NewVersion })
            {
                pi.InstallerVersion = installer;
                var run = await pi.RunAsync("upgrade");

                run.ExitCode.Should().Be((int)InstallerExitCode.Refused, $"{installer}: {run}");
                run.Error.Should().Contain($"Release {NewVersion} cannot read the data here: {problem}.").And.Contain("Nothing was changed.");
                Unchanged(pi, before, folder ? [] : [IdentityFile]);
            }

            pi.Launches.Should().BeEmpty("the installer is not replaced");
            if (!folder)
            {
                // A plan without sudo cannot tell, and leaves it to the run as root.
                pi.AsPerson();
                var plan = await pi.RunAsync("upgrade", "--plan");
                plan.Output.Should().Contain("the identity store, which only root reads");
            }
        }
        finally
        {
            if (!folder)
            {
                File.SetUnixFileMode(pi.OnDisk(IdentityFile), Modes.PrivateFile);
            }
        }
    }

    [TestMethod]
    public async Task ARollback_WhoseStoreIsAFolder_IsRefused_AndChangesNothing_EvenWithRestoreBackup()
    {
        using var pi = await UpgradedPiAsync(SchemaOne);
        File.Delete(pi.OnDisk(IdentityFile));
        Directory.CreateDirectory(pi.OnDisk(IdentityFile));
        var before = State(pi);

        var run = await pi.RunAsync("rollback");
        var restore = await pi.RunAsync("rollback", "--restore-backup", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"Rollback is refused before replacing the controller: release 4.0.0 cannot read the data here: {IdentityFile} is a folder, not a file");
        restore.ExitCode.Should().Be((int)InstallerExitCode.Refused, restore.ToString());
        restore.Output.Should().Contain($"{IdentityFile} is a folder: a backup puts back only the file");
        Unchanged(pi, before);
    }

    // #136 F4: the backup is taken before the installer replaces itself, and handed over.

    [TestMethod]
    public async Task ABackupThatCannotBeTaken_RefusesTheUpgrade_BeforeTheInstallerReplacesItself()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        File.SetUnixFileMode(pi.OnDisk(ControllerLayout.System.Configuration), GroupWritable);
        var before = State(pi);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"{ControllerLayout.System.Configuration} is root:root's, 0775: someone other than root can change what is in it");
        Unchanged(pi, before);
        pi.Read(InstallPaths.SystemInstaller).Should().Be(Encoding.UTF8.GetString(FakeMachine.InstallerProgram("4.0.0", "linux-arm64")));
        pi.Exists(ProgramFile.Previous(InstallPaths.SystemInstaller)).Should().BeFalse();
        pi.Launches.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheBackupHandedOver_IsChecked_AndOneNotAsTakenRefusesTheUpgrade()
    {
        using var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: SchemaOne);
        var record = pi.Read(InstallPaths.SystemRecord);
        var deploys = pi.Deploys.Count;
        pi.Launch = (path, arguments, environment) =>
        {
            File.AppendAllText(pi.OnDisk(environment[LifecycleCommands.UpgradeBackupVariable]), "changed since");
            return pi.RunInstallerAsync(path, arguments, environment);
        };

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        (run.Output + run.Error).Should().Contain($"{Backup}, the backup the installer before this one took before it handed over, is not the file it took (its SHA-256 differs)");
        pi.Deploys.Should().HaveCount(deploys, "the controller is not replaced");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
    }

    // What a run that refuses must leave as it was: every file (the installer, the record, the stores and the backups),
    // and the containers.
    private static (IReadOnlyDictionary<string, string> Files, Dictionary<string, FakeContainer> Containers) State(FakeMachine pi, params string[] except)
        => (pi.Snapshot([InstallPaths.SystemLog, .. except]), new Dictionary<string, FakeContainer>(pi.Containers));

    private static void Unchanged(FakeMachine pi, (IReadOnlyDictionary<string, string> Files, Dictionary<string, FakeContainer> Containers) before, params string[] except)
    {
        pi.Snapshot([InstallPaths.SystemLog, .. except]).Should().Equal(before.Files);
        pi.Containers.Should().Equal(before.Containers);
    }

    // The controller and the kiosk installed from release 4.0.0, with a person in the identity store (schema 1).
    private static async Task<FakeMachine> InstalledPiAsync()
    {
        var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Write(IdentityFile, IdentityBefore);
        return pi;
    }

    // Upgraded from 4.0.0 to NewVersion, which declares stateContract: the installer in place is NewVersion's.
    private static async Task<FakeMachine> UpgradedPiAsync(string stateContract)
    {
        var pi = await InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true, stateContract: stateContract);
        var run = await pi.RunAsync("upgrade");
        run.ExitCode.Should().Be(0, run.ToString());
        pi.InstallerVersion = NewVersion;
        return pi;
    }

    private static InstallRecord Record(FakeMachine pi) => InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));
}
