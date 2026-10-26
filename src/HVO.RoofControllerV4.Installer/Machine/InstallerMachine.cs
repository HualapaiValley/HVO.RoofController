using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Installer.Machine;

/// <summary>The operating systems the installer supports.</summary>
public enum InstallerOs
{
    Linux,
    MacOS
}

/// <summary>
/// The machine the installer runs on. Every path the installer names is a path on this machine (<c>/etc/hvo-roof</c>);
/// <see cref="Root"/> is where that path is on disk: <c>/</c> on the real machine, a folder of its own in a test, so a
/// test can give the installer a whole fake Pi. Programs run through <see cref="Commands"/>, which a test fakes too.
/// </summary>
public sealed class InstallerMachine
{
    public required string Root { get; init; }

    public required ICommandRunner Commands { get; init; }

    public required InstallerOs Os { get; init; }

    public required Architecture Architecture { get; init; }

    /// <summary>True when the installer runs as root (with sudo).</summary>
    public required bool IsRoot { get; init; }

    /// <summary>The user the installer runs as.</summary>
    public required string UserName { get; init; }

    /// <summary>That user's home folder, as a path on this machine.</summary>
    public required string Home { get; init; }

    public required string HostName { get; init; }

    /// <summary>The installer's environment.</summary>
    public Func<string, string?> Environment { get; init; } = global::System.Environment.GetEnvironmentVariable;

    /// <summary>The folder the installer was started in: where the wizard offers to save answers.</summary>
    public string CurrentDirectory { get; init; } = "/";

    /// <summary>This machine, running its commands through <paramref name="commands"/> (one that logs them, for one).</summary>
    public InstallerMachine WithCommands(ICommandRunner commands) => new()
    {
        Root = Root,
        Commands = commands,
        Os = Os,
        Architecture = Architecture,
        IsRoot = IsRoot,
        UserName = UserName,
        Home = Home,
        HostName = HostName,
        Environment = Environment,
        CurrentDirectory = CurrentDirectory,
        IsPortInUse = IsPortInUse,
        NetworkAddresses = NetworkAddresses,
        ServedCertificateAsync = ServedCertificateAsync,
        DownloadTextAsync = DownloadTextAsync,
        DownloadToAsync = DownloadToAsync,
        FetchCaAsync = FetchCaAsync,
        FreeSpaceOnDisk = FreeSpaceOnDisk,
        ApiHandler = ApiHandler,
        TemporaryDirectory = TemporaryDirectory,
        RunProgramAsync = RunProgramAsync,
        SyncFolderOnDisk = SyncFolderOnDisk,
        SyncFileOnDisk = SyncFileOnDisk,
        RenameOnDisk = RenameOnDisk,
        OpenReadOnDisk = OpenReadOnDisk
    };

    /// <summary>
    /// Makes a folder's entries durable, given where the folder is on disk: a file renamed into it is still there after a
    /// power loss, not the one it replaced. Throws an <see cref="IOException"/> when the system does not say it is done.
    /// <see cref="SyncDirectory"/> calls it; a test fakes it.
    /// </summary>
    public Action<string> SyncFolderOnDisk { get; init; } = onDisk => SyncFolder(onDisk, OperatingSystem.IsMacOS(), Libc);

    /// <summary>
    /// Makes a file's content and metadata (its mode, its owner) durable, given where it is on disk, as
    /// <see cref="SyncFolderOnDisk"/> does a folder's entries: fsync(2) on Linux, F_FULLFSYNC on a Mac. Throws an
    /// <see cref="IOException"/> when the system does not say it is done. <see cref="SyncFile"/> calls it; a test fakes it.
    /// </summary>
    public Action<string> SyncFileOnDisk { get; init; } = onDisk => SyncFolder(onDisk, OperatingSystem.IsMacOS(), Libc, "file");

    /// <summary>
    /// Renames a file, given where both names are on disk, over a file there when told to: what
    /// <see cref="MoveFileDurably"/> renames with. A test fakes it, so that what it knows of the file (its owner) follows it.
    /// </summary>
    public Action<string, string, bool> RenameOnDisk { get; init; } = File.Move;

