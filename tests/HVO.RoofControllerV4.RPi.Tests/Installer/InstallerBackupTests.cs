using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
/// <c>backup</c> and <c>restore</c> (#72): a backup is one archive only root reads, holding the controller's and the
/// kiosk's files (secrets, certificate and CA, people, settings, the device key and the record) and printing only paths
/// and counts; a restore checks the whole archive before writing anything, puts the files back with their modes, and
/// installs what the backup's record says, with a new certificate when the machine's names differ.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerBackupTests
{
    private const string Kept = "/srv/keep/roof.tar.gz";
    private const string DefaultBackup = "/var/backups/hvo-roof/hvo-roof-roofpi-20261001T090000Z.tar.gz";
    private const string Ca = "/etc/hvo-roof/ca.crt";
    private const string Certificate = "/etc/hvo-roof/https/roof-controller.pfx";
    private const string ManagedSecret = "/var/lib/hvo-roof/settings-secrets/Weather__ApiKey";
    private const string CommandLine = "/boot/firmware/cmdline.txt";
    private const string Booted = "console=serial0,115200 console=tty1 root=PARTUUID=0a1b2c3d-02 rootfstype=ext4 fsck.repair=yes rootwait";

    private static readonly InstallAnswers ControllerAndKiosk = new() { Roles = [InstallRole.Controller, InstallRole.Kiosk] };

    [TestMethod]
    public async Task ABackup_HoldsTheControllersAndTheKiosksFiles_InAnArchiveOnlyRootReads_AndPrintsNoSecret()
    {
        using var pi = await InstalledPiAsync();

        var run = await pi.RunAsync("backup");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().MatchRegex($"Backed up [0-9]+ files and [0-9]+ folders to {Regex(DefaultBackup)} \\(0600, root's\\):")
            .And.Contain("  /etc/hvo-roof: ").And.Contain("  /var/lib/hvo-roof: ").And.Contain($"  {KioskSteps.ConfigurationFolder}: 1 file")
            .And.Contain($"  {KioskSteps.SettingsFile}: 1 file")
            .And.Contain($"Keep a copy of {DefaultBackup} off this machine, where only you can read it");
        pi.Mode(DefaultBackup).Should().Be(Modes.PrivateFile);
        pi.Mode(BackupCommands.DefaultFolder).Should().Be(Modes.PrivateFolder);
        pi.Exists(DefaultBackup + ".partial").Should().BeFalse();

        var entries = Entries(pi, DefaultBackup);
        entries[0].Name.Should().Be(BackupCommands.ManifestName, "the manifest comes first");
        var names = entries.Select(entry => "/" + entry.Name).ToList();
        names.Should().Contain([InstallPaths.SystemRecord, Ca, Certificate, KioskSteps.DeviceKeyFile, KioskSteps.SettingsFile, "/etc/hvo-roof/ca", "/var/lib/hvo-roof/identity", ManagedSecret]);
        names.Should().Contain(name => name.StartsWith("/etc/hvo-roof/secrets/", StringComparison.Ordinal));
        names.Should().Contain(name => name.StartsWith("/etc/hvo-roof/ca/", StringComparison.Ordinal), "the CA's key");

        // Each file as it is on the machine, with its mode, and as the manifest says.
        var manifest = Manifest(entries);
        foreach (var entry in entries.Skip(1))
        {
            var path = "/" + entry.Name;
            entry.Mode.Should().Be(pi.Mode(path), path);
            var listed = manifest.Single(item => item["path"]!.GetValue<string>() == path);
            if (entry.Type == TarEntryType.Directory)
            {
                listed["type"]!.GetValue<string>().Should().Be(BackupEntry.Folder);
                continue;
            }

            entry.Content.Should().Equal(File.ReadAllBytes(pi.OnDisk(path)), path);
            listed["sha256"]!.GetValue<string>().Should().Be(Convert.ToHexStringLower(SHA256.HashData(entry.Content!)));
        }

        manifest.Should().HaveCount(entries.Count - 1, "the manifest lists every entry, and only those");
        Encoding.UTF8.GetString(entries[0].Content!).Should().NotContain(pi.Read(KioskSteps.DeviceKeyFile), "the manifest holds hashes, not files");
        InstallerLifecycleTests.ShowsNoSecret(pi, run);
        foreach (var secret in new[] { pi.Read(KioskSteps.DeviceKeyFile), pi.Read(ManagedSecret) })
        {
            run.ToString().Should().NotContain(secret);
            pi.Read(InstallPaths.SystemLog).Should().NotContain(secret);
        }

        pi.Read(InstallPaths.SystemLog).Should().Contain($"to {DefaultBackup}.");
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ABackup_WithOutput_WritesThatFile_FromTheCurrentFolder()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Folder("/srv/keep");
        pi.CurrentDirectory = "/srv/keep";

        var run = await pi.RunAsync("backup", "--output", "roof.tar.gz");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"folders to {Kept} (0600, root's):");
        pi.Mode(Kept).Should().Be(Modes.PrivateFile);
        pi.Exists(BackupCommands.DefaultFolder).Should().BeFalse("the file given is the only one written");
    }

    [TestMethod]
    [DataRow("/etc/hvo-roof/b.tar.gz", "/etc/hvo-roof/b.tar.gz is in /etc/hvo-roof, which the backup holds or uninstall --purge removes: keep the backup somewhere else.")]
    [DataRow("/var/lib/hvo-roof", "/var/lib/hvo-roof is in /var/lib/hvo-roof, which the backup holds")]
    [DataRow("/opt/hvo-roof-kiosk/b.tar.gz", "/opt/hvo-roof-kiosk/b.tar.gz is in /opt/hvo-roof-kiosk, which the backup holds or uninstall --purge removes")]
    [DataRow("/srv/keep/there.tar.gz", "/srv/keep/there.tar.gz is there already: a backup never replaces a file. Give a new file's name.")]
    [DataRow("/srv/keep", "/srv/keep is there already")]
    [DataRow("/nowhere/b.tar.gz", "/nowhere is not there: make it first, or give a file in a folder that is.")]
    public async Task ABackup_ToAFileItCannotWrite_IsRefused_AndWritesNothing(string output, string reason)
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Folder("/srv/keep").Write("/srv/keep/there.tar.gz", "mine\n");
        var before = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("backup", "--output", output);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain(reason);
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
    }

    [TestMethod]
    public async Task ABackup_IsRefused_InEveryFolderAPurgeRemoves()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        var record = InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));
        var purged = PlanBuilder.PurgedFolders(pi.Machine, record, ControllerLayout.System);

        purged.Select(folder => folder.Folder).Should().Contain([KioskSteps.ProgramFolder, KioskSteps.ConfigurationFolder, "/etc/hvo-roof", "/var/lib/hvo-roof"]);
        foreach (var (folder, _) in purged)
        {
            var run = await pi.RunAsync("backup", "--output", $"{folder}/b.tar.gz");

            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
            run.Error.Should().Contain($"is in {folder}, which the backup holds or uninstall --purge removes");
        }
    }

    [TestMethod]
    public async Task ABackup_AndARestore_AreRoots_AndForLinux()
    {
        using var pi = (await InstallerLifecycleTests.InstalledPiAsync()).AsPerson();
        using var mac = new FakeMachine(InstallerOs.MacOS, root: false, hostName: "mac", userName: "roy");

        var backup = await pi.RunAsync("backup");
        var restore = await RestoreAsync(pi, Kept);
        var onMac = await mac.RunAsync("backup");

        backup.ExitCode.Should().Be((int)InstallerExitCode.Refused, backup.ToString());
        backup.Error.Should().Contain("The files a backup holds are root's: run it with sudo, sudo hvo-roof-install backup.");
        restore.ExitCode.Should().Be((int)InstallerExitCode.Refused, restore.ToString());
        restore.Error.Should().Contain("sudo hvo-roof-install restore.");
        onMac.ExitCode.Should().Be((int)InstallerExitCode.Refused, onMac.ToString());
        onMac.Error.Should().Contain("On a Mac, Time Machine backs up what is yours.");
    }

    [TestMethod]
    public async Task ABackup_OfAMachineWithNothingOnIt_IsRefused()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("backup");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("There is nothing here to back up");
        pi.Exists(BackupCommands.DefaultFolder).Should().BeFalse();
    }

    [TestMethod]
    public async Task ABackup_ThenAPurge_ThenARestore_PutsTheMachineBackAsItWas_AndASecondRunChangesNothing()
    {
        using var pi = await InstalledPiAsync();
        var keys = pi.ApiKeyValues();
        var deviceKey = pi.Read(KioskSteps.DeviceKeyFile);
        var managed = pi.Read(ManagedSecret);
        var ca = pi.Read(Ca);
        var certificate = File.ReadAllBytes(pi.OnDisk(Certificate));
        var modes = new[] { InstallPaths.SystemRecord, KioskSteps.DeviceKeyFile, ManagedSecret, "/etc/hvo-roof/secrets", "/etc/hvo-roof/ca", "/var/lib/hvo-roof/settings-secrets" }.ToDictionary(path => path, pi.Mode);
        var owners = new[] { KioskSteps.DeviceKeyFile, KioskSteps.ConfigurationFolder }.ToDictionary(path => path, path => pi.Owners[path]);
        await BackUpAndPurgeAsync(pi);

        var run = await RestoreAsync(pi, Kept);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain($"The backup of roofpi, made 2026-10-01 09:00 UTC of 4.0.0, the controller and the kiosk, in {Kept}:")
            .And.Contain("  /etc/hvo-roof: ").And.Contain("  /var/lib/hvo-roof: 1 file to write")
            .And.MatchRegex("Put back [0-9]+ of the backup's [0-9]+ files and folders\\.")
            .And.NotContain("certificate for this machine's names");
        pi.Read(InstallPaths.SystemLog).Should().Contain($"Restoring from {Kept}: the controller and the kiosk.");
        pi.Read(ManagedSecret).Should().Be(managed);
        pi.ApiKeyValues().Should().BeEquivalentTo(keys);
        pi.Read(KioskSteps.DeviceKeyFile).Should().Be(deviceKey);
        pi.Read(Ca).Should().Be(ca);
        File.ReadAllBytes(pi.OnDisk(Certificate)).Should().Equal(certificate, "the certificate is for this machine's names still");
        foreach (var (path, mode) in modes)
        {
            pi.Mode(path).Should().Be(mode, path);
        }

        foreach (var (path, owner) in owners)
        {
            pi.Owners[path].Should().Be(owner, path);
        }

        var record = InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));
        record.Roles.Should().BeEquivalentTo([InstallRole.Controller, InstallRole.Kiosk]);
        record.UninstalledAt.Should().BeNull();
        pi.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
        pi.Kiosk.Should().Be((true, true));
        InstallerLifecycleTests.ShowsNoSecret(pi, run, keys);
        foreach (var secret in new[] { deviceKey, managed })
        {
            run.ToString().Should().NotContain(secret);
            pi.Read(InstallPaths.SystemLog).Should().NotContain(secret);
        }

        pi.Unexpected.Should().BeEmpty();

        var answers = pi.WriteAnswers(ControllerAndKiosk);
        var files = pi.Snapshot(InstallPaths.SystemLog);
        var again = await pi.RunAsync("--answers", answers);

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("Nothing to change");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
    }

    [TestMethod]
    public async Task ARestore_OverTheDataAnUninstallKept_NeedsReplace()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Folder("/srv/keep");
        (await pi.RunAsync("backup", "--output", Kept)).ExitCode.Should().Be(0);
        (await pi.RunAsync("uninstall", "--yes")).ExitCode.Should().Be(0);
        var before = pi.Snapshot(InstallPaths.SystemLog);

        var refused = await RestoreAsync(pi, Kept);

        refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
        refused.Output.Should().Contain("/etc/hvo-roof: 1 file to replace, ");
        refused.Error.Should().Contain("1 file here differs from the backup's (the data an uninstall kept, or another install's): give --replace to put the backup's in its place.");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);

        var run = await RestoreAsync(pi, Kept, "--replace");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Put back ");
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).UninstalledAt.Should().BeNull();
        pi.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
    }

    [TestMethod]
    public async Task ARestore_OnAMachineWithSomethingInstalled_IsRefused()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Folder("/srv/keep");
        (await pi.RunAsync("backup", "--output", Kept)).ExitCode.Should().Be(0);
        var before = pi.Snapshot(InstallPaths.SystemLog);

        var run = await RestoreAsync(pi, Kept, "--replace");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("the controller and the kiosk (4.0.0) is installed here: a restore is for a machine with nothing installed. Uninstall it first (sudo hvo-roof-install uninstall)");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
    }

    [TestMethod]
    public async Task ARestorePlan_SaysWhatItWouldPutBack_AndChangesNothing()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        await BackUpAndPurgeAsync(pi);
        var before = pi.Snapshot();

        var run = await RestoreAsync(pi, Kept, "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("files to write")
            .And.Contain("Then it installs the controller and the kiosk as the backup's record says, with this installer's release (4.0.0).");
        pi.Snapshot().Should().Equal(before, "a plan changes nothing, not even the log");
    }

    [TestMethod]
    public async Task ARestore_OfANewerReleasesBackup_IsRefused()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.WithRelease("4.0.1", latest: true);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = "4.0.1";
        await BackUpAndPurgeAsync(pi);
        pi.InstallerVersion = "4.0.0";

        var run = await RestoreAsync(pi, Kept);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("The backup is of 4.0.1, newer than this installer (4.0.0): restore it with release 4.0.1's installer");
    }

    [TestMethod]
    [DataRow("changed", "/etc/hvo-roof/install.json is not as its manifest says (its SHA-256 differs): the backup is damaged.")]
    [DataRow("unlisted", "it holds etc/hvo-roof/extra, which its manifest does not list.")]
    [DataRow("missing", "/etc/hvo-roof/install.json is in its manifest but not in it.")]
    [DataRow("outside", "hvo-roof-backup.json lists /root/.ssh/authorized_keys, which a backup does not hold.")]
    [DataRow("climbing", "hvo-roof-backup.json lists /etc/hvo-roof/../../root/.bashrc, which a backup does not hold.")]
    [DataRow("link", "/etc/hvo-roof/install.json is not a file of")]
    [DataRow("late manifest", "its first entry is not hvo-roof-backup.json.")]
    [DataRow("newer schema", "was made by a newer installer (backup schema 2; this one reads 1)")]
    [DataRow("not an archive", "is not a backup the installer can restore: ")]
    public async Task ARestore_OfAnArchiveThatIsNotAsItShouldBe_IsRefused_BeforeAnythingIsWritten(string damage, string reason)
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        await BackUpAndPurgeAsync(pi);
        Damage(pi, damage);
        var before = pi.Snapshot();

        var run = await RestoreAsync(pi, Kept);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain(reason);
        pi.Snapshot().Should().Equal(before);
        pi.Exists("/etc/hvo-roof").Should().BeFalse();
    }

    [TestMethod]
    [DataRow("0644", "root:root", "regular file", "root:root's, 0644")]
    [DataRow("0600", "pi:pi", "regular file", "pi:pi's, 0600")]
    [DataRow("0600", "root:root", "link", "symbolic link")]
    public async Task ARestore_OfAnArchiveOthersCouldRead_IsRefused(string mode, string owner, string kind, string reason)
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        await BackUpAndPurgeAsync(pi);
        var path = Kept;
        if (kind == "link")
        {
            path = "/srv/keep/link.tar.gz";
            File.CreateSymbolicLink(pi.OnDisk(path), pi.OnDisk(Kept));
        }
        else
        {
            File.SetUnixFileMode(pi.OnDisk(Kept), (UnixFileMode)Convert.ToInt32(mode, 8));
            pi.Owners[Kept] = owner;
        }

        var run = await RestoreAsync(pi, path);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"{path} must be a file only root reads (root's, 0600 or 0400): it holds the controller's secrets.").And.Contain(reason);
        pi.Exists("/etc/hvo-roof").Should().BeFalse();
    }

    [TestMethod]
    public async Task ARestore_ChecksTheArchiveItOpened_NotOnlyThePath()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        await BackUpAndPurgeAsync(pi);

        // Someone who can write /srv/keep puts their own archive (the same bytes, theirs) in its place just after
        // restore checks the path, and puts root's back just after restore opens it: the path is root's archive at
        // every check of the path, and only the open file is theirs.
        const string theirs = "/srv/keep/theirs.tar.gz";
        const string roots = "/srv/keep/roots.tar.gz";
        File.Copy(pi.OnDisk(Kept), pi.OnDisk(theirs));
        File.SetUnixFileMode(pi.OnDisk(theirs), Modes.PrivateFile);
        pi.Owners[theirs] = "pi:pi";
        var swapped = false;
        pi.AfterStat[Kept] = () =>
        {
            if (!swapped)
            {
                swapped = true;
                File.Move(pi.OnDisk(Kept), pi.OnDisk(roots));
                File.Move(pi.OnDisk(theirs), pi.OnDisk(Kept));
            }
        };
        pi.BeforeStat = () =>
        {
            if (swapped && File.Exists(pi.OnDisk(roots)))
            {
                File.Move(pi.OnDisk(Kept), pi.OnDisk(theirs));
                File.Move(pi.OnDisk(roots), pi.OnDisk(Kept));
            }
        };

        var run = await RestoreAsync(pi, Kept);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"{Kept} must be a file only root reads (root's, 0600 or 0400)").And.Contain("It is regular file, pi:pi's, 0600.");
        pi.Exists("/etc/hvo-roof").Should().BeFalse();
    }

    [TestMethod]
    [DataRow("/var/lib/hvo-roof/identity", "root:root", "0777")]
    [DataRow("/etc/hvo-roof", "root:root", "0775")]
    [DataRow(KioskSteps.ConfigurationFolder, "hvo-kiosk:hvo-kiosk", "0750")]
    [DataRow(KioskSteps.ProgramFolder, "pi:pi", "0755")]
    public async Task ABackup_OfAFolderSomeoneButRootCanChange_IsRefused_AndWritesNothing(string folder, string owner, string mode)
    {
        using var pi = await InstalledPiAsync();
        pi.Owners[folder] = owner;
        File.SetUnixFileMode(pi.OnDisk(folder), (UnixFileMode)Convert.ToInt32(mode, 8));
        pi.Folder("/srv/keep");

        var run = await pi.RunAsync("backup", "--output", Kept);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"{folder} is {owner}'s, {mode}: someone other than root can change what is in it, so a backup cannot trust what it reads there.");
        pi.Exists(Kept).Should().BeFalse();
        pi.Exists(Kept + ".partial").Should().BeFalse();
    }

    [TestMethod]
    public async Task ABackup_KeepsModesWithoutTheirSpecialBits_SoAFolderWithSetgid_IsRestored()
    {
        using var pi = await InstalledPiAsync();
        File.SetUnixFileMode(pi.OnDisk(KioskSteps.ConfigurationFolder), Modes.GroupFolder | UnixFileMode.SetGroup);
        await BackUpAndPurgeAsync(pi);

        Manifest(Entries(pi, Kept)).Single(entry => entry["path"]!.GetValue<string>() == KioskSteps.ConfigurationFolder)["mode"]!.GetValue<string>().Should().Be("0750");
        var run = await RestoreAsync(pi, Kept);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Mode(KioskSteps.ConfigurationFolder).Should().Be(Modes.GroupFolder);
    }

    [TestMethod]
    public async Task ABackup_RestoreCouldNotRead_IsNotKept()
    {
        using var pi = await InstalledPiAsync();
        var record = JsonNode.Parse(pi.Read(InstallPaths.SystemRecord))!;
        record["version"] = "4.0";
        pi.Write(InstallPaths.SystemRecord, record.ToJsonString());
        pi.Folder("/srv/keep");

        var run = await pi.RunAsync("backup", "--output", Kept);

        run.ExitCode.Should().NotBe(0, run.ToString());
        run.Error.Should().Contain("The backup was not kept: restore could not read it back. The backup's install record cannot be read: Its version, '4.0', is not a release version");
        pi.Exists(Kept).Should().BeFalse();
        pi.Exists(Kept + ".partial").Should().BeFalse();
    }

    [TestMethod]
    public async Task ARestore_OnANewPiWithTheSameName_KeepsTheCaAndTheCertificate()
    {
        using var pi = await InstalledPiAsync();
        using var next = await RestoredOnAsync(pi, "roofpi");

        next.Run.ExitCode.Should().Be(0, next.Run.ToString());
        next.Run.Output.Should().NotContain("certificate for this machine's names");
        next.Machine.Read(Ca).Should().Be(pi.Read(Ca));
        File.ReadAllBytes(next.Machine.OnDisk(Certificate)).Should().Equal(File.ReadAllBytes(pi.OnDisk(Certificate)));
        next.Machine.ApiKeyValues().Should().BeEquivalentTo(pi.ApiKeyValues());
        next.Machine.Read(KioskSteps.DeviceKeyFile).Should().Be(pi.Read(KioskSteps.DeviceKeyFile));
        next.Machine.Containers.Should().ContainKey(MachineSurveyor.ControllerContainer);
        InstallerLifecycleTests.ShowsNoSecret(next.Machine, next.Run, pi.ApiKeyValues());
        next.Machine.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARestore_OnARenamedPi_GivesTheControllerACertificateForItsNames_FromANewCa()
    {
        using var pi = await InstalledPiAsync();
        using var next = await RestoredOnAsync(pi, "roofpi2");

        next.Run.ExitCode.Should().Be(0, next.Run.ToString());
        next.Run.Output.Should().Contain("It is roofpi's; this machine is roofpi2: the install that follows gives the controller a certificate for this machine's names, from a new CA when the backup's may not issue for them")
            .And.Contain("it may not issue for roofpi2");
        next.Machine.Read(Ca).Should().NotBe(pi.Read(Ca), "the backup's CA may issue for roofpi's names only");
        next.Machine.ApiKeyValues().Should().BeEquivalentTo(pi.ApiKeyValues(), "the keys go with the backup whatever the name");
        var show = await next.Machine.RunAsync("cert", "show");
        show.Output.Should().Contain("  Subject:   roofpi2").And.Contain("  For:       roofpi2, roofpi2.local, ").And.Contain("HVO Roof CA (roofpi2, 2026-10-01), this machine's CA");
        InstallerLifecycleTests.ShowsNoSecret(next.Machine, next.Run, pi.ApiKeyValues());
        next.Machine.Unexpected.Should().BeEmpty();
    }

    // restore checks the archive it opened through /proc/<pid>/fd, which only Linux has: elsewhere these tests cannot run.
    private static Task<InstallerRun> RestoreAsync(FakeMachine pi, params string[] arguments)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("restore checks the archive it opened through /proc/<pid>/fd, which only Linux has.");
        }

        return pi.RunAsync(["restore", .. arguments]);
    }

    // The controller and the kiosk, installed, with a secret set through the API (as the controller keeps one).
    private static async Task<FakeMachine> InstalledPiAsync()
    {
        var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Write(ManagedSecret, "weather-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + "\n");
        File.SetUnixFileMode(pi.OnDisk(ManagedSecret), Modes.PrivateFile);
        return pi;
    }

    private sealed record Restored(FakeMachine Machine, InstallerRun Run) : IDisposable
    {
        public void Dispose() => Machine.Dispose();
    }

    // pi's backup, restored on a new Pi called hostName with nothing on it.
    private static async Task<Restored> RestoredOnAsync(FakeMachine pi, string hostName)
    {
        pi.Folder("/srv/keep");
        (await pi.RunAsync("backup", "--output", Kept)).ExitCode.Should().Be(0);
        var next = new FakeMachine(hostName: hostName).WithPi().WithDisplay().Write(CommandLine, Booted + "\n").Folder("/srv/keep");
        File.WriteAllBytes(next.OnDisk(Kept), File.ReadAllBytes(pi.OnDisk(Kept)));
        File.SetUnixFileMode(next.OnDisk(Kept), Modes.PrivateFile);
        return new Restored(next, await RestoreAsync(next, Kept));
    }

    // A backup in Kept, then the machine purged: nothing installed, no data.
    private static async Task BackUpAndPurgeAsync(FakeMachine pi)
    {
        pi.Folder("/srv/keep");
        var backup = await pi.RunAsync("backup", "--output", Kept);
        backup.ExitCode.Should().Be(0, backup.ToString());
        var purge = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");
        purge.ExitCode.Should().Be(0, purge.ToString());
        pi.Exists("/etc/hvo-roof").Should().BeFalse();
        pi.Exists("/var/lib/hvo-roof").Should().BeFalse();
        pi.Exists(KioskSteps.DeviceKeyFile).Should().BeFalse();
        pi.Exists(KioskSteps.SettingsFile).Should().BeFalse();
    }

    private sealed record ArchiveEntry(string Name, TarEntryType Type, UnixFileMode Mode, byte[]? Content, string? LinkName = null);

    private static List<ArchiveEntry> Entries(FakeMachine pi, string path)
    {
        using var file = File.OpenRead(pi.OnDisk(path));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = new List<ArchiveEntry>();
        while (tar.GetNextEntry() is { } entry)
        {
            byte[]? content = null;
            if (entry.DataStream is { } data)
            {
                using var copy = new MemoryStream();
                data.CopyTo(copy);
                content = copy.ToArray();
            }

            entries.Add(new ArchiveEntry(entry.Name.TrimEnd('/'), entry.EntryType, entry.Mode, content ?? (entry.EntryType == TarEntryType.RegularFile ? [] : null)));
        }

        return entries;
    }

    private static List<JsonNode> Manifest(IReadOnlyList<ArchiveEntry> entries)
        => [.. JsonNode.Parse(entries[0].Content!)!["entries"]!.AsArray().Select(item => item!)];

    private static void Write(FakeMachine pi, string path, IEnumerable<ArchiveEntry> entries)
    {
        using (var file = File.Create(pi.OnDisk(path)))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var item in entries)
            {
                var entry = new PaxTarEntry(item.Type, item.Name) { Mode = item.Mode };
                if (item.LinkName is not null)
                {
                    entry.LinkName = item.LinkName;
                }

                if (item.Content is not null)
                {
                    entry.DataStream = new MemoryStream(item.Content);
                }

                tar.WriteEntry(entry);
            }
        }

        File.SetUnixFileMode(pi.OnDisk(path), Modes.PrivateFile);
    }

    // The backup in Kept, damaged as damage says: by a person, a disk, or someone who wants the restore to write elsewhere.
    private static void Damage(FakeMachine pi, string damage)
    {
        const string record = "etc/hvo-roof/install.json";
        var entries = Entries(pi, Kept);
        var manifest = JsonNode.Parse(entries[0].Content!)!;
        void List(string path)
        {
            manifest["entries"]!.AsArray().Add(new JsonObject { ["path"] = path, ["type"] = "file", ["mode"] = "0600", ["owner"] = "root:root", ["size"] = 2, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData("x\n"u8)) });
        }

        switch (damage)
        {
            case "changed":
                entries = [.. entries.Select(entry => entry.Name == record ? entry with { Content = [.. entry.Content!.Select((value, at) => at == 0 ? (byte)(value ^ 1) : value)] } : entry)];
                break;
            case "unlisted":
                entries.Add(new ArchiveEntry("etc/hvo-roof/extra", TarEntryType.RegularFile, Modes.PrivateFile, "x\n"u8.ToArray()));
                break;
            case "missing":
                entries.RemoveAll(entry => entry.Name == record);
                break;
            case "outside":
                List("/root/.ssh/authorized_keys");
                entries.Add(new ArchiveEntry("root/.ssh/authorized_keys", TarEntryType.RegularFile, Modes.PrivateFile, "x\n"u8.ToArray()));
                break;
            case "climbing":
                List("/etc/hvo-roof/../../root/.bashrc");
                entries.Add(new ArchiveEntry("etc/hvo-roof/../../root/.bashrc", TarEntryType.RegularFile, Modes.PrivateFile, "x\n"u8.ToArray()));
                break;
            case "link":
                entries = [.. entries.Select(entry => entry.Name == record ? new ArchiveEntry(record, TarEntryType.SymbolicLink, Modes.PrivateFile, null, "/etc/shadow") : entry)];
                break;
            case "late manifest":
                entries = [.. entries.Skip(1), entries[0]];
                break;
            case "newer schema":
                manifest["schema"] = 2;
                break;
            case "not an archive":
                File.WriteAllText(pi.OnDisk(Kept), "not a backup\n");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }

        if (damage is "outside" or "climbing" or "newer schema")
        {
            entries[0] = entries[0] with { Content = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };
        }

        Write(pi, Kept, entries);
    }

    private static string Regex(string text) => System.Text.RegularExpressions.Regex.Escape(text);
}
