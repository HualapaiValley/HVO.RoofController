using System.CommandLine;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// <c>backup</c> and <c>restore</c>: the controller's and the kiosk's files in one archive only root reads, and that
/// archive put back on a machine (a new Pi, or this one after <c>uninstall --purge</c>) followed by an install of what
/// its record says. The archive holds secrets (the controller's, its certificate authority's key, the kiosk's device key),
/// so nothing it holds is ever printed or logged: only paths and counts.
/// </summary>
internal static class BackupCommands
{
    /// <summary>Where a backup goes when no file is given: a folder only root opens.</summary>
    public const string DefaultFolder = "/var/backups/hvo-roof";

    /// <summary>The archive's first entry: what it holds, with each file's mode, owner and SHA-256.</summary>
    public const string ManifestName = "hvo-roof-backup.json";

    public const int CurrentSchema = 1;

    /// <summary>The most a backup holds (and a restore reads), unpacked: far more than the controller's files come to.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private const long MaxManifestBytes = 4L * 1024 * 1024;

    /// <summary>
    /// The folders a backup holds, whole: the controller's configuration (settings, secrets, certificate, CA and the install
    /// record), its data (people, managed API keys, managed secrets) and the kiosk's device key.
    /// </summary>
    public static IReadOnlyList<string> Folders { get; } = [ControllerLayout.System.Configuration, ControllerLayout.System.Data, KioskSteps.ConfigurationFolder, KioskCommissioningClaimStep.ClaimDirectory];

    /// <summary>The files a backup holds on their own: the kiosk's settings (its program comes from the release).</summary>
    public static IReadOnlyList<string> Files { get; } = [KioskSteps.SettingsFile];

    public static IEnumerable<Command> Create(InstallerHost host)
    {
        yield return Backup(host);
        yield return Restore(host);
    }

    private static Command Backup(InstallerHost host)
    {
        var output = new Option<string?>("--output")
        {
            Description = $"The file to write, which must not be there. Without it, a new file in {DefaultFolder}.",
            HelpName = "FILE"
        };
        var backup = new Command(
            "backup",
            "Backs up the controller's and the kiosk's files into one archive only root reads: the controller's secrets, its "
            + "certificate and certificate authority, its people and settings, the kiosk's device key and settings, and the record "
            + "of what is installed. restore puts them back. Keep a copy off the Pi, where only you can read it.");
        backup.Options.Add(output);
        backup.SetAction((parseResult, token) => Installer.GuardAsync(host, () => BackupAsync(host, parseResult.GetValue(output), token), token));
        return backup;
    }