    /// <summary>
    /// Opens a file for reading, given where it is on disk. <see cref="OpenRead"/> calls it; a test can change an
    /// untrusted source just before a consumer opens its verified copy.
    /// </summary>
    public Func<string, Stream> OpenReadOnDisk { get; init; } = onDisk =>
        new FileStream(onDisk, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

    /// <summary>
    /// True when something on this machine accepts connections on the TCP port: it answers on the loopback address
    /// within a second.
    /// </summary>
    public Func<int, bool> IsPortInUse { get; init; } = AnswersOnLoopback;

    /// <summary>The machine's addresses on the interfaces that are up, with each interface's name (the loopback's left out).</summary>
    public Func<IReadOnlyList<NetworkAddress>> NetworkAddresses { get; init; } = CurrentAddresses;

    /// <summary>
    /// The certificate presented over TLS on the TCP port, on the loopback address, or null when nothing there answers
    /// with TLS within a few seconds. Nothing is sent but the handshake.
    /// </summary>
    public Func<int, CancellationToken, Task<X509Certificate2?>> ServedCertificateAsync { get; init; } = PresentedOnLoopbackAsync;

    /// <summary>
    /// Downloads a small text file over HTTPS (release.json): its text, or an <see cref="HttpRequestException"/> saying
    /// why not. Redirects are followed; anything over a megabyte is refused.
    /// </summary>
    public Func<Uri, CancellationToken, Task<string>> DownloadTextAsync { get; init; } = DownloadAsync;

    /// <summary>
    /// Downloads a file over HTTPS (a release's kiosk) into the stream, or throws an <see cref="HttpRequestException"/>
    /// saying why not, or an <see cref="InvalidDataException"/> when it is larger than the most given. Redirects are
    /// followed.
    /// </summary>
    public Func<Uri, Stream, long, CancellationToken, Task> DownloadToAsync { get; init; } = DownloadStreamAsync;

    /// <summary>
    /// The CA a controller serves at <c>GET /ca.crt</c>, checked against the certificate it presents (see
    /// <see cref="RoofCertificateAuthority.FetchAsync"/>): what hvo-roof and the Mac app trust once its fingerprint is the
    /// one the person gave.
    /// </summary>
    public Func<Uri, CancellationToken, Task<X509Certificate2>> FetchCaAsync { get; init; }
        = (controller, cancellationToken) => RoofCertificateAuthority.FetchAsync(controller, TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>
    /// The innermost handler for the controller's API, or null for the client's own. A test puts a fake controller
    /// here; the installer never changes how the certificate is checked.
    /// </summary>
    public Func<HttpMessageHandler>? ApiHandler { get; init; }

    /// <summary>The folder for the installer's temporary files, as a path on this machine.</summary>
    public string TemporaryDirectory { get; init; } = Path.GetTempPath();

    /// <summary>
    /// The bytes free to the installer on the file system a folder is on, given where the folder is on disk; null when the
    /// system does not say. <see cref="FreeSpace"/> calls it; a test fakes it.
    /// </summary>
    public Func<string, long?> FreeSpaceOnDisk { get; init; } = AvailableBytes;

    /// <summary>The bytes free to the installer on the file system <paramref name="path"/> (a folder) is on, or null when the system does not say.</summary>
    public long? FreeSpace(string path) => FreeSpaceOnDisk(OnDisk(path));

    /// <summary>
    /// Runs a program (a path on this machine) with the arguments and the variables added to the environment, on this
    /// terminal, and gives its exit code once it ends: <c>upgrade</c> hands over to the new release's installer this way.
    /// The program gets a Ctrl-C itself, so it is waited for, never killed.
    /// </summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Task<int>> RunProgramAsync { get; init; } = RunOnTerminalAsync;

    /// <summary>The platform as release assets name it: linux-arm64, linux-x64 or osx-arm64 (or another the installer refuses).</summary>
    public string RuntimeIdentifier => $"{(Os == InstallerOs.MacOS ? "osx" : "linux")}-{Architecture.ToString().ToLowerInvariant()}";

    /// <summary>Where <paramref name="path"/>, a path on this machine, is on disk.</summary>
    public string OnDisk(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));
        }

