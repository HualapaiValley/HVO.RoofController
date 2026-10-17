using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The installer's durable changes (#136 F7 and F8): each makes what it changes durable before it returns, the file
/// itself before the folder entry that names it, each folder made through the folder above it, and goes no further when
/// a sync fails. The fake machine's power loss keeps only what the syncs made durable, as a file system does.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerDurabilityTests
{
    private const string Folder = "/var/lib/hvo-roof/identity";

    private const string File1 = Folder + "/identity.json";

    private static readonly byte[] Before = Encoding.UTF8.GetBytes("before");

    private static readonly byte[] After = Encoding.UTF8.GetBytes("after");

    [TestMethod]
    public void TheFake_LosesARenameIntoAFolder_AndAFolderMade_UntilTheFolderAboveIsSynced()
    {
        using var pi = new FakeMachine();
        pi.TrackFolderDurability("/var/backups");
        pi.Machine.CreateDirectory("/var/backups/hvo-roof", Modes.PrivateFolder);
        pi.Write(File1, "before").TrackDurability(File1);
        pi.Machine.WriteAtomically(File1, After, Modes.PrivateFile);

        pi.PowerLoss();

        pi.Exists("/var/backups").Should().BeFalse("nothing synced /var");
        pi.Read(File1).Should().Be("before", "nothing synced the identity folder");
    }

    [TestMethod]
    public void WriteDurably_SyncsTheFile_ThenItsFolder()
    {
        using var pi = new FakeMachine();
        pi.Write(File1, "before").TrackDurability(File1);
        pi.DurabilityEvents.Clear();

        pi.Machine.WriteDurably(File1, After, Modes.PrivateFile);

        pi.DurabilityEvents.Should().Equal($"sync-file {File1}", $"sync-folder {Folder}");
        pi.SurvivesPowerLoss(File1).Should().BeTrue();
        pi.PowerLoss();
        pi.Read(File1).Should().Be("after");
        pi.Mode(File1).Should().Be(Modes.PrivateFile);
    }

    [TestMethod]
    public void MoveFileDurably_SyncsTheFileBeforeTheRename_ThenEachFolder()
    {
        using var pi = new FakeMachine();
        const string staged = "/var/tmp/staged";
        pi.Write(staged, "after").Write(File1, "before").TrackDurability(File1);
        pi.Owners[staged] = "hvo-roof:hvo-roof";
        pi.DurabilityEvents.Clear();

        pi.Machine.MoveFileDurably(staged, File1, overwrite: true);

        pi.DurabilityEvents.Should().Equal($"sync-file {staged}", $"rename {staged} {File1}", $"sync-folder {Folder}", "sync-folder /var/tmp");
        pi.Owners[File1].Should().Be("hvo-roof:hvo-roof", "the owner goes with the file");
        pi.PowerLoss();
        pi.Read(File1).Should().Be("after");
    }

    [TestMethod]
    public void MoveFileDurably_ToANewName_TakesItAwayAgain_WhenItsFolderCannotBeSynced()
    {
        using var pi = new FakeMachine();
        const string partial = Folder + "/backup.partial";
        const string backup = Folder + "/backup";
        pi.Write(partial, "archive");
        pi.DirectorySyncFailure = "Input/output error";

        var move = () => pi.Machine.MoveFileDurably(partial, backup);

        move.Should().Throw<IOException>().WithMessage("*Input/output error*");
        pi.Exists(backup).Should().BeFalse("a new name that is not durable is not left for a later step to rely on");
    }

    [TestMethod]
    public void MoveFileDurably_StopsBeforeTheRename_WhenTheFileCannotBeSynced()
    {
        using var pi = new FakeMachine();
        const string staged = Folder + "/identity.json.restoring";
        pi.Write(staged, "after").Write(File1, "before");
        pi.FileSyncFailure = "Input/output error";

        var move = () => pi.Machine.MoveFileDurably(staged, File1, overwrite: true);

        move.Should().Throw<IOException>();
        pi.Read(File1).Should().Be("before");
        pi.DurabilityEvents.Should().NotContain(change => change.StartsWith("rename ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeleteFileDurably_SyncsTheFolder()
    {
        using var pi = new FakeMachine();
        pi.Write(File1, "before").TrackDurability(File1);
        pi.DurabilityEvents.Clear();

        pi.Machine.DeleteFileDurably(File1);

        pi.DurabilityEvents.Should().Equal($"sync-folder {Folder}");
        pi.PowerLoss();
        pi.Exists(File1).Should().BeFalse();
    }

    [TestMethod]
    public void CreateDirectoryDurably_SyncsTheFolderAboveTheFirstMade_ThenEachMade()
    {
        using var pi = new FakeMachine();
        pi.TrackFolderDurability("/var/backups").TrackFolderDurability("/var/backups/hvo-roof");

        pi.Machine.CreateDirectoryDurably("/var/backups/hvo-roof", Modes.PrivateFolder);

        pi.DirectorySyncs.Should().Equal("/var", "/var/backups", "/var/backups/hvo-roof");
        pi.Mode("/var/backups").Should().Be(Modes.Folder);
        pi.Mode("/var/backups/hvo-roof").Should().Be(Modes.PrivateFolder);
        pi.PowerLoss();
        pi.Exists("/var/backups/hvo-roof").Should().BeTrue();

        // One that is there already: nothing made, nothing synced.
        pi.DirectorySyncs.Clear();
        pi.Machine.CreateDirectoryDurably("/var/backups/hvo-roof", Modes.PrivateFolder);
        pi.DirectorySyncs.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("/var")]
    [DataRow("/var/backups")]
    [DataRow("/var/backups/hvo-roof")]
    public void CreateDirectoryDurably_TakesAwayWhatItMade_WhenASyncFails(string failing)
    {
        using var pi = new FakeMachine();
        pi.DuringDirectorySync = folder => pi.DirectorySyncFailure = folder == failing ? "Input/output error" : null;

        var create = () => pi.Machine.CreateDirectoryDurably("/var/backups/hvo-roof", Modes.PrivateFolder);

        create.Should().Throw<IOException>().WithMessage($"*{failing}*Input/output error*");
        pi.Exists("/var/backups").Should().BeFalse("each folder it made is taken away again");
        pi.Exists("/var").Should().BeTrue();
    }

    [TestMethod]
    public void SyncFile_OnAMac_HasTheDriveWriteItsCacheOut_AndNeverTriesFsyncInItsPlace()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, root: false, userName: "roys");
        const string file = "/Users/roys/notes";
        mac.Write(file, "notes");

        mac.Machine.SyncFile(file);
        mac.SyncCalls.Should().Equal("open", "F_FULLFSYNC", "close");

        mac.SyncCalls.Clear();
        mac.FullFsyncError = 45;
        var sync = () => mac.Machine.SyncFile(file);
        sync.Should().Throw<IOException>().WithMessage("Could not sync the file *notes to disk (F_FULLFSYNC: *Keep the file on a file system that can*");
        mac.SyncCalls.Should().Equal("open", "F_FULLFSYNC", "close");
    }
}
