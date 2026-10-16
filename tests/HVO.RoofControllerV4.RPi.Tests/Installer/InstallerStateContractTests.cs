using System.Runtime.Versioning;
using System.Text.Json.Nodes;
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