        return Root == "/" ? path : Path.Join(Root, path);
    }

    public bool FileExists(string path) => File.Exists(OnDisk(path));

    public bool DirectoryExists(string path) => Directory.Exists(OnDisk(path));

    /// <summary>The file's text, or null when there is no such file.</summary>
    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(OnDisk(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file's bytes, or null when there is no such file.</summary>
    public byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(OnDisk(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file's bytes, or null when there is no such file, read only when it is a regular file: something else there (a
    /// FIFO, a device, a socket, a folder) throws <see cref="NotARegularFileException"/> before anything is read, and
    /// opening it never waits (<see cref="RegularFiles"/>).
    /// </summary>
    public byte[]? ReadRegularBytes(string path) => RegularFiles.Read(OnDisk(path), path);

    /// <summary>The file's text, as <see cref="ReadRegularBytes"/> reads it (without a byte order mark), or null when there is no such file.</summary>
    public string? ReadRegularText(string path)
    {
        if (ReadRegularBytes(path) is not { } bytes)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The file's or folder's permission bits, or null when it does not exist.</summary>
    public UnixFileMode? GetMode(string path)
    {
        var onDisk = OnDisk(path);
        return File.Exists(onDisk) || Directory.Exists(onDisk) ? File.GetUnixFileMode(onDisk) : null;
    }

    /// <summary>When the file was last written, or null when there is no such file (or it cannot be seen).</summary>
    public DateTimeOffset? LastWritten(string path)
    {
        var onDisk = OnDisk(path);
        return File.Exists(onDisk) ? new DateTimeOffset(File.GetLastWriteTimeUtc(onDisk), TimeSpan.Zero) : null;
    }

    /// <summary>The files in the folder (not in the folders within it), as paths on this machine; none when it is not there.</summary>
    public IReadOnlyList<string> ListFiles(string path)
    {
        var onDisk = OnDisk(path);
        return Directory.Exists(onDisk)
            ? [.. Directory.EnumerateFiles(onDisk).Select(file => Path.Join(path, Path.GetFileName(file))).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The names of the entries in the folder (files, folders and links), sorted; none when it is not there.</summary>
    public IReadOnlyList<string> ListNames(string path)
    {
        var onDisk = OnDisk(path);
        return Directory.Exists(onDisk)
            ? [.. Directory.EnumerateFileSystemEntries(onDisk).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="path"/>, a new file only its owner may read, of no more than
    /// <paramref name="maxBytes"/>: a download that fails leaves no file.
    /// </summary>
    public async Task DownloadFileAsync(Uri uri, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var onDisk = OnDisk(path);
        var stream = new FileStream(onDisk, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = Modes.PrivateFile
        });
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await DownloadToAsync(uri, stream, maxBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(onDisk);
            throw;
        }
    }

    /// <summary>Deletes the folder and everything in it; nothing when it is not there.</summary>
    public void DeleteDirectory(string path)
    {
        var onDisk = OnDisk(path);
        if (Directory.Exists(onDisk))
        {
            Directory.Delete(onDisk, recursive: true);
        }
    }

    /// <summary>Deletes the folder when nothing is in it; leaves it, and whatever is in it, otherwise.</summary>
    public void DeleteDirectoryIfEmpty(string path)
    {
        var onDisk = OnDisk(path);
        if (Directory.Exists(onDisk) && !Directory.EnumerateFileSystemEntries(onDisk).Any())
        {
            try
            {
                Directory.Delete(onDisk, recursive: false);
            }
            catch (IOException)
            {
                // Something was put in it meanwhile: it stays.
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is a folder itself, not a symbolic link (to a folder or anything else).</summary>
    public bool IsFolderNotLink(string path) => Inspect(OnDisk(path)) is { IsFolder: true, IsLink: false };

    /// <summary>
    /// Resolves every symbolic link in an existing folder's path and returns its physical path on this machine.
    /// Throws when the folder cannot be resolved; does not check ownership or prevent concurrent path replacement.
    /// </summary>
    public string ResolveFolder(string path)
    {
        var onDisk = RealPath(OnDisk(path));
        if (onDisk is null || !Directory.Exists(onDisk))
        {
            throw new InstallerRefusedException($"{path} cannot be resolved to a folder: check the path and its permissions, then run it again.");
        }

        return OnMachine(onDisk);
    }

    public void SetMode(string path, UnixFileMode mode) => File.SetUnixFileMode(OnDisk(path), mode);

    /// <summary>
    /// Resolves every symbolic link in <paramref name="path"/>, then checks the physical folder and its ancestors
    /// upward to /. At inspection each must be a directory, not a link, owned by root or the effective user, without
    /// group/other write bits unless sticky (as /tmp is). Returns that physical path on this machine, or throws
    /// <see cref="InstallerException"/> naming a failed check. Links in the supplied spelling are resolved, not refused.
    /// These are ordinary misconfiguration checks under the single-user-host decision of 2026-10-17 (#147;
    /// docs/install.md, Temporary files): root and the deploying account are assumed to control the path chain, with
    /// no hostile local account expected. They neither inspect macOS ACLs nor prevent concurrent ancestor replacement;
    /// no directory descriptors are held to preserve the checked chain.
    /// </summary>
    public string TrustedFolder(string path)
    {
        var user = geteuid();
        var who = user == 0 ? "root" : "root and you";
        var onDisk = RealPath(OnDisk(path))
            ?? throw new InstallerException($"The installer's temporary folder {path} is not there. Set TMPDIR to a folder only {who} can change, such as /tmp, then run the installer again.");
        for (var folder = onDisk; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Untrusted(folder, user) is { } problem)
            {
                throw new InstallerException(
                    $"The installer's temporary folder {path} is not one only {who} can change: {OnMachine(folder)} {problem}, so someone else could change "
                    + $"what the installer writes and checks there (the copy of an image archive that Docker loads, the deploy script) before it is used. Set TMPDIR to a folder only {who} can change, such as /tmp, then run the installer again.");
            }
        }

        return OnMachine(onDisk);
    }

    /// <summary>
    /// Makes the new folder <paramref name="path"/> with mode 0700. Nothing may be there yet, not even a link, and the
    /// folder made must then be a real folder of the installer's own user with that mode. Throws
    /// <see cref="InstallerException"/> otherwise.
    /// </summary>
    public void CreatePrivateDirectory(string path)
    {
        var onDisk = OnDisk(path);
        if (mkdir(onDisk, PrivateFolderBits) != 0)
        {
            throw new InstallerException($"The installer could not make its folder {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}.");
        }

        // The umask may have taken bits off.
        File.SetUnixFileMode(onDisk, Modes.PrivateFolder);
        if (!IsPrivateFolder(onDisk))
        {
            throw new InstallerException($"The installer's folder {path}, just made, is not a folder of its own with mode 0700.");
        }
    }

    /// <summary>
    /// Recursively deletes <paramref name="path"/> when its current entry is a non-link directory owned by the effective
    /// user with mode 0700; otherwise leaves it alone. This checks the saved path, owner and mode, not device/inode
    /// identity or a held handle. A same-owner 0700 replacement also passes and can be deleted (accepted F9, #147).
    /// </summary>
    public void DeletePrivateDirectory(string path)
    {
        var onDisk = OnDisk(path);
        if (IsPrivateFolder(onDisk))
        {
            Directory.Delete(onDisk, recursive: true);
        }
    }

    // Where a folder, given where it is on disk, is on this machine: its path less the machine's root (a test's folder),
    // or as it is when it is above that root.
    private string OnMachine(string onDisk)
    {
        if (Root == "/")
        {
            return onDisk;
        }

        var root = RealPath(Root) ?? Root;
        return onDisk == root ? "/" : onDisk.StartsWith(root + "/", StringComparison.Ordinal) ? onDisk[root.Length..] : onDisk;
    }

    // Current type, owner and Unix mode only; not the identity of the directory originally created or its ACL access.
    private static bool IsPrivateFolder(string onDisk)
        => Inspect(onDisk) is { IsFolder: true, IsLink: false } folder && folder.Owner == geteuid() && (folder.Mode & 0xFFF) == PrivateFolderBits;

    // A failure of the type, owner or Unix-mode checks at this path; null when they pass. ACLs and concurrent path-chain
    // replacement are outside these checks (accepted F7/F8, #147).
    private static string? Untrusted(string onDisk, uint user)
    {
        if (Inspect(onDisk) is not { } folder)
        {
            return "cannot be examined";
        }

        if (folder.IsLink)
        {
            return "is a symbolic link";
        }

        if (!folder.IsFolder)
        {
            return "is not a folder";
        }

        if (folder.Owner != 0 && folder.Owner != user)
        {
            return $"belongs to user {folder.Owner}";
        }

        return (folder.Mode & OthersMayWrite) != 0 && (folder.Mode & Sticky) == 0
            ? $"can be written to by others (mode {Convert.ToString(folder.Mode & 0xFFF, 8).PadLeft(4, '0')}) and is not sticky"
            : null;
    }

    // What lstat(2) says of the file, given where it is on disk, without following it when it is a link: its type,
    // owner and mode; null when it cannot. Linux's statx(2) has the same layout on every architecture: stx_mask at 0,
    // stx_uid at 20 and stx_mode (16 bits) at 28. macOS's struct stat on arm64 (its only architecture here) has st_mode
    // (16 bits) at 4 and st_uid at 16.
    private static Entry? Inspect(string onDisk)
    {
        var buffer = new byte[256];
        ushort mode;
        uint owner;
        if (OperatingSystem.IsMacOS())
        {
            if (lstat(onDisk, buffer) != 0)
            {
                return null;
            }

            mode = BitConverter.ToUInt16(buffer, 4);
            owner = BitConverter.ToUInt32(buffer, 16);
        }
        else
        {
            if (statx(CurrentFolder, onDisk, NoFollow, StatxWanted, buffer) != 0 || (BitConverter.ToUInt32(buffer, 0) & StatxWanted) != StatxWanted)
            {
                return null;
            }

            mode = BitConverter.ToUInt16(buffer, 28);
            owner = BitConverter.ToUInt32(buffer, 20);
        }

        var type = mode & FileType;
        return new Entry(type == FolderType, type == LinkType, owner, mode & 0xFFF);
    }

    // realpath(3): the path, given where it is on disk, with every link followed; null when it cannot be (one is missing).
    private static string? RealPath(string onDisk)
    {
        var resolved = realpath(onDisk, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    private sealed record Entry(bool IsFolder, bool IsLink, uint Owner, int Mode);

    /// <summary>Makes the folder, and any parent missing, with <paramref name="mode"/> (the parents get 0755).</summary>
    public void CreateDirectory(string path, UnixFileMode mode)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !DirectoryExists(parent))
        {
            CreateDirectory(parent, Modes.Folder);
        }

        Directory.CreateDirectory(OnDisk(path), mode);

        // The umask may have taken bits off.
        SetMode(path, mode);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> in one step: to a new file beside it, made with
    /// <paramref name="mode"/> from the start (so a secret is never readable by others, even briefly), then renamed over
    /// the old one. Someone reading the file sees the old content or the new, never part of it.
    /// </summary>
    public void WriteAtomically(string path, byte[] content, UnixFileMode mode)
    {
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode
            }))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void WriteAtomically(string path, string content, UnixFileMode mode)
        => WriteAtomically(path, Encoding.UTF8.GetBytes(content), mode);

    /// <summary>
    /// Writes <paramref name="path"/> in one step, as <see cref="WriteAtomically(string, string, UnixFileMode)"/> does, then
    /// syncs its folder (<see cref="SyncDirectory"/>), so the new content, not the old, is there after a power loss: for
    /// what the changes after it rely on, the install record. Throws an <see cref="IOException"/> when that cannot be made
    /// sure of; the file may then have either content.
    /// </summary>
    public void WriteDurably(string path, string content, UnixFileMode mode)
    {
        WriteAtomically(path, content, mode);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Makes the folder's entries durable (<see cref="SyncFolderOnDisk"/>): a file renamed into it stays there after a power
    /// loss. A file's own flush does not do this; the rename is the folder's.
    /// </summary>
    public void SyncDirectory(string path) => SyncFolderOnDisk(OnDisk(path));

    /// <summary>
    /// Makes the file's content and metadata (its mode, its owner) durable (<see cref="SyncFileOnDisk"/>), as a change made
    /// after the content was flushed (a chmod, a chown) needs before its name is relied on.
    /// </summary>
    public void SyncFile(string path) => SyncFileOnDisk(OnDisk(path));

    // The durable changes. Each makes what it changes durable before it returns: the file itself (content, mode, owner)
    // before the folder entry that names it, and each folder entry once it is made. When a sync fails, it throws an
    // IOException and goes no further. For what a later step relies on after a power loss: the backup an upgrade's record
    // names, and the stores a rollback puts back before the controller before starts on them.

    /// <summary>
    /// Writes <paramref name="path"/> in one step (<see cref="WriteAtomically(string, byte[], UnixFileMode)"/>), then makes
    /// it durable: the file synced, then its folder. When a sync fails it throws, and the file may have either content.
    /// </summary>
    public void WriteDurably(string path, byte[] content, UnixFileMode mode)
    {
        WriteAtomically(path, content, mode);
        SyncFile(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/>, over a file there only with
    /// <paramref name="overwrite"/>, and makes it durable: the file synced first (so its name never points at content or an
    /// owner that is not on disk), then the destination's folder, then the source's when it is another. A new name
    /// (without <paramref name="overwrite"/>) that cannot be made durable is taken away again before it throws, so nothing
    /// is left there that a later step could take for durable.
    /// </summary>
    public void MoveFileDurably(string source, string destination, bool overwrite = false)
    {
        SyncFile(source);
        RenameOnDisk(OnDisk(source), OnDisk(destination), overwrite);
        var folder = Path.GetDirectoryName(destination)!;
        try
        {
            SyncDirectory(folder);
            if (Path.GetDirectoryName(source) is { } from && from != folder)
            {
                SyncDirectory(from);
            }
        }
        catch (IOException) when (!overwrite)
        {
            File.Delete(OnDisk(destination));
            throw;
        }
    }

    /// <summary>Deletes the file and makes that durable: its folder synced. Its folder is synced even when it was not there.</summary>
    public void DeleteFileDurably(string path)
    {
        DeleteFile(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Makes the folder, and any parent missing (0755), as <see cref="CreateDirectory"/> does, and makes each folder made
    /// durable: the folder above the first one made is synced, then each one made, from the top down. When a sync fails it
    /// takes away again, deepest first, each folder it made that is still empty, then throws. Nothing to make, nothing synced.
    /// </summary>
    public void CreateDirectoryDurably(string path, UnixFileMode mode)
    {
        var made = new List<string>();
        for (var folder = path; !DirectoryExists(folder); folder = Path.GetDirectoryName(folder)!)
        {
            made.Insert(0, folder);
        }

        if (made.Count == 0)
        {
            return;
        }

        CreateDirectory(path, mode);
        try
        {
            SyncDirectory(Path.GetDirectoryName(made[0])!);
            foreach (var folder in made)
            {
                SyncDirectory(folder);
            }
        }
        catch (IOException)
        {
            // Best effort: what failed is the sync, and that is what is thrown.
            foreach (var folder in Enumerable.Reverse(made).Where(folder => DirectoryExists(folder) && ListNames(folder).Count == 0))
            {
                try
                {
                    Directory.Delete(OnDisk(folder));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Replaces a file's text in one step, through a new file renamed over it, and sets no mode: for a file system that
    /// keeps none (the Pi's FAT boot partition, where setting one fails).
    /// </summary>
    public void ReplaceText(string path, string content)
    {
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> in one step, as <see cref="WriteAtomically(string, byte[], UnixFileMode)"/> does,
    /// with what <paramref name="write"/> puts in the stream it is given: when it throws, the old file stays as it was.
    /// </summary>
    public async Task WriteAtomicallyAsync(string path, Func<Stream, CancellationToken, Task> write, UnixFileMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode
            });
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> in one step, with <paramref name="mode"/>.</summary>
    public Task CopyFileAsync(string source, string destination, UnixFileMode mode, CancellationToken cancellationToken)
        => WriteAtomicallyAsync(
            destination,
            async (stream, token) =>
            {
                var from = OpenRead(source) ?? throw new FileNotFoundException($"{source} is not there.", source);
                await using (from.ConfigureAwait(false))
                {
                    await from.CopyToAsync(stream, token).ConfigureAwait(false);
                }
            },
            mode,
            cancellationToken);

    /// <summary>The file, open to read, or null when there is no such file.</summary>
    public Stream? OpenRead(string path)
    {
        try
        {
            return OpenReadOnDisk(OnDisk(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file's SHA-256, in lower-case hex, or null when there is no such file.</summary>
    public async Task<string?> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = OpenRead(path);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Renames <paramref name="source"/> over <paramref name="destination"/>, in one step (or only to where nothing is, without <paramref name="overwrite"/>).</summary>
    public void MoveFile(string source, string destination, bool overwrite = true) => File.Move(OnDisk(source), OnDisk(destination), overwrite);

    /// <summary>
    /// A new file at <paramref name="path"/>, open to write, made with <paramref name="mode"/> from the start: it fails
    /// when there is a file there already.
    /// </summary>
    public FileStream CreateNew(string path, UnixFileMode mode) => new(OnDisk(path), new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        UnixCreateMode = mode,
        Options = FileOptions.Asynchronous
    });

    /// <summary>Renames the folder <paramref name="source"/> to <paramref name="destination"/>, which must not be there.</summary>
    public void MoveDirectory(string source, string destination) => Directory.Move(OnDisk(source), OnDisk(destination));

    /// <summary>True when the installer may make files in the folder (or change the file): the system says it may write there.</summary>
    public bool IsWritable(string path) => access(OnDisk(path), WriteAccess) == 0;

    /// <summary>Deletes the file; nothing when it is not there.</summary>
    public void DeleteFile(string path) => File.Delete(OnDisk(path));

    /// <summary>Adds a line to the end of the file, making the file with <paramref name="mode"/> when it is not there.</summary>
    public void AppendLine(string path, string line, UnixFileMode mode)
    {
        using var stream = new FileStream(OnDisk(path), new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            UnixCreateMode = mode
        });
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
    }

    /// <summary>The machine the installer is running on.</summary>
    public static InstallerMachine Current()
    {
        var root = geteuid() == 0;
        var user = global::System.Environment.UserName;
        var home = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);
        return new InstallerMachine
        {
            Root = "/",
            Commands = new ProcessCommandRunner(),
            Os = OperatingSystem.IsMacOS() ? InstallerOs.MacOS : InstallerOs.Linux,
            Architecture = RuntimeInformation.OSArchitecture,
            IsRoot = root,
            UserName = string.IsNullOrEmpty(user) ? (root ? "root" : "unknown") : user,
            Home = string.IsNullOrEmpty(home) ? "/" : home,
            HostName = System.Net.Dns.GetHostName(),
            CurrentDirectory = global::System.Environment.CurrentDirectory
        };
    }

    private static async Task<int> RunOnTerminalAsync(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var start = new global::System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = global::System.Diagnostics.Process.Start(start) ?? throw new InstallerException($"Could not start {program}.");
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<string> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = limit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hvo-roof-install");
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadStreamAsync(Uri uri, Stream destination, long limit, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hvo-roof-install");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is > 0 and var length && length > limit)
        {
            throw new InvalidDataException($"It is {length} bytes, more than the {limit} expected.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException($"It is more than the {limit} bytes expected.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static long? AvailableBytes(string onDisk)
    {
        try
        {
            return new DriveInfo(onDisk).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool AnswersOnLoopback(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static IReadOnlyList<NetworkAddress> CurrentAddresses()
    {
        var passing = PassingAddresses();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown
                && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(unicast => new NetworkAddress(network.Name, unicast.Address)
            {
                Temporary = passing.Contains((network.Name, unicast.Address))
            }))
            .ToArray();
    }

    // Linux says which IPv6 addresses are temporary or deprecated in /proc/net/if_inet6; .NET does not. macOS has no such
    // file, and its addresses count as lasting.
    private static IReadOnlySet<(string Interface, IPAddress Address)> PassingAddresses()
    {
        try
        {
            return File.Exists("/proc/net/if_inet6") ? NetworkAddress.PassingIn(File.ReadAllText("/proc/net/if_inet6")) : new HashSet<(string, IPAddress)>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HashSet<(string, IPAddress)>();
        }
    }

    private static async Task<X509Certificate2?> PresentedOnLoopbackAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        X509Certificate2? presented = null;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            // Only read: whatever it presents is accepted, and nothing is sent over the connection.
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        presented ??= certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                        return true;
                    }
                },
                timeout.Token).ConfigureAwait(false);
            return presented;
        }
        catch (Exception error) when (error is SocketException or IOException or System.Security.Authentication.AuthenticationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            presented?.Dispose();
            return null;
        }
    }

    // Opens the folder (or the file, as what says) to read, then makes its entries (a file's content and metadata) durable:
    // fsync(2) on Linux. macOS's fsync can leave them in the drive's cache, where a power loss can lose them, so there only
    // F_FULLFSYNC will do, which has the drive write its cache out. When it fails, for whatever reason (a file system that does not support it among them), the sync fails:
    // fsync is never tried in its place. Anything that fails throws. libc's calls are given, so a test can fail them.
    internal static void SyncFolder(string onDisk, bool macOS, FolderSyncCalls calls, string what = "folder")
    {
        var descriptor = Retried(() => calls.Open(onDisk, macOS ? MacCloseOnExec : LinuxCloseOnExec), out var error);
        if (descriptor < 0)
        {
            throw SyncFailed(onDisk, "open", error, what);
        }

        try
        {
            if (macOS)
            {
                if (Retried(() => calls.FullFsync(descriptor), out error) != 0)
                {
                    throw FullSyncFailed(onDisk, error, what);
                }
            }
            else if (Retried(() => calls.Fsync(descriptor), out error) != 0)
            {
                throw SyncFailed(onDisk, "fsync", error, what);
            }
        }
        finally
        {
            _ = calls.Close(descriptor);
        }
    }

    // A call into libc, again while a signal interrupts it, with its errno when it fails.
    private static int Retried(Func<int> call, out int error)
    {
        int result;
        do
        {
            result = call();
            error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (result < 0 && error == Interrupted);

        return result;
    }

    private static IOException SyncFailed(string path, string call, int error, string what)
        => new($"Could not sync the {what} {path} to disk ({call}: {Marshal.GetPInvokeErrorMessage(error)}).");

    // F_FULLFSYNC's failure; ENOTSUP or EINVAL says the file system does not support it.
    private static IOException FullSyncFailed(string path, int error, string what)
        => error is MacNotSupported or InvalidArgument
            ? new($"Could not sync the {what} {path} to disk (F_FULLFSYNC: {Marshal.GetPInvokeErrorMessage(error)}): its file system "
                + $"cannot have the drive write its cache out, which a Mac needs for a change to survive a power loss. Keep the {what} "
                + "on a file system that can, as APFS on the Mac's own drive does.")
            : SyncFailed(path, "F_FULLFSYNC", error, what);

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    // W_OK, the same on Linux and macOS.
    private const int WriteAccess = 2;

    // EINTR, the same on Linux and macOS.
    private const int Interrupted = 4;

    // EINVAL, the same on Linux and macOS.
    private const int InvalidArgument = 22;

    // O_RDONLY (0) with O_CLOEXEC, which differs between them; F_FULLFSYNC and its ENOTSUP are macOS's alone here.
    private const int LinuxCloseOnExec = 0x80000;
    private const int MacCloseOnExec = 0x1000000;
    private const int FullFsync = 51;
    private const int MacNotSupported = 45;

    // libc's own calls, for SyncFolder.
    private static readonly FolderSyncCalls Libc = new(open, descriptor => fcntl(descriptor, FullFsync), fsync, close);

    // A file's type and mode bits (S_IFMT, S_IFDIR, S_IFLNK, S_ISVTX, S_IWGRP | S_IWOTH), the same on Linux and macOS.
    private const int FileType = 0xF000;
    private const int FolderType = 0x4000;
    private const int LinkType = 0xA000;
    private const int Sticky = 0x200;
    private const int OthersMayWrite = 0x12;

    // 0700.
    private const int PrivateFolderBits = 0x1C0;

    // statx(2): AT_FDCWD, AT_SYMLINK_NOFOLLOW, and STATX_TYPE | STATX_MODE | STATX_UID.
    private const int CurrentFolder = -100;
    private const int NoFollow = 0x100;
    private const uint StatxWanted = 0x1 | 0x2 | 0x8;

    [DllImport("libc", SetLastError = false, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int access(string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int mkdir(string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int statx(int folder, string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int lstat(string path, [Out] byte[] buffer);
}

/// <summary>
/// The libc calls that sync a folder to disk (<see cref="InstallerMachine.SyncFolderOnDisk"/>): open(2), with the flags
/// given; fcntl(2) with F_FULLFSYNC; fsync(2); and close(2). Each answers as libc does, -1 when it fails, with errno as
/// the last P/Invoke error (<see cref="Marshal.GetLastPInvokeError"/>). A test fakes them.
/// </summary>
internal sealed record FolderSyncCalls(Func<string, int, int> Open, Func<int, int> FullFsync, Func<int, int> Fsync, Func<int, int> Close);

/// <summary>An address of the machine, and the network interface it is on (eth0, wlan0, docker0).</summary>
public sealed record NetworkAddress(string Interface, IPAddress Address)
{
    // The kernel's address flags (linux/if_addr.h): a temporary (privacy) address, and one that is on its way out.
    private const int TemporaryFlag = 0x01;
    private const int DeprecatedFlag = 0x20;

    /// <summary>
    /// A temporary (privacy) or deprecated IPv6 address: the machine drops it within a day or so, so a certificate that
    /// named it would name an address the controller no longer has.
    /// </summary>
    public bool Temporary { get; init; }

    /// <summary>The temporary and deprecated addresses in <c>/proc/net/if_inet6</c>'s text, with their interfaces.</summary>
    public static IReadOnlySet<(string Interface, IPAddress Address)> PassingIn(string ifInet6)
    {
        ArgumentNullException.ThrowIfNull(ifInet6);
        var passing = new HashSet<(string, IPAddress)>();

        // Each line: the address in 32 hex digits, the interface's index, the prefix length, the scope, the flags, the name.
        foreach (var line in ifInet6.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields is [var hex, _, _, _, var flags, var name]
                && hex.Length == 32
                && int.TryParse(flags, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && (value & (TemporaryFlag | DeprecatedFlag)) != 0)
            {
                try
                {
                    passing.Add((name, new IPAddress(Convert.FromHexString(hex))));
                }
                catch (FormatException)
                {
                }
            }
        }

        return passing;
    }
}

/// <summary>The permissions the installer gives what it makes.</summary>
public static class Modes
{
    /// <summary>0755: a folder anyone may read.</summary>
    public const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>0750: a folder its owner and group may read.</summary>
    public const UnixFileMode GroupFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    /// <summary>0700: a folder only its owner may open.</summary>
    public const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>0644: a file anyone may read.</summary>
    public const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>0640: a file its owner and group may read.</summary>
    public const UnixFileMode GroupFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>0600: a file only its owner may read.</summary>
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>0400: a file only its owner may read, and nobody writes.</summary>
    public const UnixFileMode OwnerReadOnly = UnixFileMode.UserRead;

    /// <summary>0755: a program.</summary>
    public const UnixFileMode Program = Folder;

    /// <summary>The mode as <c>ls</c> and <c>chmod</c> write it: 0755.</summary>
    public static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');
}