    private static Command Restore(InstallerHost host)
    {
        var file = new Argument<string>("FILE") { Description = "The backup to restore, which only root may read." };
        var replace = new Option<bool>("--replace")
        {
            Description = "Put the backup's files in place of different ones here (the data an uninstall kept)."
        };
        var plan = new Option<bool>("--plan") { Description = "Print what the restore would put back, and change nothing." };
        var release = Installer.ReleaseOption("Read the release");
        var restore = new Command(
            "restore",
            "Puts a backup's files back, on a new Pi or on this one after an uninstall, then installs what the backup's record "
            + "says, as the installer would: the controller (with a new certificate when this machine's names differ), the "
            + "kiosk, and the rest.");
        restore.Arguments.Add(file);
        restore.Options.Add(replace);
        restore.Options.Add(plan);
        restore.Options.Add(release);
        restore.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => RestoreAsync(host, parseResult.GetValue(file)!, parseResult.GetValue(replace), parseResult.GetValue(plan), parseResult.GetValue(release), token),
            token));
        return restore;
    }

    private static async Task<int> BackupAsync(InstallerHost host, string? output, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        NeedsRootOnLinux(machine, "backup");
        var session = await InstallerSession.StartAsync(
            machine,
            InstallLog.Open(machine, InstallPaths.Log(machine), host.Time),
            host.Version,
            host.Time,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var written = await WriteAsync(host, session, output, cancellationToken).ConfigureAwait(false);
        host.Out.WriteLine();
        host.Out.WriteLine($"Keep a copy of {written} off this machine, where only you can read it: it holds the controller's secrets, "
            + "its certificate authority's key and the kiosk's device key. Anyone with it can act as the controller.");
        return (int)InstallerExitCode.Success;
    }

    /// <summary>
    /// Writes a backup to <paramref name="output"/> (a new file in <see cref="DefaultFolder"/> when null), and says what it
    /// holds: paths and counts, nothing from the files. It goes to a file beside the backup first, made 0600, renamed to
    /// the backup once it is whole; a backup that fails leaves nothing.
    /// </summary>
    internal static async Task<string> WriteAsync(InstallerHost host, InstallerSession session, string? output, CancellationToken cancellationToken)
    {
        var now = host.Time.GetUtcNow();
        var written = await WriteArchiveAsync(
            session.Machine,
            session.Log,
            session.Survey.HostName,
            InstallerSession.RoofVersion(host.Version),
            session.Survey.SystemRecord?.Version,
            now,
            () => OutputPathAsync(session.Machine, session.Survey.HostName, now, output, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var entries = written.Entries;
        var files = entries.Count(entry => entry.Type == BackupEntry.File);
        var folders = entries.Count - files;
        var path = written.Path;
        host.Out.WriteLine($"Backed up {files} files and {folders} folders to {path} ({Modes.Octal(Modes.PrivateFile)}, root's):");
        foreach (var root in Folders.Concat(Files))
        {
            var held = entries.Count(entry => entry.Type == BackupEntry.File && Within(entry.Path, root));
            if (held > 0)
            {
                host.Out.WriteLine($"  {root}: {held} {(held == 1 ? "file" : "files")}");
            }
        }

        foreach (var left in written.Skipped)
        {
            host.Out.WriteLine($"  Left out {left}: a backup holds files and folders only.");
        }

        return path;
    }

    /// <summary>
    /// Writes a backup to <paramref name="output"/>, which must not be there, as <see cref="WriteAsync"/> does, saying
    /// nothing but in the log: what <c>upgrade</c> takes before it changes anything (<see cref="UpgradeBackupStep"/>).
    /// Its manifest names each of the stores' files (<see cref="StoreFiles"/>) that was not there (<c>absent</c>). With
    /// <paramref name="storesRequired"/> (an upgrade's), one that is there but that a backup cannot hold (a link, say)
    /// refuses it before anything is written: a rollback could not put that store back.
    /// </summary>
    internal static async Task<WrittenBackup> WriteArchiveAsync(
        InstallerMachine machine,
        InstallLog log,
        string hostName,
        string installerVersion,
        string? recordVersion,
        DateTimeOffset now,
        Func<Task<string>> output,
        CancellationToken cancellationToken,
        bool storesRequired = false)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(output);
        var skipped = new List<string>();
        var entries = await CollectAsync(machine, skipped, cancellationToken).ConfigureAwait(false);
        var files = entries.Count(entry => entry.Type == BackupEntry.File);
        if (files == 0)
        {
            throw new InstallerRefusedException($"There is nothing here to back up: {string.Join(", ", Folders)} and {string.Join(", ", Files)} hold no files.");
        }

        var absent = new List<string>();
        foreach (var store in StoreFiles)
        {
            if (await IsAbsentAsync(machine, store, cancellationToken).ConfigureAwait(false))
            {
                absent.Add(store);
            }
            else if (storesRequired && !entries.Any(entry => entry.Path == store && entry.Type == BackupEntry.File))
            {
                throw new InstallerRefusedException($"{NotHeld(store, await NotAFileAsync(machine, store, cancellationToken).ConfigureAwait(false) ?? "the backup left it out")}. Nothing was changed.");
            }
        }

        var path = await output().ConfigureAwait(false);
        var manifest = new BackupManifest(
            CurrentSchema,
            hostName,
            now,
            installerVersion,
            recordVersion,
            entries,
            absent);
        var partial = path + ".partial";
        try
        {
            var stream = machine.CreateNew(partial, Modes.PrivateFile);
            await using (stream.ConfigureAwait(false))
            {
                var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
                await using (gzip.ConfigureAwait(false))
                {
                    var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
                    await using (tar.ConfigureAwait(false))
                    {
                        await WriteEntryAsync(tar, ManifestName, Modes.PrivateFile, now, JsonSerializer.SerializeToUtf8Bytes(manifest, InstallerJson.Options), cancellationToken).ConfigureAwait(false);
                        foreach (var entry in entries)
                        {
                            await WriteEntryAsync(tar, entry.Path.TrimStart('/'), entry.FileMode, now, entry.Content, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await ReadBackAsync(machine, partial, path, cancellationToken).ConfigureAwait(false);
            machine.SetMode(partial, Modes.PrivateFile);
            machine.MoveFile(partial, path, overwrite: false);
        }
        finally
        {
            if (machine.FileExists(partial))
            {
                machine.DeleteFile(partial);
            }
        }

        log.Write($"Backed up {files} files and {entries.Count - files} folders to {path}.");
        foreach (var left in skipped)
        {
            log.Write($"Left out of the backup: {left}.");
        }

        return new WrittenBackup(path, entries, skipped);
    }

    // The archive just written, read as restore reads it: a backup restore would refuse fails now, not when it is needed.
    private static async Task ReadBackAsync(InstallerMachine machine, string partial, string path, CancellationToken cancellationToken)
    {
        try
        {
            var stream = machine.OpenRead(partial) ?? throw new InstallerException($"{partial} went while it was written.");
            await using (stream.ConfigureAwait(false))
            {
                await ReadArchiveAsync(stream, path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InstallerRefusedException error)
        {
            throw new InstallerException($"The backup was not kept: restore could not read it back. {error.Message}");
        }
    }

    private static async Task WriteEntryAsync(TarWriter tar, string name, UnixFileMode mode, DateTimeOffset time, byte[]? content, CancellationToken cancellationToken)
    {
        var entry = new PaxTarEntry(content is null ? TarEntryType.Directory : TarEntryType.RegularFile, name)
        {
            Mode = mode,
            ModificationTime = time
        };

        // A folder entry refuses a data stream, even an empty one.
        if (content is not null)
        {
            entry.DataStream = new MemoryStream(content, writable: false);
        }

        await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    // Every folder and file the backup holds, parents first, each file's content read once (so the SHA-256 the manifest
    // gives is of what the archive holds). Links and special files are left out, and never followed. Each folder read,
    // and each file's own folder, must be one only root changes: whoever else could would swap a file for a link, to
    // somewhere the backup then reads and restore writes back. Modes are kept without their special bits (setuid,
    // setgid, sticky): the installer sets none, and restore takes permission bits alone.
    private static async Task<List<BackupEntry>> CollectAsync(InstallerMachine machine, List<string> skipped, CancellationToken cancellationToken)
    {
        var entries = new List<BackupEntry>();
        long total = 0;
        foreach (var folder in Folders)
        {
            await AddAsync(folder, recurse: true).ConfigureAwait(false);
        }

        foreach (var file in Files)
        {
            if (machine.FileExists(file))
            {
                var parent = Path.GetDirectoryName(file)!;
                if (await StatAsync(machine, parent, cancellationToken).ConfigureAwait(false) is var (_, parentOwner))
                {
                    OnlyRootChanges(parent, parentOwner, machine.GetMode(parent));
                }
            }

            await AddAsync(file, recurse: false).ConfigureAwait(false);
        }

        return entries;

        async Task AddAsync(string path, bool recurse)
        {
            if (await StatAsync(machine, path, cancellationToken).ConfigureAwait(false) is not var (kind, owner))
            {
                return;
            }

            var mode = (UnixFileMode)((int)(machine.GetMode(path) ?? Modes.PrivateFile) & 0x1FF);
            if (kind == "directory" && recurse)
            {
                OnlyRootChanges(path, owner, mode);
                entries.Add(new BackupEntry(path, BackupEntry.Folder, Modes.Octal(mode), owner));
                foreach (var name in machine.ListNames(path))
                {
                    await AddAsync(Path.Join(path, name), recurse).ConfigureAwait(false);
                }
            }
            else if (kind is "regular file" or "regular empty file")
            {
                var content = machine.ReadBytes(path) ?? [];
                total += content.Length;
                if (total > MaxBytes)
                {
                    throw new InstallerRefusedException($"{string.Join(", ", Folders)} hold more than {MaxBytes / 1024 / 1024} MB: more than the controller's files come to. Look at what is in them.");
                }

                entries.Add(new BackupEntry(path, BackupEntry.File, Modes.Octal(mode), owner, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)))
                {
                    Content = content
                });
            }
            else
            {
                skipped.Add($"{path} ({kind})");
            }
        }
    }

    private static void OnlyRootChanges(string folder, string? owner, UnixFileMode? mode)
    {
        const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if (owner?.StartsWith("root:", StringComparison.Ordinal) != true || mode is not { } found || (found & othersWrite) != 0)
        {
            throw new InstallerRefusedException(
                $"{folder} is {owner ?? "an unknown owner"}'s, {(mode is { } known ? Modes.Octal(known) : "unknown")}: someone other than root can change what is in it, "
                + $"so a backup cannot trust what it reads there. Give it to root, with only root writing (sudo chown root {folder}; sudo chmod go-w {folder}), then back up. Nothing was written.");
        }
    }

    /// <summary>
    /// The files the controller's stores are kept in (the state contracts'): a backup holds each that is there, and its
    /// manifest names each that was not (<c>absent</c>), so a rollback that puts a store back never takes a file the
    /// backup left out for one that was never saved.
    /// </summary>
    internal static IReadOnlyList<string> StoreFiles { get; } = [.. StateContractGuard.Files(ControllerLayout.System).SelectMany(store => store.Files)];

    /// <summary>
    /// Why an upgrade cannot back up <paramref name="store"/>, one of <see cref="StoreFiles"/>: it is there, but a backup
    /// cannot hold it (it holds files and folders only, and follows no link). Null when it can: a file, or nothing there.
    /// What the upgrade's backup checks before anything changes.
    /// </summary>
    internal static async Task<string?> CannotHoldAsync(InstallerMachine machine, string store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return await IsAbsentAsync(machine, store, cancellationToken).ConfigureAwait(false) || await NotAFileAsync(machine, store, cancellationToken).ConfigureAwait(false) is not { } what
            ? null
            : NotHeld(store, what);
    }

    // Verified not there: stat finds nothing at the path, not even a link, and says so (it fails no other way).
    private static async Task<bool> IsAbsentAsync(InstallerMachine machine, string path, CancellationToken cancellationToken)
    {
        var result = await machine.Commands.RunAsync(new CommandLine("stat", "-c", "%F|%U:%G", "--", path), cancellationToken).ConfigureAwait(false);
        return !result.Succeeded && !machine.FileExists(path) && !machine.DirectoryExists(path)
            && (result.Error.Contains("No such file or directory", StringComparison.Ordinal) || result.Error.Contains("Not a directory", StringComparison.Ordinal));
    }

    // What keeps a backup from holding a store's file, as CollectAsync walks to it from the folder it backs up: a folder on
    // the way that is not one (a link), or the file not a file. Null when nothing does.
    private static async Task<string?> NotAFileAsync(InstallerMachine machine, string store, CancellationToken cancellationToken)
    {
        var root = Folders.First(folder => Within(store, folder));
        var folders = new List<string>();
        for (var folder = Path.GetDirectoryName(store)!; ; folder = Path.GetDirectoryName(folder)!)
        {
            folders.Insert(0, folder);
            if (folder == root)
            {
                break;
            }
        }

        foreach (var folder in folders)
        {
            if (await StatAsync(machine, folder, cancellationToken).ConfigureAwait(false) is var (kind, _) && kind != "directory")
            {
                return $"{folder} is a {kind}";
            }
        }

        return await StatAsync(machine, store, cancellationToken).ConfigureAwait(false) is var (storeKind, _) && storeKind is not ("regular file" or "regular empty file")
            ? $"it is a {storeKind}"
            : null;
    }

    private static string NotHeld(string store, string what)
        => $"{store} is there, but {what}, and a backup holds only files and folders (it follows no link): the upgrade backs up the stores before it changes anything, "
            + "and a rollback could not put this one back. Put the file itself in its place, then upgrade";

    // What the path is (as stat names it: "regular file", "directory", "symbolic link", …) and who owns it, without
    // following a link (or, with follow, of what a link names); null when it is not there.
    private static async Task<(string Kind, string? Owner)?> StatAsync(InstallerMachine machine, string path, CancellationToken cancellationToken, bool follow = false)
    {
        var result = await machine.Commands.RunAsync(
            follow ? new CommandLine("stat", "-L", "-c", "%F|%U:%G", "--", path) : new CommandLine("stat", "-c", "%F|%U:%G", "--", path),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return machine.FileExists(path) || machine.DirectoryExists(path)
                ? throw new InstallerException($"Could not tell what {path} is: {result.Reason}")
                : null;
        }

        var parts = result.Output.Trim().Split('|', 2);
        var owner = parts.Length == 2 && !parts[1].Contains("UNKNOWN", StringComparison.Ordinal) ? parts[1] : null;
        return (parts[0], owner);
    }

    /// <summary>
    /// A new backup's file in <see cref="DefaultFolder"/>: <c>hvo-roof-&lt;host&gt;-&lt;UTC time&gt;.tar.gz</c>, or, for the one an
    /// upgrade takes, <c>…-before-&lt;version&gt;.tar.gz</c>.
    /// </summary>
    internal static string DefaultPath(string hostName, DateTimeOffset now, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        var shortName = hostName.Split('.')[0];
        var name = $"hvo-roof-{shortName}-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}{(before is null ? string.Empty : $"-before-{before}")}.tar.gz";
        return Path.Join(DefaultFolder, name);
    }

    /// <summary>
    /// Makes <see cref="DefaultFolder"/>, only root's, when it is not there (its parents 0755, root's); refuses one that is
    /// there but not trusted (<see cref="FolderProblemAsync"/>), and never repairs it: a folder someone else could change
    /// may hold their archive already.
    /// </summary>
    internal static async Task MakeDefaultFolderAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (await FolderProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new InstallerRefusedException($"{problem}. Nothing was written.");
        }

        if (!machine.DirectoryExists(DefaultFolder))
        {
            machine.CreateDirectory(DefaultFolder, Modes.PrivateFolder);
            if (await FolderProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } made)
            {
                throw new InstallerRefusedException($"{made}. Nothing was written.");
            }
        }
    }

    /// <summary>
    /// Why a backup cannot be trusted in <see cref="DefaultFolder"/>; null when it can, or when the folder is not there yet
    /// (the installer makes it). It and each folder above it, up to <c>/</c>, must be a folder, not a link, root's, and
    /// written to by root alone: whoever else could change one could put another archive (one of theirs, or an older one
    /// of root's) in place of the one an upgrade took, between the moment it is named and the moment it is read. The
    /// installer refuses such a folder, and does not repair it, even one it made: what is in it may not be its own.
    /// </summary>
    internal static async Task<string?> FolderProblemAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        const string why = "a backup is kept, and read, only in folders no one but root can change, so that no other archive can take its place";
        for (var folder = DefaultFolder; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (await StatAsync(machine, folder, cancellationToken).ConfigureAwait(false) is not var (kind, owner))
            {
                continue;
            }

            if (kind != "directory")
            {
                return $"{folder} is a {kind}, not a folder: {why}. Put a folder of root's that only root writes to in its place, then run it again";
            }

            if (owner?.StartsWith("root:", StringComparison.Ordinal) != true || machine.GetMode(folder) is not { } mode || (mode & othersWrite) != 0)
            {
                var found = machine.GetMode(folder) is { } known ? Modes.Octal(known) : "unknown";
                return $"{folder} is {owner ?? "an unknown owner"}'s, {found}: someone other than root can change what is in it, and {why}. "
                    + $"Give it to root, with only root writing (sudo chown root:root {folder}; sudo chmod go-w {folder}), then run it again";
            }
        }

        return null;
    }

    private static async Task<string> OutputPathAsync(InstallerMachine machine, string hostName, DateTimeOffset now, string? output, CancellationToken cancellationToken)
    {
        if (output is null)
        {
            await MakeDefaultFolderAsync(machine, cancellationToken).ConfigureAwait(false);
            return DefaultPath(hostName, now);
        }

        var path = Path.GetFullPath(output, machine.CurrentDirectory);
        if (Folders.Concat(Files).Append(KioskSteps.ProgramFolder).FirstOrDefault(root => Within(path, root)) is { } inside)
        {
            throw new InstallerRefusedException($"{path} is in {inside}, which the backup holds or uninstall --purge removes: keep the backup somewhere else.");
        }

        if (machine.FileExists(path) || machine.DirectoryExists(path))
        {
            throw new InstallerRefusedException($"{path} is there already: a backup never replaces a file. Give a new file's name.");
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is null || !machine.DirectoryExists(parent))
        {
            throw new InstallerRefusedException($"{parent} is not there: make it first, or give a file in a folder that is.");
        }

        // The backups' own folder is held to its rule, whoever names a file in it.
        if (parent == DefaultFolder && await FolderProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new InstallerRefusedException($"{problem}. Nothing was written.");
        }

        return path;
    }

    private static bool Within(string path, string root)
        => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static bool IsAllowed(string path)
        => Files.Contains(path, StringComparer.Ordinal) || Folders.Any(folder => Within(path, folder));

    private static void NeedsRootOnLinux(InstallerMachine machine, string command)
    {
        CertificateCommands.RefuseRootOnMac(machine, command);
        if (machine.Os == InstallerOs.MacOS)
        {
            throw new InstallerRefusedException($"{command} is for the controller and the kiosk, on Linux. On a Mac, Time Machine backs up what is yours.");
        }

        if (!machine.IsRoot)
        {
            throw new InstallerRefusedException($"The files a backup holds are root's: run it with sudo, sudo {Installer.CommandName} {command}.");
        }
    }

    private static async Task<int> RestoreAsync(InstallerHost host, string file, bool replace, bool planOnly, string? releaseFolder, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        NeedsRootOnLinux(machine, "restore");
        var release = Installer.Release(host, releaseFolder);
        var path = Path.GetFullPath(file, machine.CurrentDirectory);
        var backup = await ReadAsync(machine, path, cancellationToken).ConfigureAwait(false);
        RefusePendingCommissioning(backup);
        var record = backup.Record;
        var version = InstallerSession.RoofVersion(host.Version);
        if (record is not null && RoofSemVer.IsOlder(version, record.Version))
        {
            throw new InstallerRefusedException(
                $"The backup is of {record.Version}, newer than this installer ({version}): restore it with release {record.Version}'s installer, from {ReleaseManifest.PageUri(record.Version)}.");
        }

        var session = await InstallerSession.StartAsync(
            machine,
            planOnly ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time),
            host.Version,
            host.Time,
            release,
            cancellationToken).ConfigureAwait(false);
        var survey = session.Survey;
        if (survey.SystemRecord is { UninstalledAt: null, Roles.Count: > 0 } installed)
        {
            throw new InstallerRefusedException(
                $"{InstallRoles.Describe(installed.Roles)} ({installed.Version}) is installed here: a restore is for a machine with nothing installed. "
                + $"Uninstall it first (sudo {Installer.CommandName} uninstall), then restore, with --replace to put the backup's files over the ones it keeps.");
        }

        if (survey.Controller is not null)
        {
            throw new InstallerRefusedException(
                $"A controller ({MachineSurveyor.ControllerContainer}) is deployed here: a restore is for a machine with nothing installed. Remove it with the deploy script first (it stops the roof), then restore.");
        }

        var changes = backup.Entries.Select(entry => (Entry: entry, Change: ChangeFor(machine, entry))).ToList();
        var blocked = changes.Where(change => change.Change == StepChange.Blocked).Select(change => change.Entry.Path).ToList();
        var replacing = changes.Count(change => change.Entry.Type == BackupEntry.File && change.Change == StepChange.Change);
        var recordText = record is null ? string.Empty : $" of {record.Version}, {(record.Roles.Count > 0 ? InstallRoles.Describe(record.Roles) : "nothing installed")}";
        host.Out.WriteLine($"The backup of {backup.Manifest.Host}, made {backup.Manifest.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC{recordText}, in {path}:");
        foreach (var root in Folders.Concat(Files))
        {
            var under = changes.Where(change => change.Entry.Type == BackupEntry.File && Within(change.Entry.Path, root)).ToList();
            if (under.Count > 0)
            {
                host.Out.WriteLine($"  {root}: {Counts(under.Select(change => change.Change))}");
            }
        }

        if (!string.Equals(backup.Manifest.Host, survey.HostName, StringComparison.OrdinalIgnoreCase))
        {
            host.Out.WriteLine($"It is {backup.Manifest.Host}'s; this machine is {survey.HostName}: the install that follows gives the controller a certificate for this machine's names, "
                + "from a new CA when the backup's may not issue for them (its plan says which). Clients then trust the new CA in place of the backup's.");
        }

        if (blocked.Count > 0)
        {
            throw new InstallerRefusedException($"{string.Join(", ", blocked)}: a file where the backup has a folder, or a folder where it has a file. Move it out of the way first.");
        }

        if (replacing > 0 && !replace)
        {
            throw new InstallerRefusedException(
                $"{replacing} {(replacing == 1 ? "file" : "files")} here {(replacing == 1 ? "differs" : "differ")} from the backup's (the data an uninstall kept, or another install's): give --replace to put the backup's in {(replacing == 1 ? "its" : "their")} place.");
        }

        var installs = record is { UninstalledAt: null, Roles.Count: > 0 };
        if (planOnly)
        {
            host.Out.WriteLine(installs
                ? $"Then it installs {InstallRoles.Describe(record!.Roles)} as the backup's record says, with this installer's release ({version}). Without --plan it shows that plan as it goes."
                : "The backup's record installs nothing: only its files go back.");
            return (int)InstallerExitCode.Success;
        }

        host.Out.WriteLine();
        Put(machine, session.Log, changes);
        var owners = await GiveOwnersAsync(machine, session.Log, backup.Entries, cancellationToken).ConfigureAwait(false);
        var written = changes.Count(change => change.Change != StepChange.Unchanged);
        host.Out.WriteLine($"Put back {written} of the backup's {backup.Entries.Count} files and folders.");
        session.Log.Write($"Restored {written} files and folders from {path}.");
        if (owners > 0)
        {
            host.Out.WriteLine($"{owners} could not be given to {(owners == 1 ? "its" : "their")} owner yet (a user the install makes): the install that follows does.");
        }

        if (!installs)
        {
            host.Out.WriteLine($"The backup's record installs nothing: its files are back. To install, sudo {Installer.CommandName}.");
            return (int)InstallerExitCode.Success;
        }

        host.Out.WriteLine();
        var restored = await InstallerSession.StartAsync(machine, session.Log, host.Version, host.Time, release, cancellationToken).ConfigureAwait(false);
        Installer.WriteWarnings(host, restored);
        var answers = record!.ToAnswers();
        if (answers.Missing().FirstOrDefault() is { } missing)
        {
            throw new InstallerRefusedException($"{missing} The backup's record leaves it out: its files are back; run sudo {Installer.CommandName} to choose it.");
        }

        return await Installer.InstallAsync(host, restored, answers, $"Restoring from {path}", new Installer.RunOptions(null, false, releaseFolder), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Never restore temporary access, even onto an empty replacement machine: refuses a backup holding pending kiosk commissioning.</summary>
    internal static void RefusePendingCommissioning(ReadBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var identityEntry = backup.Entries.FirstOrDefault(entry => entry.Path == ControllerLayout.System.IdentityFile);
        if (identityEntry?.Content is { } identityBytes)
        {
            using var identity = JsonDocument.Parse(identityBytes);
            if (identity.RootElement.TryGetProperty("kioskCredential", out var credential)
                && credential.ValueKind == JsonValueKind.Object && credential.TryGetProperty("state", out var state)
                && state.GetString() == "pending")
                throw new InstallerRefusedException("This backup contains pending kiosk commissioning. Temporary access cannot be restored; preserve it and use explicit administrator recovery.");
        }
    }

    private static string Counts(IEnumerable<StepChange> changes)
    {
        var list = changes.ToList();
        var parts = new List<string>();
        Add(StepChange.Create, "to write");
        Add(StepChange.Change, "to replace");
        Add(StepChange.Unchanged, "the same");
        return string.Join(", ", parts);

        void Add(StepChange change, string what)
        {
            if (list.Count(item => item == change) is var count and > 0)
            {
                parts.Add($"{count} {(count == 1 ? "file" : "files")} {what}");
            }
        }
    }

    private static StepChange ChangeFor(InstallerMachine machine, BackupEntry entry)
    {
        if (entry.Type == BackupEntry.Folder)
        {
            return machine.FileExists(entry.Path) ? StepChange.Blocked
                : !machine.DirectoryExists(entry.Path) ? StepChange.Create
                : machine.GetMode(entry.Path) == entry.FileMode ? StepChange.Unchanged
                : StepChange.Change;
        }

        if (machine.DirectoryExists(entry.Path))
        {
            return StepChange.Blocked;
        }

        if (machine.ReadBytes(entry.Path) is not { } current)
        {
            return StepChange.Create;
        }

        return Convert.ToHexStringLower(SHA256.HashData(current)) != entry.Sha256 ? StepChange.Change
            : machine.GetMode(entry.Path) == entry.FileMode ? StepChange.Unchanged
            : StepChange.Change;
    }

    // The folders first (parents before what is in them), then the files, each made with its mode from the start.
    private static void Put(InstallerMachine machine, InstallLog log, IReadOnlyList<(BackupEntry Entry, StepChange Change)> changes)
    {
        foreach (var (entry, change) in changes.Where(change => change.Entry.Type == BackupEntry.Folder).OrderBy(change => change.Entry.Path, StringComparer.Ordinal))
        {
            if (change == StepChange.Create)
            {
                machine.CreateDirectory(entry.Path, entry.FileMode);
                log.Write($"Made {entry.Path} ({entry.Mode}).");
            }
            else if (change == StepChange.Change)
            {
                machine.SetMode(entry.Path, entry.FileMode);
                log.Write($"Set {entry.Path} to {entry.Mode}.");
            }
        }

        foreach (var (entry, change) in changes.Where(change => change.Entry.Type == BackupEntry.File && change.Change != StepChange.Unchanged))
        {
            var parent = Path.GetDirectoryName(entry.Path)!;
            if (!machine.DirectoryExists(parent))
            {
                machine.CreateDirectory(parent, Modes.Folder);
            }

            machine.WriteAtomically(entry.Path, entry.Content!, entry.FileMode);
            log.Write($"{(change == StepChange.Create ? "Wrote" : "Replaced")} {entry.Path} ({entry.Mode}).");
        }
    }

    // Each to the owner the backup says, where that owner is here; the count of those that are not (yet).
    private static async Task<int> GiveOwnersAsync(InstallerMachine machine, InstallLog log, IReadOnlyList<BackupEntry> entries, CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var entry in entries.Where(entry => entry.Owner is not null))
        {
            if (await Ownership.GetAsync(machine, entry.Path, cancellationToken).ConfigureAwait(false) == entry.Owner)
            {
                continue;
            }

            var result = await machine.Commands.RunAsync(new CommandLine("chown", entry.Owner!, "--", entry.Path), cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                log.Write($"Gave {entry.Path} to {entry.Owner}.");
            }
            else
            {
                failed++;
                log.Write($"Could not give {entry.Path} to {entry.Owner} yet: {result.Reason}");
            }
        }

        return failed;
    }

    /// <summary>
    /// A backup read whole and checked: its manifest, its folders and files (with their content), and its record; and the
    /// SHA-256 of the archive those were read from (lower-case hex).
    /// </summary>
    internal sealed record ReadBackup(BackupManifest Manifest, IReadOnlyList<BackupEntry> Entries, InstallRecord? Record)
    {
        public string Sha256 { get; init; } = string.Empty;
    }

    /// <summary>A backup written: where, and the folders and files it holds, and what it left out.</summary>
    internal sealed record WrittenBackup(string Path, IReadOnlyList<BackupEntry> Entries, IReadOnlyList<string> Skipped);

    // The backup, read whole and checked before anything is written: only root may read it; its manifest comes first; it
    // holds only folders and files, each where a backup puts them, each as the manifest says (size and SHA-256).
    internal static async Task<ReadBackup> ReadAsync(InstallerMachine machine, string path, CancellationToken cancellationToken)
        => await ParseAsync(await ReadPrivateAsync(machine, path, cancellationToken).ConfigureAwait(false), path, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The archive at <paramref name="path"/>, read whole from one open file once that file is checked to be root's alone:
    /// the path is checked without following a link, then the file opened is checked again (through /proc, by its
    /// descriptor). Whoever can write the folder holding it could swap the path for another archive between a check and
    /// the open, or between two opens: so it is opened once, and what is hashed or read as a backup is what was read here
    /// (<see cref="ParseAsync"/>), never the path opened again. A file in <see cref="DefaultFolder"/> is read only while
    /// that folder is trusted (<see cref="FolderProblemAsync"/>).
    /// </summary>
    internal static async Task<byte[]> ReadPrivateAsync(InstallerMachine machine, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (Path.GetDirectoryName(path) == DefaultFolder && await FolderProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new InstallerRefusedException($"{problem}. Nothing was changed.");
        }

        if (await StatAsync(machine, path, cancellationToken).ConfigureAwait(false) is not var (kind, owner))
        {
            throw new InstallerRefusedException($"{path} is not there.");
        }

        if (!IsPrivate(kind, owner, machine.GetMode(path)))
        {
            throw NotPrivate(path, kind, owner, machine.GetMode(path));
        }

        var stream = machine.OpenRead(path) as FileStream ?? throw new InstallerRefusedException($"{path} is not there.");
        await using (stream.ConfigureAwait(false))
        {
            var opened = await StatAsync(machine, $"/proc/{Environment.ProcessId}/fd/{stream.SafeFileHandle.DangerousGetHandle()}", cancellationToken, follow: true).ConfigureAwait(false)
                ?? throw new InstallerRefusedException($"{path} went as it was opened.");
            var openedMode = File.GetUnixFileMode(stream.SafeFileHandle);
            if (!IsPrivate(opened.Kind, opened.Owner, openedMode))
            {
                throw NotPrivate(path, opened.Kind, opened.Owner, openedMode);
            }

            if (stream.Length > MaxBytes)
            {
                throw Unreadable(path, $"it is more than {MaxBytes / 1024 / 1024} MB");
            }

            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > MaxBytes)
                {
                    throw Unreadable(path, $"it is more than {MaxBytes / 1024 / 1024} MB");
                }

                copy.Write(buffer, 0, read);
            }

            return copy.ToArray();
        }
    }

    /// <summary>
    /// The backup in <paramref name="archive"/>, the bytes <see cref="ReadPrivateAsync"/> read from <paramref name="path"/>,
    /// read whole and checked, with their SHA-256: the archive checked and the one hashed are the same bytes.
    /// </summary>
    internal static async Task<ReadBackup> ParseAsync(byte[] archive, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var stream = new MemoryStream(archive, writable: false);
        var backup = await ReadArchiveAsync(stream, path, cancellationToken).ConfigureAwait(false);
        return backup with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(archive)) };
    }

    private static bool IsPrivate(string kind, string? owner, UnixFileMode? mode)
        => kind is ("regular file" or "regular empty file") && owner?.StartsWith("root:", StringComparison.Ordinal) == true && mode is (Modes.PrivateFile or Modes.OwnerReadOnly);

    private static InstallerRefusedException NotPrivate(string path, string kind, string? owner, UnixFileMode? mode)
        => new($"{path} must be a file only root reads (root's, 0600 or 0400): it holds the controller's secrets. It is {kind}, {owner ?? "an unknown owner"}'s, {(mode is { } found ? Modes.Octal(found) : "unknown")}. Fix that with sudo chown root: and sudo chmod 600.");

    // The archive in stream, which path names, read whole and checked.
    private static async Task<ReadBackup> ReadArchiveAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (stream.Length > MaxBytes)
            {
                throw Unreadable(path, $"it is more than {MaxBytes / 1024 / 1024} MB");
            }

            var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            await using (gzip.ConfigureAwait(false))
            {
                var tar = new TarReader(gzip);
                await using (tar.ConfigureAwait(false))
                {
                    return await ReadEntriesAsync(tar, path, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or FormatException or JsonException or ArgumentException)
        {
            throw Unreadable(path, error.Message);
        }
    }

    private static async Task<ReadBackup> ReadEntriesAsync(TarReader tar, string path, CancellationToken cancellationToken)
    {
        var first = await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
        if (first is not { EntryType: TarEntryType.RegularFile } || first.Name != ManifestName || first.Length > MaxManifestBytes)
        {
            throw Unreadable(path, $"its first entry is not {ManifestName}");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(await ContentAsync(first, cancellationToken).ConfigureAwait(false), InstallerJson.Options)
            ?? throw Unreadable(path, $"{ManifestName} is empty");
        if (manifest.Schema > CurrentSchema)
        {
            throw new InstallerRefusedException($"{path} was made by a newer installer (backup schema {manifest.Schema}; this one reads {CurrentSchema}): restore it with that release's installer.");
        }

        var listed = new Dictionary<string, BackupEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries ?? [])
        {
            if (!IsSafe(entry.Path) || !IsAllowed(entry.Path) || entry.Type is not (BackupEntry.File or BackupEntry.Folder) || !IsMode(entry.Mode) || !listed.TryAdd(entry.Path, entry))
            {
                throw Unreadable(path, $"{ManifestName} lists {entry.Path}, which a backup does not hold");
            }

            if (entry.Type == BackupEntry.File && (entry.Size is not >= 0 || entry.Sha256 is not { Length: 64 }))
            {
                throw Unreadable(path, $"{ManifestName} gives {entry.Path} no size or SHA-256");
            }
        }

        var absent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in manifest.Absent ?? [])
        {
            if (!StoreFiles.Contains(store, StringComparer.Ordinal) || listed.ContainsKey(store) || !absent.Add(store))
            {
                throw Unreadable(path, $"{ManifestName} names {store} as not there, which a backup does not say");
            }
        }

        // A file or folder's folder is in the backup too (so it goes back with its own mode), unless it is one the backup holds whole.
        foreach (var entry in listed.Values)
        {
            var parent = Path.GetDirectoryName(entry.Path)!;
            if (!Folders.Contains(entry.Path, StringComparer.Ordinal) && !Files.Contains(entry.Path, StringComparer.Ordinal) && !(listed.TryGetValue(parent, out var folder) && folder.Type == BackupEntry.Folder))
            {
                throw Unreadable(path, $"{ManifestName} lists {entry.Path} without its folder");
            }
        }

        var found = new Dictionary<string, BackupEntry>(StringComparer.Ordinal);
        long total = 0;
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } item)
        {
            var name = "/" + item.Name.TrimEnd('/');
            if (!listed.TryGetValue(name, out var entry) || found.ContainsKey(name))
            {
                throw Unreadable(path, $"it holds {item.Name}, which its manifest does not list");
            }

            if (entry.Type == BackupEntry.Folder)
            {
                if (item.EntryType != TarEntryType.Directory)
                {
                    throw Unreadable(path, $"{entry.Path} is not a folder in it");
                }

                found[name] = entry;
                continue;
            }

            if (item.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || item.Length != entry.Size)
            {
                throw Unreadable(path, $"{entry.Path} is not a file of {entry.Size} bytes in it");
            }

            total += item.Length;
            if (total > MaxBytes)
            {
                throw Unreadable(path, $"it holds more than {MaxBytes / 1024 / 1024} MB");
            }

            var content = await ContentAsync(item, cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(content)) != entry.Sha256)
            {
                throw Unreadable(path, $"{entry.Path} is not as its manifest says (its SHA-256 differs): the backup is damaged");
            }

            found[name] = entry with { Content = content };
        }

        if (listed.Keys.FirstOrDefault(listedPath => !found.ContainsKey(listedPath)) is { } lost)
        {
            throw Unreadable(path, $"{lost} is in its manifest but not in it");
        }

        var entries = listed.Keys.Select(key => found[key]).ToList();
        InstallRecord? record = null;
        if (found.TryGetValue(InstallPaths.SystemRecord, out var recordEntry))
        {
            try
            {
                record = InstallRecord.Parse(Encoding.UTF8.GetString(recordEntry.Content!));
            }
            catch (JsonException error)
            {
                throw new InstallerRefusedException($"The backup's install record cannot be read: {error.Message}");
            }
        }

        return new ReadBackup(manifest, entries, record);
    }

    private static async Task<byte[]> ContentAsync(TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.DataStream is null)
        {
            return [];
        }

        using var copy = new MemoryStream();
        await entry.DataStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    // An absolute path with nothing to resolve: no "." or "..", no doubled or trailing slash.
    private static bool IsSafe(string? path)
        => path is { Length: > 1 } && path[0] == '/' && !path.EndsWith('/') && !path.Contains('\0', StringComparison.Ordinal) && Path.GetFullPath(path) == path;

    private static bool IsMode(string? mode)
        => mode is { Length: 4 } && mode[0] == '0' && mode.Skip(1).All(digit => digit is >= '0' and <= '7');

    private static InstallerRefusedException Unreadable(string path, string why)
        => new($"{path} is not a backup the installer can restore: {why}.");
}

/// <summary>What a backup holds: <see cref="BackupCommands.ManifestName"/>, the archive's first entry.</summary>
/// <param name="Absent">
/// The stores' files (<see cref="BackupCommands.StoreFiles"/>) that were not there when it was made: null in a backup from
/// before it was kept. A file neither held nor named here is one the backup left out, not one never saved.
/// </param>
internal sealed record BackupManifest(int Schema, string Host, DateTimeOffset CreatedAt, string InstallerVersion, string? RecordVersion, IReadOnlyList<BackupEntry>? Entries, IReadOnlyList<string>? Absent = null);

/// <summary>A folder or file in a backup: its path on the machine, its mode and owner, and a file's size and SHA-256.</summary>
internal sealed record BackupEntry(string Path, string Type, string Mode, string? Owner, long? Size = null, string? Sha256 = null)
{
    public const string File = "file";

    public const string Folder = "folder";

    /// <summary>The file's bytes: in the archive, never in the manifest.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? Content { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public UnixFileMode FileMode => (UnixFileMode)(Convert.ToInt32(Mode, 8) & 0x1FF);
}
