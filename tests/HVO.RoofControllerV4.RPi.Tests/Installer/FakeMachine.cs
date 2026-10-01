using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>A container the fake machine's Docker reports.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed record FakeContainer
{
    public bool Emulated { get; init; }

    public string State { get; init; } = "running";

    /// <summary>The Compose project that made it; null for the deploy script.</summary>
    public string? ComposeProject { get; init; }

    public string? Version { get; init; } = "4.0.0";

    /// <summary>A value in its environment that must never reach the log, the plan or the answers.</summary>
    public string? Secret { get; init; }

    /// <summary>The controller serves HTTPS, as the deploy script deploys it by default.</summary>
    public bool Https { get; init; } = true;

    /// <summary>The port the controller's API is published on: by default, the deploy script's for HTTPS or HTTP.</summary>
    public int? ApiPort { get; init; }

    /// <summary>The port the web UI is published on.</summary>
    public int WebPort { get; init; } = ControllerSettings.DefaultWebPort;

    /// <summary>The host names it answers to (AllowedHosts); null when the deploy script left it unset (any).</summary>
    public string? AllowedHosts { get; init; }

    /// <summary>The digest of the image it runs; null for the one the fake release names.</summary>
    public string? Digest { get; init; }

    /// <summary>The one address its ports are published on; null for every address.</summary>
    public string? PublishAddress { get; init; }

    /// <summary>The Docker network it is on; null for Docker's default bridge.</summary>
    public string? Network { get; init; }

    /// <summary>The HAT emulator an emulated controller uses (HAT_EMULATOR_ENDPOINT).</summary>
    public string HatEmulatorEndpoint { get; init; } = HatEmulatorStep.Endpoint;

    /// <summary>Its environment's other settings (never a secret): RoofWeb__StopKeyFile, OTEL_EXPORTER_OTLP_ENDPOINT…</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>When it last started; null for one that says it never has.</summary>
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>A person the fake controller was asked to add through its API: what the installer sent.</summary>
internal sealed record FakeUserRequest(string Name, string Role, string Password, string? Pin, string Key);

/// <summary>A PIN the fake controller was asked to set for a person through its API: what the installer sent.</summary>
internal sealed record FakePinRequest(string Name, string Role, string Pin, string Key);

/// <summary>
/// A whole machine for the installer, in a folder of its own: its files (a Pi's HAT devices, records, folders the
/// installer makes) under <see cref="Root"/>, and the programs it runs (docker, systemctl, hvo-roof) answered from what
/// the test set up. A command it does not know fails as a missing program and is kept in <see cref="Unexpected"/>: the
/// installer only reads with commands in this issue, so a test can prove it ran nothing else.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed partial class FakeMachine : ICommandRunner, IDisposable
{
    /// <summary>The digest of the controller's image in the fake release.</summary>
    public static readonly string ControllerDigest = "sha256:" + new string('c', 64);

    /// <summary>The digest of the HAT emulator's image in the fake release.</summary>
    public static readonly string EmulatorDigest = "sha256:" + new string('e', 64);

    /// <summary>When the tests' installs happen: certificates' dates, and the screenshots that show them, never change.</summary>
    public static readonly DateTimeOffset Today = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A clock that always reads <see cref="Today"/>, which the installer runs with in these tests.</summary>
    public static TimeProvider Clock { get; } = new FixedClock(Today);

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
        Directory.CreateDirectory(OnDisk(TemporaryDirectory));
        CurrentDirectory = Home;
        Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = ReleaseJson();
        FileDownloads[ReleaseManifest.DownloadUri("4.0.0", KioskAssetName("4.0.0")).ToString()] = KioskTarball("4.0.0");
        foreach (var (name, content) in ClientAssets("4.0.0"))
        {
            FileDownloads[ReleaseManifest.DownloadUri("4.0.0", name).ToString()] = content;
        }
        foreach (var program in new[] { "bash", "curl", "setsid", "jq" })
        {
            Programs[program] = $"/usr/bin/{program}";
        }

        if (os == InstallerOs.Linux)
        {
            Write("/etc/os-release", "NAME=\"Debian GNU/Linux\"\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n");
            Folder("/var/log");
        }
    }

    public string Root { get; }

    public InstallerOs Os { get; }

    public Architecture Architecture { get; }

    public bool IsRoot { get; private set; }

    public string HostName { get; }

    public string UserName { get; private set; }

    public string Home { get; private set; }

    public string CurrentDirectory { get; set; }

    /// <summary>Docker's engine version; null when Docker is not installed (or <see cref="DockerError"/> says why it fails).</summary>
    public string? DockerVersion { get; set; } = "27.3.1";

    public string? ComposeVersion { get; set; } = "2.29.7";

    /// <summary>What docker says when it cannot answer (not running, not allowed).</summary>
    public string? DockerError { get; set; }

    public Dictionary<string, FakeContainer> Containers { get; } = [];

    /// <summary>The kiosk's service, when its unit file is there: whether it is enabled, and running.</summary>
    public (bool Enabled, bool Active) Kiosk { get; set; }

    /// <summary>True while systemd waits to start the kiosk again after it stopped: it is activating, not active.</summary>
    public bool KioskRestarting { get; set; }

    /// <summary>When the kiosk's service last started; null for never.</summary>
    public DateTimeOffset? KioskStartedAt { get; set; }

    /// <summary>How often systemd was asked to start the kiosk (again).</summary>
    public int KioskStarts { get; private set; }

    /// <summary>How often systemd was asked to read its units again.</summary>
    public int DaemonReloads { get; private set; }

    /// <summary>How often udev was asked to apply its rules to the backlight.</summary>
    public int BacklightTriggers { get; private set; }

    /// <summary>A desktop's display manager has the screen (display-manager.service is active).</summary>
    public bool DisplayManagerActive { get; set; }

    /// <summary>A desktop's display manager is enabled: it starts at boot when <see cref="DefaultTarget"/> is graphical.target.</summary>
    public bool DisplayManagerEnabled { get; set; }

    /// <summary>What systemd boots to (systemctl get-default).</summary>
    public string DefaultTarget { get; set; } = MachineSurveyor.GraphicalTarget;

    /// <summary>The Debian packages installed, as dpkg knows them; null for a machine without dpkg.</summary>
    public HashSet<string>? Packages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What apt-get installed, in order, and the environment it ran with.</summary>
    public List<(IReadOnlyList<string> Packages, IReadOnlyDictionary<string, string>? Environment)> AptInstalls { get; } = [];

    /// <summary>The machine's users, by name: the groups each is in (their own first).</summary>
    public Dictionary<string, List<string>> Users { get; } = new(StringComparer.Ordinal) { ["root"] = ["root"], ["pi"] = ["pi", "sudo", "video"] };

    /// <summary>The machine's groups, by name.</summary>
    public HashSet<string> Groups { get; } = new(StringComparer.Ordinal) { "root", "pi", "sudo", "video", "input", "render", "nogroup" };

    /// <summary>Each file's owner and group (user:group), by path, as chown left them; root:root for one not here.</summary>
    public Dictionary<string, string> Owners { get; } = new(StringComparer.Ordinal);

    // The unit systemd read (at a daemon-reload, or when it first loaded it): a unit file that differs needs a reload.
    private string? _loadedKioskUnit;

    /// <summary>TCP ports something on the machine listens on.</summary>
    public HashSet<int> PortsInUse { get; } = [];

    /// <summary>The machine's addresses, by interface: its LAN's, a link-local one, and Docker's bridge.</summary>
    public List<NetworkAddress> Addresses { get; } =
    [
        new("eth0", IPAddress.Parse("192.168.1.50")),
        new("eth0", IPAddress.Parse("fe80::1")),
        new("docker0", IPAddress.Parse("172.17.0.1"))
    ];

    /// <summary>The certificate something presents on a loopback port, by port (as the controller would).</summary>
    public Dictionary<int, X509Certificate2> ServedCertificates { get; } = [];

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" };

    /// <summary>Programs on the PATH, by name: their full paths.</summary>
    public Dictionary<string, string> Programs { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<CommandLine> Ran { get; } = new();

    /// <summary>What the machine can download, by URL: the release's release.json on GitHub, unless a test takes it away.</summary>
    public Dictionary<string, string> Downloads { get; } = new(StringComparer.Ordinal);

    /// <summary>The files the machine can download, by URL: the release's kiosk, unless a test takes it away.</summary>
    public Dictionary<string, byte[]> FileDownloads { get; } = new(StringComparer.Ordinal);

    /// <summary>What the installer downloaded, in order.</summary>
    public ConcurrentQueue<Uri> Downloaded { get; } = new();

    /// <summary>The machine's folder for temporary files.</summary>
    public string TemporaryDirectory { get; } = "/tmp";

    public ConcurrentQueue<CommandLine> Unexpected { get; } = new();

    /// <summary>The API keys the running controller knows: those it read when it started.</summary>
    public HashSet<string> ControllerKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>What the controller's Status says to a key it knows; null when it does not answer.</summary>
    public string? StatusJson { get; set; } = "{\"isMoving\":false,\"commandedMotion\":\"None\",\"hatMode\":\"Physical\"}";

    /// <summary>The controller's people, by name (as its identity store keeps them, whatever the case): their roles.</summary>
    public Dictionary<string, string> People { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each request to add a person, in order, with what was sent.</summary>
    public List<FakeUserRequest> UserRequests { get; } = [];

    /// <summary>The PINs people have, by name, as the controller was given them.</summary>
    public Dictionary<string, string> PeoplesPins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each request to set a person's PIN, in order, with what was sent.</summary>
    public List<FakePinRequest> PinRequests { get; } = [];

    /// <summary>What the controller answers a request to add a person; null to add them (201, or 409 when they are there).</summary>
    public int? UsersAnswer { get; set; }

    /// <summary>Docker's networks.</summary>
    public HashSet<string> Networks { get; } = new(StringComparer.Ordinal) { "bridge" };

    /// <summary>The environment of each run of the deploy script, in order.</summary>
    public List<IReadOnlyDictionary<string, string>> Deploys { get; } = [];

    /// <summary>What the deploy script says when it fails; null for a deploy that succeeds.</summary>
    public string? DeployFailure { get; set; }

    /// <summary>The deploy script's exit code when it fails with <see cref="DeployFailure"/>: 130 when a Ctrl-C stopped it.</summary>
    public int DeployFailureExitCode { get; set; } = 1;

    /// <summary>What happens while the deploy script runs (a Ctrl-C, say), before it finishes.</summary>
    public Action? DuringDeploy { get; set; }

    /// <summary>For each run of the deploy script, in order: whether the installer could have stopped it part-way.</summary>
    public List<bool> DeploysCancellable { get; } = [];

    /// <summary>What the HAT emulator's health check says once it is started.</summary>
    public string EmulatorHealth { get; set; } = "healthy";

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
        IsPortInUse = PortsInUse.Contains,
        NetworkAddresses = () => Addresses.ToArray(),
        ServedCertificateAsync = (port, _) => Task.FromResult(ServedCertificates.TryGetValue(port, out var served) ? X509CertificateLoader.LoadCertificate(served.RawData) : null),
        DownloadTextAsync = (uri, _) =>
        {
            Downloaded.Enqueue(uri);
            return Downloads.TryGetValue(uri.ToString(), out var text)
                ? Task.FromResult(text)
                : Task.FromException<string>(new HttpRequestException("HTTP 404 Not Found.", null, HttpStatusCode.NotFound));
        },
        DownloadToAsync = async (uri, stream, maxBytes, cancellationToken) =>
        {
            Downloaded.Enqueue(uri);
            if (!FileDownloads.TryGetValue(uri.ToString(), out var content))
            {
                throw new HttpRequestException("HTTP 404 Not Found.", null, HttpStatusCode.NotFound);
            }

            if (content.Length > maxBytes)
            {
                throw new InvalidDataException($"The download is larger than {maxBytes} bytes.");
            }

            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        },
        FetchCaAsync = (controller, cancellationToken) =>
        {
            CaFetches.Enqueue(controller);
            return FetchCa(controller, cancellationToken);
        },
        ApiHandler = ApiHandler,
        TemporaryDirectory = TemporaryDirectory,
        RunProgramAsync = RunProgramAsync
    };

    /// <summary>The kiosk's program in the fake release of <paramref name="version"/>: a script that says which it is.</summary>
    public static byte[] KioskProgram(string version) => Encoding.UTF8.GetBytes($"#!/bin/sh\necho 'HVO roof kiosk {version}'\n");

    /// <summary>The name of the fake release's kiosk tarball, as build/release-assets.py names it.</summary>
    public static string KioskAssetName(string version) => $"hvo-roof-kiosk-{version}-linux-arm64.tar.gz";

    /// <summary>
    /// The fake release's kiosk, as the release workflow packs it: one folder with the program and the files it is set up
    /// with, the same bytes each time. <paramref name="program"/> replaces the program (one that is not the release's).
    /// </summary>
    public static byte[] KioskTarball(string version, byte[]? program = null)
    {
        var folder = KioskAssetName(version)[..^".tar.gz".Length];
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            void Add(string name, byte[] content, UnixFileMode mode)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{folder}/{name}")
                {
                    Mode = mode,
                    ModificationTime = Today,
                    DataStream = new MemoryStream(content)
                };
                tar.WriteEntry(entry);
            }

            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = Modes.Folder, ModificationTime = Today });
            Add("hvo-roof-kiosk", program ?? KioskProgram(version), Modes.Program);
            Add("hvo-roof-kiosk.service", Encoding.UTF8.GetBytes(KioskSteps.Resource("hvo-roof-kiosk.service")), Modes.File);
            Add("99-hvo-roof-kiosk-backlight.rules", Encoding.UTF8.GetBytes(KioskSteps.Resource("99-hvo-roof-kiosk-backlight.rules")), Modes.File);
            Add("appsettings.Local.example.json", Encoding.UTF8.GetBytes(KioskSteps.Resource("appsettings.Local.example.json")), Modes.File);
        }

        return output.ToArray();
    }

    /// <summary>A release.json as build/release-assets.py writes it, for <paramref name="version"/>, with the fake release's digests.</summary>
    public static string ReleaseJson(string version = "4.0.0", string? controllerDigest = null, string? emulatorDigest = null, IReadOnlyList<UpgradeNote>? upgradeNotes = null)
    {
        object Image(string name, string digest) => new
        {
            repository = $"ghcr.io/hualapaivalley/{name}",
            tag = version,
            digest,
            reference = $"ghcr.io/hualapaivalley/{name}:{version}@{digest}",
            platforms = new[] { "linux/amd64", "linux/arm64" }
        };

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            product = "HVO Roof Controller",
            version,
            tag = $"v{version}",
            prerelease = version.Contains('-', StringComparison.Ordinal),
            commit = "0123456789abcdef0123456789abcdef01234567",
            created = "2026-10-01T12:00:00Z",
            repository = "https://github.com/HualapaiValley/HVO.RoofController",
            upgradeNotes = upgradeNotes?.Select(note => new { version = note.Version, text = note.Text }),
            images = new
            {
                controller = Image("roof-controller", controllerDigest ?? ControllerDigest),
                hatEmulator = Image("roof-hat-emulator", emulatorDigest ?? EmulatorDigest)
            },
            assets = ((object[])
            [
                new
                {
                    name = KioskAssetName(version),
                    kind = KioskSteps.AssetKind,
                    platform = KioskSteps.AssetPlatform,
                    size = KioskTarball(version).LongLength,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(KioskTarball(version))),
                    files = new Dictionary<string, string> { ["hvo-roof-kiosk"] = Convert.ToHexStringLower(SHA256.HashData(KioskProgram(version))) }
                },
                .. ClientAssetEntries(version)
            ])
        });
    }

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

    /// <summary>
    /// The Pi's display outputs, as the kernel lists them: the touchscreen on DSI-1 (connected unless
    /// <paramref name="connected"/> is false) and an HDMI port with nothing in it.
    /// </summary>
    public FakeMachine WithDisplay(bool connected = true)
    {
        Write($"{MachineSurveyor.DrmFolder}/card1-DSI-1/status", connected ? "connected\n" : "disconnected\n");
        Write($"{MachineSurveyor.DrmFolder}/card1-HDMI-A-1/status", "disconnected\n");
        Folder($"{MachineSurveyor.DrmFolder}/card1");
        return this;
    }

    /// <summary>The kiosk's libraries, installed as apt installs them.</summary>
    public FakeMachine WithKioskPackages()
    {
        Packages!.UnionWith(KioskSteps.Packages);
        return this;
    }

    public FakeMachine WithContainer(string name, FakeContainer container)
    {
        Containers[name] = container;
        return this;
    }

    /// <summary>
    /// The certificate files an earlier install left, made by the installer's own code for <paramref name="settings"/>
    /// (a private CA by default): the CA, the file's password and the certificate, each with its mode (none over HTTP).
    /// The controller's container, when there is one, answers to the controller's names and serves the certificate.
    /// </summary>
    public FakeMachine WithCertificates(ControllerSettings? settings = null)
    {
        settings ??= new ControllerSettings();
        var layout = ControllerLayout.For(Machine);
        var names = CertificateNames.For(Machine, settings);
        var now = Today;
        if (Containers.TryGetValue(MachineSurveyor.ControllerContainer, out var controller))
        {
            Containers[MachineSurveyor.ControllerContainer] = controller with { AllowedHosts = names.AllowedHosts };
        }

        if (settings.Connection == ConnectionMode.Http)
        {
            return this;
        }

        foreach (var folder in new[] { layout.Configuration, layout.Secrets, layout.Https })
        {
            Machine.CreateDirectory(folder, folder == layout.Configuration ? Modes.Folder : Modes.PrivateFolder);
        }

        using var authority = settings.Connection == ConnectionMode.PrivateCa ? ControllerCertificates.CreateAuthority(names, now) : null;
        if (authority is not null)
        {
            Machine.CreateDirectory(layout.Ca, Modes.PrivateFolder);
            using var key = authority.GetECDsaPrivateKey()!;
            Machine.WriteAtomically(layout.CaKey, key.ExportPkcs8PrivateKeyPem(), Modes.PrivateFile);
            Machine.WriteAtomically(layout.CaCertificate, authority.ExportCertificatePem() + "\n", Modes.File);
        }

        var password = ControllerCertificates.NewPassword();
        Machine.WriteAtomically(layout.PfxPassword, password, Modes.PrivateFile);
        using var certificate = authority is null ? ControllerCertificates.SelfSigned(names, now) : ControllerCertificates.Issue(authority, names, now);
        Machine.WriteAtomically(layout.Pfx, ControllerCertificates.ExportPfx(certificate, authority, password), Modes.PrivateFile);
        if (controller is not null)
        {
            ServedCertificates[controller.ApiPort ?? settings.ApiPort] = X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        return this;
    }

    /// <summary>
    /// An API key in the controller's secrets folder, one file per setting as docs/deployment.md sets one up, with a random
    /// key; <paramref name="key"/> false leaves only its name and role. The running controller knows it unless
    /// <paramref name="known"/> is false (it was added after the controller started).
    /// </summary>
    public FakeMachine WithApiKey(int index, string name, string role, bool kiosk = false, bool local = false, bool key = true, bool known = true)
    {
        var secrets = ControllerLayout.For(Machine).Secrets;
        Machine.CreateDirectory(secrets, Modes.PrivateFolder);
        void Setting(string field, string value) => Machine.WriteAtomically(Path.Join(secrets, ApiKeyFiles.FileName(index, field)), value, Modes.PrivateFile);
        Setting("Name", name);
        Setting("Role", role);
        if (kiosk)
        {
            Setting("Kiosk", "true");
        }

        if (local)
        {
            Setting("Local", "true");
        }

        if (key)
        {
            var value = ApiKeyFiles.NewKey();
            Setting("Key", value);
            if (known)
            {
                ControllerKeys.Add(value);
            }
        }

        return this;
    }

    /// <summary>The keys' values in the secrets folder, to prove they appear nowhere else.</summary>
    public IReadOnlyList<string> ApiKeyValues()
    {
        var secrets = OnDisk(ControllerLayout.For(Machine).Secrets);
        return Directory.Exists(secrets)
            ? [.. Directory.GetFiles(secrets, $"{ApiKeyFiles.Prefix}*__Key").Select(File.ReadAllText)]
            : [];
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

    /// <summary>The clock hvo-roof-install runs with: <see cref="Clock"/> unless a test moves it.</summary>
    public TimeProvider RunsAt { get; set; } = Clock;

    /// <summary>What the person types when the installer asks for a secret, by what it asks for; null when no one can be asked.</summary>
    public Func<string, string?> Types { get; set; } = _ => null;

    /// <summary>What the installer asked the person to type, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>The person's answer when the installer asks a yes-or-no question, by the question; null when no one can be asked.</summary>
    public Func<string, bool?> Replies { get; set; } = _ => null;

    /// <summary>What the person types when the installer asks a question that is not a secret; null when no one can be asked.</summary>
    public Func<string, string?> Typed { get; set; } = _ => null;

    /// <summary>The questions the installer asked (yes or no, and typed), in order.</summary>
    public List<string> Questions { get; } = [];

    public InstallerHost Host(TextWriter output, TextWriter error, bool interactive = false, TimeProvider? time = null, string version = "4.0.0") => new()
    {
        Out = output,
        Error = error,
        Machine = Machine,
        IsInteractive = interactive,
        Time = time ?? RunsAt,
        Version = $"{version}+0123456789abcdef0123456789abcdef01234567",
        ReadSecret = what =>
        {
            Asked.Add(what);
            return Types(what);
        },
        Confirm = question =>
        {
            Questions.Add(question);
            return Replies(question);
        },
        Ask = question =>
        {
            Questions.Add(question);
            return Typed(question);
        }
    };

    /// <summary>Runs hvo-roof-install with <paramref name="args"/> on this machine.</summary>
    public Task<InstallerRun> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    /// <summary>Runs hvo-roof-install with <paramref name="args"/> on this machine, stopped as a Ctrl-C stops it when <paramref name="interrupt"/> is cancelled.</summary>
    public async Task<InstallerRun> RunAsync(CancellationToken interrupt, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        _output = output;
        _error = error;
        try
        {
            var exit = await HVO.RoofControllerV4.Installer.Installer.RunAsync(args, Host(output, error, Interactive, version: InstallerVersion), interrupt).ConfigureAwait(false);
            return new InstallerRun(exit, output.ToString(), error.ToString());
        }
        finally
        {
            _output = null;
            _error = null;
        }
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

    /// <summary>Every file and folder under the machine's root, in order, as "path kind mode owner": never what a file holds.</summary>
    public IReadOnlyList<string> Listing()
        => [.. Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Select(entry => entry[Root.Length..])
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path} {(Directory.Exists(OnDisk(path)) ? "folder" : "file")} {Modes.Octal(Mode(path))} {Owners.GetValueOrDefault(path, "root:root")}")];

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
            "docker" when arguments is ["container", "inspect", "--format", _, var name] => Docker(() => Containers.TryGetValue(name, out var container)
                ? Answer(container.State == "running" ? EmulatorHealth : container.State)
                : new CommandResult(1, string.Empty, $"Error response from daemon: No such container: {name}\n")),
            "docker" when arguments is ["exec", "-i", var name, "curl", ..] => Docker(() => Controller(name, command)),
            "docker" when arguments is ["context", "show"] => Docker(() => Answer("default")),
            "docker" when arguments is ["pull", "--platform", _, _] => Docker(() => Answer("Status: Downloaded newer image")),
            "docker" when arguments is ["network", "inspect", var network] => Docker(() => Networks.Contains(network)
                ? Answer("[]")
                : new CommandResult(1, "[]\n", $"Error response from daemon: network {network} not found\n")),
            "docker" when arguments is ["network", "create", var network] => Docker(() => Networks.Add(network) ? Answer(new string('n', 64)) : new CommandResult(1, string.Empty, "already exists\n")),
            "docker" when arguments is ["stop", var name] => Docker(() => Containers.TryGetValue(name, out var container)
                ? Answer(name, after: () => Containers[name] = container with { State = "exited" })
                : new CommandResult(1, string.Empty, $"Error response from daemon: No such container: {name}\n")),
            "docker" when arguments is ["rm", var name] => Docker(() => Containers.Remove(name) ? Answer(name) : new CommandResult(1, string.Empty, $"Error response from daemon: No such container: {name}\n")),
            "docker" when arguments is ["run", "-d", "--name", MachineSurveyor.HatEmulatorContainer, ..] => Docker(() => RunEmulator(arguments)),
            "bash" when arguments is [var script, ..] && script.EndsWith("/" + DeployScript.FileName, StringComparison.Ordinal) => Deploy(script, arguments.Skip(1).ToArray(), command, cancellationToken),
            "systemctl" when arguments is ["is-enabled", MachineSurveyor.KioskUnit] => !Exists(MachineSurveyor.KioskUnitFile)
                ? new CommandResult(1, string.Empty, $"Failed to get unit file state for {MachineSurveyor.KioskUnit}: No such file or directory\n")
                : Answer(Kiosk.Enabled ? "enabled" : "disabled", Kiosk.Enabled ? 0 : 1),
            "systemctl" when arguments is ["is-active", MachineSurveyor.KioskUnit] => Answer(
                !Exists(MachineSurveyor.KioskUnitFile) ? "inactive" : KioskRestarting ? "activating" : Kiosk.Active ? "active" : "inactive",
                Exists(MachineSurveyor.KioskUnitFile) && Kiosk.Active && !KioskRestarting ? 0 : 3),
            "systemctl" when arguments is ["is-active", MachineSurveyor.DisplayManagerUnit] => Answer(DisplayManagerActive ? "active" : "inactive", DisplayManagerActive ? 0 : 3),
            "systemctl" when arguments is ["is-enabled", MachineSurveyor.DisplayManagerUnit] => DisplayManagerEnabled
                ? Answer("alias")
                : new CommandResult(1, string.Empty, $"Failed to get unit file state for {MachineSurveyor.DisplayManagerUnit}: No such file or directory\n"),
            "systemctl" when arguments is ["get-default"] => Answer(DefaultTarget),
            "systemctl" when arguments is ["show", "-p", "NeedDaemonReload", "--value", MachineSurveyor.KioskUnit]
                => Answer(_loadedKioskUnit is not null && Exists(MachineSurveyor.KioskUnitFile) && Read(MachineSurveyor.KioskUnitFile) != _loadedKioskUnit ? "yes" : "no"),
            "systemctl" when arguments is ["show", "-p", "ActiveEnterTimestamp", "--timestamp=us+utc", "--value", MachineSurveyor.KioskUnit]
                => Answer(KioskStartedAt is { } started ? started.UtcDateTime.ToString("ddd yyyy-MM-dd HH:mm:ss.ffffff 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : string.Empty),
            "systemctl" when arguments is ["daemon-reload"] => Answer(string.Empty, after: () =>
            {
                DaemonReloads++;
                _loadedKioskUnit = Exists(MachineSurveyor.KioskUnitFile) ? Read(MachineSurveyor.KioskUnitFile) : null;
            }),
            "systemctl" when arguments is ["enable", MachineSurveyor.KioskUnit] => KioskService(() => Kiosk = Kiosk with { Enabled = true }),
            "systemctl" when arguments is ["restart", MachineSurveyor.KioskUnit] => KioskService(() =>
            {
                Kiosk = Kiosk with { Active = true };
                KioskStartedAt = DateTimeOffset.UtcNow;
                KioskStarts++;
            }),
            "udevadm" when arguments is ["trigger", "--action=add", "--subsystem-match=backlight"] => Answer(string.Empty, after: () => BacklightTriggers++),
            "dpkg-query" when Packages is not null && arguments is ["-W", "-f", "${Package} ${db:Status-Status}\n", ..] => PackageQuery(arguments.Skip(3).ToArray()),
            "apt-get" when Packages is not null && arguments is ["update"] => Answer("Reading package lists... Done"),
            "apt-get" when Packages is not null && arguments is ["install", "-y", "--no-install-recommends", ..] => Answer($"Setting up {string.Join(", ", arguments.Skip(3))}", after: () =>
            {
                AptInstalls.Add((arguments.Skip(3).ToArray(), command.Environment));
                Packages.UnionWith(arguments.Skip(3));
            }),
            "getent" when arguments is ["passwd", var name] => Users.ContainsKey(name)
                ? Answer($"{name}:x:999:999::/nonexistent:/usr/sbin/nologin")
                : new CommandResult(2, string.Empty, string.Empty),
            "id" when arguments is ["-nG", var name] => Users.TryGetValue(name, out var groups)
                ? Answer(string.Join(' ', groups))
                : new CommandResult(1, string.Empty, $"id: '{name}': no such user\n"),
            "getent" when arguments is ["group", var name] => Groups.Contains(name)
                ? Answer($"{name}:x:998:")
                : new CommandResult(2, string.Empty, string.Empty),
            "useradd" when arguments is ["--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", var name] => AddUser(name, makeGroup: true),
            "useradd" when arguments is ["--system", "--gid", var group, "--no-create-home", "--shell", "/usr/sbin/nologin", var name] => Groups.Contains(group)
                ? AddUser(name, makeGroup: false)
                : new CommandResult(6, string.Empty, $"useradd: group '{group}' does not exist\n"),
            "groupadd" when arguments is ["--system", var name] => Groups.Add(name)
                ? Answer(string.Empty)
                : new CommandResult(9, string.Empty, $"groupadd: group '{name}' already exists\n"),
            "usermod" when arguments is ["-aG", var groups, var name] => !Users.TryGetValue(name, out var memberOf)
                ? new CommandResult(6, string.Empty, $"usermod: user '{name}' does not exist\n")
                : groups.Split(',').FirstOrDefault(group => !Groups.Contains(group)) is { } unknown
                ? new CommandResult(6, string.Empty, $"usermod: group '{unknown}' does not exist\n")
                : Answer(string.Empty, after: () => memberOf.AddRange(groups.Split(',').Where(group => !memberOf.Contains(group)))),
            "stat" when arguments is ["-c", "%U:%G", "--", var path] => Exists(path)
                ? Answer(Owners.GetValueOrDefault(path, "root:root"))
                : new CommandResult(1, string.Empty, $"stat: cannot statx '{path}': No such file or directory\n"),
            "chown" when arguments is [var owner, "--", var path] => Chown(owner, path),
            "sw_vers" when arguments is ["-productVersion"] && Os == InstallerOs.MacOS => Answer("15.6.1"),
            _ when Programs.GetValueOrDefault("hvo-roof") == command.Program && arguments is ["--version"] => Answer("4.0.0+0123456789abcdef"),
            _ => ClientCommand(command) ?? LifecycleCommand(command)
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
        foreach (var served in ServedCertificates.Values)
        {
            served.Dispose();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // systemctl enable and restart: they fail for a unit with no file, and load one systemd has not read yet.
    private CommandResult KioskService(Action act)
    {
        if (!Exists(MachineSurveyor.KioskUnitFile))
        {
            return new CommandResult(1, string.Empty, $"Failed to enable unit: Unit file {MachineSurveyor.KioskUnit} does not exist.\n");
        }

        _loadedKioskUnit ??= Read(MachineSurveyor.KioskUnitFile);
        act();
        return Answer(string.Empty);
    }

    // dpkg-query -W: a line for each package dpkg knows, and exit 1 for any it does not.
    private CommandResult PackageQuery(string[] names)
    {
        var known = names.Where(Packages!.Contains).ToArray();
        var unknown = names.Where(name => !Packages!.Contains(name)).ToArray();
        return new CommandResult(
            unknown.Length > 0 ? 1 : 0,
            string.Concat(known.Select(name => $"{name} installed\n")),
            string.Concat(unknown.Select(name => $"dpkg-query: no packages found matching {name}\n")));
    }

    // useradd: --user-group makes a group named for the user, and refuses when one is there already.
    private CommandResult AddUser(string name, bool makeGroup)
    {
        if (Users.ContainsKey(name))
        {
            return new CommandResult(9, string.Empty, $"useradd: user '{name}' already exists\n");
        }

        if (makeGroup && !Groups.Add(name))
        {
            return new CommandResult(9, string.Empty, $"useradd: group {name} exists - if you want to add this user to that group, use -g.\n");
        }

        Users.Add(name, [name]);
        return Answer(string.Empty);
    }

    // chown user:group: both must be known, and so must the file.
    private CommandResult Chown(string owner, string path)
    {
        var parts = owner.Split(':');
        if (parts.Length != 2 || !Users.ContainsKey(parts[0]))
        {
            return new CommandResult(1, string.Empty, $"chown: invalid user: '{owner}'\n");
        }

        if (!Groups.Contains(parts[1]))
        {
            return new CommandResult(1, string.Empty, $"chown: invalid group: '{owner}'\n");
        }

        if (!Exists(path))
        {
            return new CommandResult(1, string.Empty, $"chown: cannot access '{path}': No such file or directory\n");
        }

        Owners[path] = owner;
        return Answer(string.Empty);
    }

    private CommandResult Docker(Func<CommandResult> answer)
        => DockerError is { } error ? new CommandResult(1, string.Empty, error + "\n")
            : DockerVersion is null ? new CommandResult(CommandResult.NotFound, string.Empty, "docker: not found")
            : answer();

    private static CommandResult Answer(string output, int exitCode = 0) => new(exitCode, output + "\n", string.Empty);

    private static CommandResult Answer(string output, Action after)
    {
        after();
        return Answer(output);
    }

    // The controller's Status from inside its container, as curl prints it with -w '\n%{http_code}'.
    // The controller's API from inside its container, as curl reads its config from standard input (-K -).
    private CommandResult Controller(string name, CommandLine command)
    {
        if (!Containers.TryGetValue(name, out var container) || container.State != "running")
        {
            return new CommandResult(1, string.Empty, $"Error response from daemon: container {name} is not running\n");
        }

        command.Arguments.Should().ContainInOrder(["-K", "-"], "curl reads its key and body from standard input, never from its command line");
        var config = CurlConfig(command.Input ?? string.Empty);
        var key = config.Where(option => option.Name == "header" && option.Value.StartsWith("X-Api-Key: ", StringComparison.Ordinal))
            .Select(option => option.Value["X-Api-Key: ".Length..]).SingleOrDefault() ?? string.Empty;
        var body = config.Where(option => option.Name == "data-raw").Select(option => option.Value).SingleOrDefault();
        var url = command.Arguments.Last();
        if (url == ControllerApi.Address + ControllerProbe.StatusPath)
        {
            if (StatusJson is null)
            {
                return new CommandResult(7, "\n000", "curl: (7) Failed to connect to localhost port 8080\n");
            }

            return ControllerKeys.Contains(key)
                ? new CommandResult(0, StatusJson + "\n200", string.Empty)
                : new CommandResult(0, "{\"title\":\"Unauthorized\"}\n401", string.Empty);
        }

        if (url == ControllerApi.Address + FirstAdminStep.UsersPath && body is not null)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var request = new FakeUserRequest(
                root.GetProperty("name").GetString()!,
                root.GetProperty("role").GetString()!,
                root.GetProperty("password").GetString()!,
                root.TryGetProperty("pin", out var pin) && pin.ValueKind == JsonValueKind.String ? pin.GetString() : null,
                key);
            UserRequests.Add(request);
            if (!ControllerKeys.Contains(key))
            {
                return new CommandResult(0, "{\"title\":\"Unauthorized\"}\n401", string.Empty);
            }

            if (UsersAnswer is { } answer)
            {
                return new CommandResult(0, $"{{\"title\":\"Refused\",\"detail\":\"The request was refused ({answer}).\"}}\n{answer}", string.Empty);
            }

            if (People.ContainsKey(request.Name))
            {
                return new CommandResult(0, "{\"title\":\"Conflict\",\"detail\":\"A person has that name.\"}\n409", string.Empty);
            }

            WithPerson(request.Name, request.Role);
            return new CommandResult(0, $"{{\"name\":\"{request.Name}\",\"role\":\"{request.Role}\"}}\n201", string.Empty);
        }

        var method = config.Where(option => option.Name == "request").Select(option => option.Value).SingleOrDefault();
        var person = ControllerApi.Address + FirstAdminStep.UsersPath + "/";
        if (method == "PUT" && url.StartsWith(person, StringComparison.Ordinal) && body is not null)
        {
            var named = Uri.UnescapeDataString(url[person.Length..]);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            (root.TryGetProperty("password", out var password) ? password.ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null, "setting a PIN leaves the person's password as it is");
            (root.TryGetProperty("removePassword", out var remove) && remove.GetBoolean()).Should().BeFalse();
            var request = new FakePinRequest(named, root.GetProperty("role").GetString()!, root.GetProperty("pin").GetString()!, key);
            PinRequests.Add(request);
            if (!ControllerKeys.Contains(key))
            {
                return new CommandResult(0, "{\"title\":\"Unauthorized\"}\n401", string.Empty);
            }

            if (!People.TryGetValue(named, out var role))
            {
                return new CommandResult(0, "{\"title\":\"Not Found\",\"detail\":\"No person has that name.\"}\n404", string.Empty);
            }

            request.Role.Should().Be(role, "the installer keeps the person's role");
            WithPerson(named, role, request.Pin);
            return new CommandResult(0, $"{{\"name\":\"{named}\",\"role\":\"{role}\",\"hasPin\":true}}\n200", string.Empty);
        }

        Unexpected.Enqueue(command);
        return new CommandResult(0, "{\"title\":\"Not Found\"}\n404", string.Empty);
    }

    // curl's config lines, name = "value", with the value's backslashes and quotes escaped.
    private static List<(string Name, string Value)> CurlConfig(string text)
    {
        var options = new List<(string Name, string Value)>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "^(\\S+) = \"(.*)\"$");
            match.Success.Should().BeTrue($"each line of curl's config is name = \"value\" (line {options.Count + 1})");
            options.Add((match.Groups[1].Value, System.Text.RegularExpressions.Regex.Replace(match.Groups[2].Value, "\\\\(.)", "$1")));
        }

        return options;
    }

    /// <summary>A person in the controller's identity store, as the controller keeps it, with a PIN's hash when they have one.</summary>
    public FakeMachine WithPerson(string name, string role, string? pin = null)
    {
        People[name] = role;
        if (pin is not null)
        {
            PeoplesPins[name] = pin;
        }

        var layout = ControllerLayout.For(Machine);
        Machine.CreateDirectory(layout.Identity, Modes.PrivateFolder);
        var users = People.Select(person => new
        {
            name = person.Key,
            role = person.Value,
            pinHash = PeoplesPins.TryGetValue(person.Key, out var hashed) ? Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(hashed))) : null
        });
        Machine.WriteAtomically(layout.IdentityFile, JsonSerializer.Serialize(new { users, apiKeys = Array.Empty<object>() }), Modes.PrivateFile);
        return this;
    }

    private CommandResult RunEmulator(IReadOnlyList<string> arguments)
    {
        string? Option(string name) => arguments.Select((argument, index) => (argument, index)).Where(pair => pair.argument == name).Select(pair => arguments[pair.index + 1]).FirstOrDefault();
        var settings = arguments.Select((argument, index) => (argument, index)).Where(pair => pair.argument == "--env")
            .Select(pair => arguments[pair.index + 1].Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1]);
        var image = arguments[^1];
        Containers[MachineSurveyor.HatEmulatorContainer] = new FakeContainer
        {
            Network = Option("--network"),
            PublishAddress = Option("-p")!.Split(':')[0],
            Digest = image[(image.IndexOf('@', StringComparison.Ordinal) + 1)..],
            Settings = settings
        };
        return Answer(new string('f', 64));
    }

    // The deploy script, as far as the installer sees it: it stops the running controller only with a key it knows,
    // reads its key file as the script does, and leaves the new controller running with the settings it was given.
    private CommandResult Deploy(string script, IReadOnlyList<string> options, CommandLine command, CancellationToken cancellationToken)
    {
        var environment = command.Environment ?? new Dictionary<string, string>();
        Deploys.Add(environment);
        DeploysCancellable.Add(cancellationToken.CanBeCanceled);
        DuringDeploy?.Invoke();
        File.Exists(OnDisk(script)).Should().BeTrue("the installer writes the deploy script before it runs it");
        if (DeployFailure is { } failure)
        {
            return new CommandResult(DeployFailureExitCode, "[deploy] Pre-flight\n", failure + "\n");
        }

        var keyFile = environment["OPERATOR_KEY_FILE"];
        if (!File.Exists(OnDisk(keyFile)) || (Mode(keyFile) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
        {
            return new CommandResult(1, string.Empty, $"[deploy] ERROR: OPERATOR_KEY_FILE '{keyFile}' must exist with mode 600.\n");
        }

        var key = Read(keyFile).TrimEnd('\n', '\r');
        if (Containers.TryGetValue(MachineSurveyor.ControllerContainer, out var running) && running.State == "running" && !ControllerKeys.Contains(key))
        {
            return new CommandResult(1, string.Empty, "[deploy] ERROR: the running controller did not accept the Stop: HTTP 401. Nothing was changed.\n");
        }

        DeployOptions.Add(string.Join(' ', options));
        if (options is ["--stop"])
        {
            // As the script's --stop: the verified Stop above, then the container stopped and kept.
            if (Containers.TryGetValue(MachineSurveyor.ControllerContainer, out var stopping) && stopping.State == "running")
            {
                Containers[MachineSurveyor.ControllerContainer] = stopping with { State = "exited" };
                return new CommandResult(0, "[deploy] Stopped the controller after a verified Stop\n", string.Empty);
            }

            return new CommandResult(0, "[deploy] The controller is not running: nothing to stop\n", string.Empty);
        }

        if (options is ["--rollback"])
        {
            // As the script's --rollback: the one kept put back, the one it replaces kept in its turn.
            if (!Containers.TryGetValue(ControllerRollbackStep.PreviousContainer, out var previous))
            {
                return new CommandResult(1, string.Empty, $"[deploy] ERROR: there is no {ControllerRollbackStep.PreviousContainer} to roll back to.\n");
            }

            if (Containers.TryGetValue(MachineSurveyor.ControllerContainer, out var replaced))
            {
                Containers[ControllerRollbackStep.PreviousContainer] = replaced with { State = "exited" };
            }
            else
            {
                Containers.Remove(ControllerRollbackStep.PreviousContainer);
            }

            Containers[MachineSurveyor.ControllerContainer] = previous with { State = "running", StartedAt = DateTimeOffset.UtcNow };
            ControllerKeys.Clear();
            ControllerKeys.UnionWith(ApiKeyValues());
            return new CommandResult(0, "[deploy] Rolled back\n", string.Empty);
        }

        if (options.Count > 0)
        {
            return new CommandResult(2, string.Empty, $"[deploy] ERROR: unknown option {options[0]}\n");
        }

        // The controller it replaces is stopped and kept (an older one kept goes).
        if (Containers.TryGetValue(MachineSurveyor.ControllerContainer, out var old))
        {
            Containers[ControllerRollbackStep.PreviousContainer] = old with { State = "exited" };
        }

        var image = environment["IMAGE_REF"];
        var extra = environment["EXTRA_DOCKER_ARGS"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { [MachineSurveyor.TelemetryEndpointSetting] = environment["OTEL_EXPORTER_OTLP_ENDPOINT"] };
        string? network = null;
        for (var index = 0; index < extra.Length - 1; index++)
        {
            if (extra[index] == "--env")
            {
                var setting = extra[index + 1].Split('=', 2);
                settings[setting[0]] = setting[1];
            }
            else if (extra[index] == "--network")
            {
                network = extra[index + 1];
            }
        }

        var https = environment["HTTPS_CERT_DIR"].Length > 0;
        Containers[MachineSurveyor.ControllerContainer] = new FakeContainer
        {
            Emulated = environment["HAT_EMULATOR_ENDPOINT"].Length > 0,
            HatEmulatorEndpoint = environment["HAT_EMULATOR_ENDPOINT"] is { Length: > 0 } endpoint ? endpoint : HatEmulatorStep.Endpoint,
            Version = image[(image.LastIndexOf(':', image.IndexOf('@', StringComparison.Ordinal)) + 1)..image.IndexOf('@', StringComparison.Ordinal)],
            Digest = image[(image.IndexOf('@', StringComparison.Ordinal) + 1)..],
            Https = https,
            ApiPort = int.Parse(environment[https ? "HTTPS_HOST_PORT" : "HOST_PORT"], System.Globalization.CultureInfo.InvariantCulture),
            WebPort = int.Parse(environment["WEB_HOST_PORT"], System.Globalization.CultureInfo.InvariantCulture),
            PublishAddress = environment["PUBLISH_ADDRESS"] is { Length: > 0 } address ? address : null,
            Network = network,
            AllowedHosts = environment["ALLOWED_HOSTS"] is { Length: > 0 } hosts ? hosts : null,
            Settings = settings,
            StartedAt = DateTimeOffset.UtcNow
        };

        // The new controller reads the keys in the secrets folder when it starts, and serves the certificate in its file.
        ControllerKeys.Clear();
        ControllerKeys.UnionWith(ApiKeyValues());
        var apiPort = Containers[MachineSurveyor.ControllerContainer].ApiPort!.Value;
        ServedCertificates.Remove(apiPort);
        if (https
            && File.Exists(OnDisk(Path.Join(environment["HTTPS_CERT_DIR"], environment["HTTPS_CERT_FILE"])))
            && ControllerCertificates.LoadPfx(
                File.ReadAllBytes(OnDisk(Path.Join(environment["HTTPS_CERT_DIR"], environment["HTTPS_CERT_FILE"]))),
                Read(Path.Join(environment["SECRETS_DIR"], "Kestrel__Certificates__Default__Password"))) is { } served)
        {
            ServedCertificates[apiPort] = served;
        }
        return new CommandResult(0, "[deploy] Pre-flight passed\n[deploy] Deployed\n", string.Empty);
    }

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
        var ports = new Dictionary<string, object[]>();
        if (IsController(name))
        {
            // As the deploy script publishes them.
            var apiPort = container.ApiPort ?? (container.Https ? ControllerSettings.DefaultHttpsPort : ControllerSettings.DefaultHttpPort);
            environment.Add(container.Https ? "ASPNETCORE_URLS=http://localhost:8080;https://+:8443" : "ASPNETCORE_URLS=http://+:8080");
            ports[container.Https ? "8443/tcp" : "8080/tcp"] = [new { HostIp = container.PublishAddress ?? string.Empty, HostPort = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }];
            ports["8088/tcp"] = [new { HostIp = container.PublishAddress ?? string.Empty, HostPort = container.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }];
            environment.Add($"HatEmulator__Enabled={(container.Emulated ? "true" : "false")}");
            if (container.Emulated)
            {
                var endpoint = container.HatEmulatorEndpoint.Split(':');
                environment.Add($"HatEmulator__Host={endpoint[0]}");
                environment.Add($"HatEmulator__Port={endpoint[1]}");
            }
        }
        else if (name == MachineSurveyor.HatEmulatorContainer)
        {
            // As the installer publishes it: its control API only.
            ports["5290/tcp"] = [new { HostIp = container.PublishAddress ?? string.Empty, HostPort = "5290" }];
        }

        if (container.AllowedHosts is { } allowed)
        {
            environment.Add($"AllowedHosts={allowed}");
        }

        environment.AddRange(container.Settings.Select(setting => $"{setting.Key}={setting.Value}"));

        if (container.Secret is { } secret)
        {
            environment.Add($"RoofControllerSecurity__ApiKeys__0__Key={secret}");
        }

        var image = ImageOf(name, container);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                Name = "/" + name,
                Config = new { Image = image, Labels = labels, Env = environment },
                HostConfig = new { PortBindings = ports, NetworkMode = container.Network ?? "bridge" },
                State = new
                {
                    Status = container.State,

                    // As Docker writes it: nine digits of the second, and the year 1 for a container that never started.
                    StartedAt = container.StartedAt is { } started
                        ? started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) + "42Z"
                        : "0001-01-01T00:00:00Z"
                }
            }
        });
    }
}

/// <summary>How a run of hvo-roof-install ended, and what it wrote.</summary>
internal sealed record InstallerRun(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n--- out\n{Output}\n--- error\n{Error}";
}

/// <summary>A clock stopped at one moment; its timers still run on the system's.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
