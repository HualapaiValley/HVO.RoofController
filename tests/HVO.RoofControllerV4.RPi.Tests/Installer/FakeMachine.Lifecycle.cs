using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// What upgrade, rollback, uninstall, backup and restore need of the machine: other releases on GitHub, the program an
/// upgrade hands over to, and Docker's, systemd's and stat's answers for taking things away.
/// </summary>
internal sealed partial class FakeMachine
{
    private TextWriter? _output;
    private TextWriter? _error;

    /// <summary>The release whose installer <see cref="RunAsync(string[])"/> runs: the one installed, after an upgrade.</summary>
    public string InstallerVersion { get; set; } = "4.0.0";

    /// <summary>True when <see cref="RunAsync(string[])"/> runs on a terminal, where the installer can ask its questions.</summary>
    public bool Interactive { get; set; }

    /// <summary>The options of each run of the deploy script, in order: empty for a deploy, --stop or --rollback.</summary>
    public List<string> DeployOptions { get; } = [];

    /// <summary>The images <c>docker image rm</c> removed, in order.</summary>
    public List<string> RemovedImages { get; } = [];

    /// <summary>Each program the installer ran on the terminal (an upgrade's hand-over): the file, its arguments and its environment.</summary>
    public List<(string Path, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)> Launches { get; } = [];

    /// <summary>
    /// What a program the installer runs on the terminal does; by default, an installer the fake release made runs as
    /// that release's installer, on this machine and terminal, with the environment it was given.
    /// </summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Task<int>>? Launch { get; set; }

