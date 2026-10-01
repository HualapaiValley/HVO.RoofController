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
    /// Publishes the fake release of <paramref name="version"/> on GitHub, with its own digests and files, and makes it the
    /// latest release when <paramref name="latest"/>.
    /// </summary>
    public FakeMachine WithRelease(string version, bool latest = false, IReadOnlyList<UpgradeNote>? upgradeNotes = null)
    {
        var json = ReleaseJson(version, ControllerDigestFor(version), EmulatorDigestFor(version), upgradeNotes);
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
            ? Stat(target[root.Length..])
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
