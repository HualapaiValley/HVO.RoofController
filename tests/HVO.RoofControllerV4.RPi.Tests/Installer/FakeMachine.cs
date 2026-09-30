using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>A container the fake machine's Docker reports.</summary>
internal sealed record FakeContainer
{
    public bool Emulated { get; init; }

    public string State { get; init; } = "running";

    /// <summary>The Compose project that made it; null for the deploy script.</summary>
    public string? ComposeProject { get; init; }

    public string? Version { get; init; } = "4.0.0";

    /// <summary>A value in its environment that must never reach the log, the plan or the answers.</summary>
    public string? Secret { get; init; }
}

/// <summary>
/// A whole machine for the installer, in a folder of its own: its files (a Pi's HAT devices, records, folders the
/// installer makes) under <see cref="Root"/>, and the programs it runs (docker, systemctl, hvo-roof) answered from what
/// the test set up. A command it does not know fails as a missing program and is kept in <see cref="Unexpected"/>: the
/// installer only reads with commands in this issue, so a test can prove it ran nothing else.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class FakeMachine : ICommandRunner, IDisposable
{
    public FakeMachine(InstallerOs os = InstallerOs.Linux, Architecture architecture = Architecture.Arm64, bool root = true, string hostName = "roofpi", string userName = "pi")
    {
        Root = Path.Combine(Path.GetTempPath(), "hvo-roof-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Os = os;
        Architecture = architecture;
        IsRoot = root;
        HostName = hostName;
        UserName = root ? "root" : userName;
        Home = root ? "/root" : os == InstallerOs.MacOS ? $"/Users/{userName}" : $"/home/{userName}";
        Directory.CreateDirectory(OnDisk(Home));
        CurrentDirectory = Home;
        if (os == InstallerOs.Linux)
        {
            Write("/etc/os-release", "NAME=\"Debian GNU/Linux\"\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n");
            Folder("/var/log");
        }
    }

    public string Root { get; }

    public InstallerOs Os { get; }

    public Architecture Architecture { get; }

    public bool IsRoot { get; }

    public string HostName { get; }

    public string UserName { get; }

    public string Home { get; }

    public string CurrentDirectory { get; set; }

    /// <summary>Docker's engine version; null when Docker is not installed (or <see cref="DockerError"/> says why it fails).</summary>
    public string? DockerVersion { get; set; } = "27.3.1";

    public string? ComposeVersion { get; set; } = "2.29.7";

    /// <summary>What docker says when it cannot answer (not running, not allowed).</summary>
    public string? DockerError { get; set; }

    public Dictionary<string, FakeContainer> Containers { get; } = [];

    /// <summary>The kiosk's service: enabled and active, when its unit file is there.</summary>
    public (bool Enabled, bool Active) Kiosk { get; set; } = (true, true);

    /// <summary>TCP ports something on the machine listens on.</summary>
    public HashSet<int> PortsInUse { get; } = [];

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" };

    /// <summary>Programs on the PATH, by name: their full paths.</summary>
    public Dictionary<string, string> Programs { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<CommandLine> Ran { get; } = new();

    public ConcurrentQueue<CommandLine> Unexpected { get; } = new();

    public InstallerMachine Machine => new()
    {
        Root = Root,
        Commands = this,
        Os = Os,
        Architecture = Architecture,
        IsRoot = IsRoot,
        UserName = UserName,
        Home = Home,
        HostName = HostName,
        Environment = name => Environment.GetValueOrDefault(name),
        CurrentDirectory = CurrentDirectory,
        IsPortInUse = PortsInUse.Contains
    };

    public string OnDisk(string path) => Path.Join(Root, path);

    public bool Exists(string path) => File.Exists(OnDisk(path)) || Directory.Exists(OnDisk(path));

    public string Read(string path) => File.ReadAllText(OnDisk(path));

    public UnixFileMode Mode(string path) => File.GetUnixFileMode(OnDisk(path));

    public FakeMachine Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OnDisk(path))!);
        File.WriteAllText(OnDisk(path), content);
        return this;
    }

    public FakeMachine Folder(string path)
    {
        Directory.CreateDirectory(OnDisk(path));
        return this;
    }

    /// <summary>A Raspberry Pi 5 with the HAT's devices: its I2C bus, its GPIO and its temperature.</summary>
    public FakeMachine WithPi(bool i2c = true, bool gpio = true, bool thermal = true)
    {
        Write(HatDevices.PiModel, "Raspberry Pi 5 Model B Rev 1.0\0");
        if (i2c)
        {
            Write(HatDevices.I2c, string.Empty);
        }

        if (gpio)
        {
            Write(HatDevices.GpioMemory, string.Empty);
        }

        if (thermal)
        {
            Write(HatDevices.ThermalSensor, "48150\n");
        }

        return this;
    }

    public FakeMachine WithContainer(string name, FakeContainer container)
    {
        Containers[name] = container;
        return this;
    }

    /// <summary>hvo-roof, on the PATH at <paramref name="path"/>.</summary>
    public FakeMachine WithCli(string path)
    {
        Write(path, "#!/bin/sh\n");
        Programs["hvo-roof"] = path;
        return this;
    }

    /// <summary>Writes the answers as a file in the home folder and returns its path.</summary>
    public string WriteAnswers(InstallAnswers answers, string name = "answers.json")
    {
        var path = Path.Join(Home, name);
        Write(path, answers.ToJson());
        return path;
    }

    public InstallerHost Host(TextWriter output, TextWriter error, bool interactive = false, TimeProvider? time = null) => new()
    {
        Out = output,
        Error = error,
        Machine = Machine,
        IsInteractive = interactive,
        Time = time ?? TimeProvider.System,
        Version = "4.0.0+0123456789abcdef0123456789abcdef01234567"
    };

    /// <summary>Runs hvo-roof-install with <paramref name="args"/> on this machine.</summary>
    public async Task<InstallerRun> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await HVO.RoofControllerV4.Installer.Installer.RunAsync(args, Host(output, error)).ConfigureAwait(false);
        return new InstallerRun(exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Every file and folder under the machine's root with its mode, content and time: equal snapshots mean nothing was
    /// made, changed or touched. <paramref name="except"/> leaves out a path (the log, which each run adds to).
    /// </summary>
    public IReadOnlyDictionary<string, string> Snapshot(params string[] except)
    {
        var left = except.Select(OnDisk).ToHashSet(StringComparer.Ordinal);
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            if (left.Contains(entry))
            {
                continue;
            }

            var mode = Modes.Octal(File.GetUnixFileMode(entry));
            snapshot[entry[Root.Length..]] = Directory.Exists(entry)
                ? $"folder {mode}"
                : $"file {mode} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))} {File.GetLastWriteTimeUtc(entry).Ticks}";
        }

        return snapshot;
    }

    /// <summary>Every file under the machine's root, as text: for proving a secret is in none of them.</summary>
    public string AllText()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            text.AppendLine(file).AppendLine(File.ReadAllText(file));
        }

        return text.ToString();
    }

    public string? Find(string program) => Programs.GetValueOrDefault(program);

    public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
    {
        Ran.Enqueue(command);
        var arguments = command.Arguments;
        CommandResult? result = command.Program switch
        {
            "docker" when arguments is ["version", "--format", "{{.Server.Version}}"] => Docker(() => Answer(DockerVersion!)),
            "docker" when arguments is ["compose", "version", "--short"] => Docker(() => ComposeVersion is null
                ? new CommandResult(1, string.Empty, "docker: 'compose' is not a docker command.\n")
                : Answer(ComposeVersion)),
            "docker" when arguments is ["container", "inspect", var name] => Docker(() => Containers.TryGetValue(name, out var container)
                ? Answer(Inspect(name, container))
                : new CommandResult(1, "[]\n", $"Error response from daemon: No such container: {name}\n")),
            "systemctl" when arguments is ["is-enabled", MachineSurveyor.KioskUnit] => Answer(Kiosk.Enabled ? "enabled" : "disabled", Kiosk.Enabled ? 0 : 1),
            "systemctl" when arguments is ["is-active", MachineSurveyor.KioskUnit] => Answer(Kiosk.Active ? "active" : "inactive", Kiosk.Active ? 0 : 3),
            "sw_vers" when arguments is ["-productVersion"] && Os == InstallerOs.MacOS => Answer("15.6.1"),
            _ when Programs.GetValueOrDefault("hvo-roof") == command.Program && arguments is ["--version"] => Answer("4.0.0+0123456789abcdef"),
            _ => null
        };

        if (result is null)
        {
            Unexpected.Enqueue(command);
            result = new CommandResult(CommandResult.NotFound, string.Empty, $"{command.Program}: not found");
        }

        return Task.FromResult(result);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CommandResult Docker(Func<CommandResult> answer)
        => DockerError is { } error ? new CommandResult(1, string.Empty, error + "\n")
            : DockerVersion is null ? new CommandResult(CommandResult.NotFound, string.Empty, "docker: not found")
            : answer();

    private static CommandResult Answer(string output, int exitCode = 0) => new(exitCode, output + "\n", string.Empty);

    private static string Inspect(string name, FakeContainer container)
    {
        var labels = new Dictionary<string, string>();
        if (container.Version is { } version)
        {
            labels[MachineSurveyor.VersionLabel] = version;
        }

        if (container.ComposeProject is { } project)
        {
            labels[MachineSurveyor.ComposeProjectLabel] = project;
        }

        var environment = new List<string> { "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" };
        if (name == MachineSurveyor.ControllerContainer)
        {
            environment.Add($"HatEmulator__Enabled={(container.Emulated ? "true" : "false")}");
            if (container.Emulated)
            {
                environment.Add("HatEmulator__Host=hat-emulator");
                environment.Add("HatEmulator__Port=5555");
            }
        }

        if (container.Secret is { } secret)
        {
            environment.Add($"RoofControllerSecurity__ApiKeys__0__Key={secret}");
        }

        var image = $"ghcr.io/hualapaivalley/{(name == MachineSurveyor.ControllerContainer ? "roof-controller" : "roof-hat-emulator")}@sha256:{new string('a', 64)}";
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                Name = "/" + name,
                Config = new { Image = image, Labels = labels, Env = environment },
                State = new { Status = container.State }
            }
        });
    }
}

/// <summary>How a run of hvo-roof-install ended, and what it wrote.</summary>
internal sealed record InstallerRun(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n--- out\n{Output}\n--- error\n{Error}";
}