    /// <summary>
    /// What happens just after stat answers for a path without following a link (the backup's check of a file or
    /// folder), as someone else on the machine might do it then: by path.
    /// </summary>
    public Dictionary<string, Action> AfterStat { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What happens just before stat answers, for any path or open file: someone else on the machine acting between two
    /// of the installer's checks.
    /// </summary>
    public Action? BeforeStat { get; set; }

    /// <summary>
    /// What happens just after stat answers for a file the installer has open (by its descriptor, through /proc): someone
    /// else on the machine acting once it is open, before it is read.
    /// </summary>
    public Action? AfterStatOpened { get; set; }

    /// <summary>
    /// What happens as the installer prints a line to its output (<see cref="RunAsync(string[])"/>): someone else on the
    /// machine acting at that moment, as when a plan has been shown and is about to be carried out.
    /// </summary>
    public Action<string>? OnOutputLine { get; set; }

    /// <summary>The installer's output, as a terminal shows it, telling <see cref="OnOutputLine"/> of each line written whole.</summary>
    private sealed class LineWriter(Action<string> written) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            written(value ?? string.Empty);
        }
    }

    /// <summary>Each folder the installer synced to disk (InstallerMachine.SyncDirectory), in order, as a path on the machine.</summary>
    public List<string> DirectorySyncs { get; } = [];

    /// <summary>Why syncing a folder fails (an IOException's message); null for a sync that succeeds.</summary>
    public string? DirectorySyncFailure { get; set; }

    /// <summary>What happens as a folder is synced, before the sync: a test looks at the machine then.</summary>
    public Action<string>? DuringDirectorySync { get; set; }

    /// <summary>The errno F_FULLFSYNC fails with on a Mac (fcntl(2)) as the installer syncs a folder; null when it succeeds.</summary>
    public int? FullFsyncError { get; set; }

    /// <summary>Each libc call the installer made to sync a folder, in order: open, F_FULLFSYNC, fsync and close.</summary>
    public List<string> SyncCalls { get; } = [];

    /// <summary>Each file the installer synced to disk (InstallerMachine.SyncFile), in order, as a path on the machine.</summary>
    public List<string> FileSyncs { get; } = [];

    /// <summary>Why syncing a file fails (an IOException's message); null for a sync that succeeds.</summary>
    public string? FileSyncFailure { get; set; }

    /// <summary>What happens as a file is synced, before the sync: a test looks at the machine, or fails the sync, then.</summary>
    public Action<string>? DuringFileSync { get; set; }

    /// <summary>
    /// What the installer did that durability depends on, in order: "sync-folder PATH", "sync-file PATH", "rename FROM TO"
    /// (InstallerMachine.MoveFileDurably) and "chown PATH".
    /// </summary>
    public List<string> DurabilityEvents { get; } = [];

    // Each folder whose durability is tracked (TrackFolderDurability), by path on the machine: whether it was there as the
    // last sync of the folder above it left it.
    private readonly Dictionary<string, bool> _durableFolders = new(StringComparer.Ordinal);

    // Each file whose durability is tracked (TrackDurability), by path on the machine: its bytes as its folder's last sync
    // left them, or null when it was not there then.
    private readonly Dictionary<string, byte[]?> _durable = new(StringComparer.Ordinal);

    /// <summary>
    /// From now on, the file at <paramref name="path"/> survives a power loss (<see cref="PowerLoss"/>) only as its folder's
    /// last sync left it, as a rename into a folder does: as it is now, until the installer syncs that folder.
    /// </summary>
    public FakeMachine TrackDurability(string path)
    {
        _durable[path] = Exists(path) ? File.ReadAllBytes(OnDisk(path)) : null;
        return this;
    }

    /// <summary>
    /// As <see cref="TrackDurability(string)"/>, but as if the file's folder had last been synced before the file was
    /// there: one written, by a run before, that nothing has made durable since.
    /// </summary>
    public FakeMachine TrackDurabilityFromNothing(string path)
    {
        _durable[path] = null;
        return this;
    }

    /// <summary>
    /// From now on, the folder at <paramref name="path"/> survives a power loss only if the last sync of the folder above it
    /// found it there (with <paramref name="durableNow"/>, as it is now; without, as if it had never been synced there). One
    /// that does not is gone after <see cref="PowerLoss"/>, with all in it.
    /// </summary>
    public FakeMachine TrackFolderDurability(string path, bool durableNow = true)
    {
        _durableFolders[path] = durableNow && Directory.Exists(OnDisk(path));
        return this;
    }

    /// <summary>
    /// Whether the tracked file at <paramref name="path"/> would be there after a power loss now, as it is: its content as
    /// its folder's last sync left it, and each tracked folder it is in there too.
    /// </summary>
    public bool SurvivesPowerLoss(string path)
        => Exists(path) && _durable[path] is { } bytes && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(OnDisk(path)))
            && _durableFolders.Where(folder => path.StartsWith(folder.Key + "/", StringComparison.Ordinal)).All(folder => folder.Value);

    /// <summary>The tracked file's text as it would be after a power loss now; null when it would not be there.</summary>
    public string? DurableText(string path) => _durable[path] is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>
    /// The machine loses power and starts again: each tracked file is as its folder's last sync left it, whatever was
    /// renamed over it since. Docker's containers and every other file stay as they are, durable already.
    /// </summary>
    public void PowerLoss()
    {
        foreach (var (folder, durable) in _durableFolders.OrderByDescending(entry => entry.Key.Length))
        {
            if (!durable && Directory.Exists(OnDisk(folder)))
            {
                Directory.Delete(OnDisk(folder), recursive: true);
            }
        }

        foreach (var (path, bytes) in _durable.Where(entry => Directory.Exists(Path.GetDirectoryName(OnDisk(entry.Key)))))
        {
            if (bytes is null)
            {
                File.Delete(OnDisk(path));
            }
            else
            {
                File.WriteAllBytes(OnDisk(path), bytes);
            }
        }
    }

    // InstallerMachine.SyncFolderOnDisk: kept, then failed as DirectorySyncFailure says, or the installer's own sync run
    // over this machine's libc (SyncCalls).
    private void SyncFolder(string onDisk)
    {
        var folder = onDisk[Root.TrimEnd('/').Length..];
        DirectorySyncs.Add(folder);
        DurabilityEvents.Add($"sync-folder {folder}");
        DuringDirectorySync?.Invoke(folder);
        Directory.Exists(onDisk).Should().BeTrue("the installer syncs a folder that is there");
        if (DirectorySyncFailure is { } failure)
        {
            throw new IOException($"Could not sync the folder {onDisk} to disk (fsync: {failure}).");
        }

        InstallerMachine.SyncFolder(onDisk, Os == InstallerOs.MacOS, LibcFor(folder));
    }

    // InstallerMachine.SyncFileOnDisk: kept, then failed as FileSyncFailure says, or the installer's own sync run over this
    // machine's libc (SyncCalls), F_FULLFSYNC on a Mac. A file's sync makes no folder entry durable.
    private void SyncFile(string onDisk)
    {
        var path = onDisk[Root.TrimEnd('/').Length..];
        FileSyncs.Add(path);
        DurabilityEvents.Add($"sync-file {path}");
        DuringFileSync?.Invoke(path);
        (File.Exists(onDisk) || Directory.Exists(onDisk)).Should().BeTrue("the installer syncs a file that is there");
        if (FileSyncFailure is { } failure)
        {
            throw new IOException($"Could not sync the file {onDisk} to disk (fsync: {failure}).");
        }

        InstallerMachine.SyncFolder(onDisk, Os == InstallerOs.MacOS, LibcFor(path), "file");
    }

    // InstallerMachine.RenameOnDisk: the rename, with what this fake knows of the file (its owner) going with it.
    private void Rename(string fromOnDisk, string toOnDisk, bool overwrite)
    {
        var root = Root.TrimEnd('/').Length;
        var (from, to) = (fromOnDisk[root..], toOnDisk[root..]);
        File.Move(fromOnDisk, toOnDisk, overwrite);
        DurabilityEvents.Add($"rename {from} {to}");
        if (Owners.Remove(from, out var owner))
        {
            Owners[to] = owner;
        }
        else
        {
            Owners.Remove(to);
        }
    }

    // libc's calls for syncing the folder, each kept in SyncCalls. The one that makes its tracked files durable as they
    // are now is fsync on Linux, and F_FULLFSYNC on a Mac, which fails as FullFsyncError says: a Mac's fsync leaves them
    // in the drive's cache, which a power loss loses. Linux has no F_FULLFSYNC (EINVAL).
    private FolderSyncCalls LibcFor(string folder)
    {
        const int descriptor = 3;
        var mac = Os == InstallerOs.MacOS;

        int Call(string name, int? error, bool durable)
        {
            SyncCalls.Add(name);
            if (error is { } errno)
            {
                Marshal.SetLastPInvokeError(errno);
                return -1;
            }

            if (durable)
            {
                foreach (var path in _durable.Keys.Where(path => Path.GetDirectoryName(path) == folder).ToArray())
                {
                    _durable[path] = File.Exists(OnDisk(path)) ? File.ReadAllBytes(OnDisk(path)) : null;
                }

                foreach (var path in _durableFolders.Keys.Where(path => Path.GetDirectoryName(path) == folder).ToArray())
                {
                    _durableFolders[path] = Directory.Exists(OnDisk(path));
                }
            }

            return 0;
        }

        return new(
            (_, _) => Call("open", null, durable: false) == 0 ? descriptor : -1,
            _ => Call("F_FULLFSYNC", mac ? FullFsyncError : 22, durable: mac),
            _ => Call("fsync", null, durable: !mac),
            _ => Call("close", null, durable: false));
    }

    /// <summary>The same machine, with <paramref name="userName"/> running the installer without sudo.</summary>
    public FakeMachine AsPerson(string userName = "pi")
    {
        IsRoot = false;
        UserName = userName;
        Home = $"/home/{userName}";
        Directory.CreateDirectory(OnDisk(Home));
        CurrentDirectory = Home;
        return this;
    }

    /// <summary>True for the names the deploy script gives a controller: the one running, the one kept, and the one swapping.</summary>
    public static bool IsController(string name)
        => name is MachineSurveyor.ControllerContainer or ControllerRollbackStep.PreviousContainer or ControllerRollbackStep.SwapContainer;

    /// <summary>The digest of the controller's image in the fake release of <paramref name="version"/>.</summary>
    public static string ControllerDigestFor(string version)
        => version == "4.0.0" ? ControllerDigest : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("controller " + version)));

    /// <summary>The digest of the HAT emulator's image in the fake release of <paramref name="version"/>.</summary>
    public static string EmulatorDigestFor(string version)
        => version == "4.0.0" ? EmulatorDigest : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("emulator " + version)));

    /// <summary>
    /// Each image's labels by digest, beyond its version: what a release's Dockerfile declares, such as the controller's
    /// state contract (<see cref="MachineSurveyor.StateContractsLabel"/>).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ImageLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes the fake release of <paramref name="version"/> on GitHub, with its own digests and files, and makes it the
    /// latest release when <paramref name="latest"/>. With <paramref name="stateContract"/> (JSON, as release.json's
    /// <c>stateContracts.controller</c>), its release.json and its controller image declare that contract.
    /// </summary>
    public FakeMachine WithRelease(string version, bool latest = false, IReadOnlyList<UpgradeNote>? upgradeNotes = null, string? stateContract = null)
    {
        int? identitySchema = null;
        if (stateContract is not null)
        {
            identitySchema = System.Text.Json.Nodes.JsonNode.Parse(stateContract)!["identity"]!["reads"]!["max"]!.GetValue<int>();
            ImageLabels[ControllerDigestFor(version)] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MachineSurveyor.StateContractsLabel] = stateContract,
                [MachineSurveyor.IdentitySchemaLabel] = identitySchema.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        var json = ReleaseJson(version, ControllerDigestFor(version), EmulatorDigestFor(version), upgradeNotes, stateContract, identitySchema);
        Downloads[ReleaseManifest.DownloadUri(version).ToString()] = json;
        FileDownloads[ReleaseManifest.DownloadUri(version, KioskAssetName(version)).ToString()] = KioskTarball(version);
        foreach (var (name, content) in ClientAssets(version))
        {
            FileDownloads[ReleaseManifest.DownloadUri(version, name).ToString()] = content;
        }

        if (latest)
        {
            Downloads[ReleaseManifest.LatestUri.ToString()] = json;
        }

        return this;
    }

    // InstallerMachine.RunProgramAsync: kept, then run as Launch says.
    private Task<int> RunProgramAsync(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        Launches.Add((path, arguments.ToArray(), new Dictionary<string, string>(environment)));
        return Launch is { } launch ? launch(path, arguments, environment) : RunInstallerAsync(path, arguments, environment);
    }

    /// <summary>
    /// Runs the fake release's installer at <paramref name="path"/> (on the machine's disk) as the release it says it is,
    /// or as <paramref name="version"/>, on this machine and terminal, with <paramref name="environment"/> set while it runs.
    /// </summary>
    public async Task<int> RunInstallerAsync(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string? version = null)
    {
        File.Exists(path).Should().BeTrue("the installer runs a program it put on the machine");
        var match = Regex.Match(File.ReadAllText(path), @"^echo 'hvo-roof-install (\S+) \(", RegexOptions.Multiline);
        match.Success.Should().BeTrue("the fake release made the program");
        var kept = environment.Keys.ToDictionary(name => name, name => Environment.GetValueOrDefault(name));
        foreach (var (name, value) in environment)
        {
            Environment[name] = value;
        }

        try
        {
            return await HVO.RoofControllerV4.Installer.Installer.RunAsync([.. arguments], Host(_output!, _error!, Interactive, version: version ?? match.Groups[1].Value)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (name, value) in kept)
            {
                if (value is null)
                {
                    Environment.Remove(name);
                }
                else
                {
                    Environment[name] = value;
                }
            }
        }
    }

    // The commands taking things away and the backup's stat; null for one this fake does not know.
    private CommandResult? LifecycleCommand(CommandLine command)
    {
        var arguments = command.Arguments;
        if (command.Program == "stat")
        {
            BeforeStat?.Invoke();
        }

        return command.Program switch
        {
            "docker" when arguments is ["image", "rm", var image] => Docker(() => RemoveImage(image)),
            "docker" when arguments is ["network", "rm", var network] => Docker(() => RemoveNetwork(network)),
            "systemctl" when arguments is ["stop", MachineSurveyor.KioskUnit] => Answer(string.Empty, after: () => Kiosk = Kiosk with { Active = false }),
            "systemctl" when arguments is ["disable", MachineSurveyor.KioskUnit] => Exists(MachineSurveyor.KioskUnitFile)
                ? Answer($"Removed \"/etc/systemd/system/graphical.target.wants/{MachineSurveyor.KioskUnit}\".", after: () => Kiosk = Kiosk with { Enabled = false })
                : new CommandResult(1, string.Empty, $"Failed to disable unit: Unit file {MachineSurveyor.KioskUnit} does not exist.\n"),
            "stat" when arguments is ["-c", "%F|%U:%G", "--", var path] => Stat(path, after: AfterStat.GetValueOrDefault(path)),
            "stat" when arguments is ["-L", "-c", "%F|%U:%G", "--", var opened] => StatOpened(opened),
            _ when arguments is ["--version"] && CliVersion(command.Program) is { } line => Answer(line),
            _ => null
        };
    }

    // The fake release's hvo-roof, wherever the installer put it, says which release it is.
    private string? CliVersion(string program)
    {
        if (!program.StartsWith('/') || !File.Exists(OnDisk(program)))
        {
            return null;
        }

        var match = Regex.Match(File.ReadAllText(OnDisk(program)), @"^echo '(hvo-roof \S+ \([^)]*\))'", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    // As Docker: an image a container uses stays.
    private CommandResult RemoveImage(string image)
    {
        if (Containers.FirstOrDefault(pair => ImageOf(pair.Key, pair.Value) == image) is { Key: { } user })
        {
            return new CommandResult(1, string.Empty, $"Error response from daemon: conflict: unable to delete {image} (must be forced) - image is being used by stopped container {user}\n");
        }

        RemovedImages.Add(image);
        return Answer($"Untagged: {image}");
    }

    // As Docker: a network a container is on stays.
    private CommandResult RemoveNetwork(string network)
    {
        if (Containers.Any(pair => pair.Value.Network == network))
        {
            return new CommandResult(1, string.Empty, $"Error response from daemon: error while removing network: network {network} has active endpoints\n");
        }

        return Networks.Remove(network) ? Answer(network) : new CommandResult(1, string.Empty, $"Error response from daemon: network {network} not found\n");
    }

    // stat -c '%F|%U:%G': its type as GNU stat names it, and its owner.
    private CommandResult Stat(string path, Action? after = null)
    {
        var info = new FileInfo(OnDisk(path));
        if (!info.Exists && !Directory.Exists(OnDisk(path)) && info.LinkTarget is null)
        {
            return new CommandResult(1, string.Empty, $"stat: cannot statx '{path}': No such file or directory\n");
        }

        var type = info.LinkTarget is not null ? "symbolic link"
            : Directory.Exists(OnDisk(path)) ? "directory"
            : info.Length == 0 ? "regular empty file"
            : "regular file";
        var answer = $"{type}|{Owners.GetValueOrDefault(path, "root:root")}";
        return after is null ? Answer(answer) : Answer(answer, after);
    }

    // stat -L -c '%F|%U:%G' /proc/<pid>/fd/<n>: the file the installer has open on that descriptor, wherever it is now.
    private CommandResult StatOpened(string opened)
    {
        var target = opened.StartsWith("/proc/", StringComparison.Ordinal) ? new FileInfo(opened).LinkTarget : null;
        var root = Root.TrimEnd('/');
        return target is not null && target.StartsWith(root + "/", StringComparison.Ordinal)
            ? Stat(target[root.Length..], AfterStatOpened)
            : new CommandResult(1, string.Empty, $"stat: cannot statx '{opened}': No such file or directory\n");
    }

    // The image a container runs, as Inspect reports it.
    private static string ImageOf(string name, FakeContainer container)
    {
        var controller = IsController(name);
        var digest = container.Digest ?? (controller ? ControllerDigest : EmulatorDigest);
        return $"ghcr.io/hualapaivalley/{(controller ? "roof-controller" : "roof-hat-emulator")}:{container.Version ?? "4.0.0"}@{digest}";
    }
}
