using System.Globalization;
using System.Runtime.InteropServices;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Survey;

/// <summary>
/// What the installer found on the machine before asking anything: the platform, the Pi and its HAT's devices, what an
/// earlier install left (its records, <c>/etc/hvo-roof</c>, the containers, the kiosk's service, hvo-roof and the Mac
/// app), and Docker. Nothing is changed to find it.
/// </summary>
public sealed record MachineSurvey
{
    public required InstallerOs Os { get; init; }

    public required Architecture Architecture { get; init; }

    public required string RuntimeIdentifier { get; init; }

    public required string HostName { get; init; }

    public required bool IsRoot { get; init; }

    public required string UserName { get; init; }

    /// <summary>The operating system's name (PRETTY_NAME in /etc/os-release, or macOS and its version).</summary>
    public string? OsName { get; init; }

    /// <summary>The Raspberry Pi's model (/proc/device-tree/model), or null when this is not a Pi.</summary>
    public string? PiModel { get; init; }

    /// <summary>/dev/i2c-1: the HAT's relays and inputs.</summary>
    public bool HasI2c { get; init; }

    /// <summary>/dev/gpiomem: the Pi's GPIO.</summary>
    public bool HasGpioMemory { get; init; }

    /// <summary>/sys/class/thermal/thermal_zone0/temp: the Pi's temperature, which the controller reports.</summary>
    public bool HasThermalSensor { get; init; }

    /// <summary>The system's install record (/etc/hvo-roof/install.json): the controller, a rig or the kiosk.</summary>
    public InstallRecord? SystemRecord { get; init; }

    /// <summary>This user's install record: hvo-roof, the Mac app, or a rig on a Mac.</summary>
    public InstallRecord? UserRecord { get; init; }

    /// <summary>The controller's (or a rig's) recorded choices: the machine's record's, else the person's; null when neither records it.</summary>
    public ControllerSettings? RecordedController
        => new[] { SystemRecord, UserRecord }.FirstOrDefault(record => record is { Controller: not null } && InstallRoles.RunsController(record.Roles))?.Controller;

    /// <summary>Why an install record could not be read, when one is there but is not valid.</summary>
    public IReadOnlyList<string> RecordProblems { get; init; } = [];

    /// <summary>
    /// Why the machine's record cannot be read, when it is there but cannot be: it may say the machine drives the real
    /// HAT, so a test rig is not installed until it is fixed.
    /// </summary>
    public string? SystemRecordProblem { get; init; }

    /// <summary>/etc/hvo-roof exists: a controller set up by hand, or by an earlier installer.</summary>
    public bool HasSystemConfiguration { get; init; }

    public DockerSurvey Docker { get; init; } = DockerSurvey.Missing;

    /// <summary>The controller's container (roof-controller), when there is one.</summary>
    public ContainerSurvey? Controller { get; init; }

    /// <summary>The HAT emulator's container (hat-emulator), when there is one.</summary>
    public ContainerSurvey? HatEmulator { get; init; }

    /// <summary>The kiosk's service, when its unit is installed.</summary>
    public ServiceSurvey? Kiosk { get; init; }

    /// <summary>The screens a kiosk could draw on: on a 64-bit Linux machine with a display controller; null elsewhere.</summary>
    public DisplaySurvey? Display { get; init; }

    /// <summary>hvo-roof on the PATH, when it is there.</summary>
    public ProgramSurvey? Cli { get; init; }

    /// <summary>The Mac app, when it is in /Applications or ~/Applications.</summary>
    public ProgramSurvey? MacApp { get; init; }

    /// <summary>The controller's HTTPS certificate (the file the deploy script mounts), when there is one.</summary>
    public CertificateSurvey? Certificate { get; init; }

    /// <summary>The installer's certificate authority (the certificate clients trust), when there is one.</summary>
    public AuthoritySurvey? Authority { get; init; }

    public bool IsPi => PiModel?.StartsWith("Raspberry Pi", StringComparison.Ordinal) == true;

    /// <summary>True when this machine can drive the real HAT: a 64-bit Linux Pi with the HAT's three devices.</summary>
    public bool HasHat => IsPi && Os == InstallerOs.Linux && Architecture == Architecture.Arm64 && HasI2c && HasGpioMemory && HasThermalSensor;

    /// <summary>The HAT's devices this machine lacks, by path.</summary>
    public IEnumerable<string> MissingHatDevices()
    {
        if (!HasI2c)
        {
            yield return HatDevices.I2c;
        }

        if (!HasGpioMemory)
        {
            yield return HatDevices.GpioMemory;
        }

        if (!HasThermalSensor)
        {
            yield return HatDevices.ThermalSensor;
        }
    }
}

/// <summary>The paths of the devices the controller maps from a Pi to drive the HAT (as the deploy script maps them).</summary>
public static class HatDevices
{
    public const string I2c = "/dev/i2c-1";
    public const string GpioMemory = "/dev/gpiomem";
    public const string ThermalSensor = "/sys/class/thermal/thermal_zone0/temp";
    public const string PiModel = "/proc/device-tree/model";
}

/// <summary>
/// A machine's display outputs, as its kernel lists them (<c>/sys/class/drm/card1-DSI-1</c>), and whether a desktop has
/// the screen: the kiosk draws on it itself, and cannot while a display manager does.
/// </summary>
/// <param name="Outputs">Each output (DSI-1, HDMI-A-1) and what its status says: connected, disconnected or unknown. None when the kernel lists none.</param>
/// <param name="DisplayManagerActive">True when display-manager.service (a desktop's login screen) is running.</param>
/// <param name="DisplayManagerAtBoot">True when it is enabled and the Pi boots to graphical.target, so it starts at the next boot.</param>
public sealed record DisplaySurvey(IReadOnlyList<(string Name, string Status)> Outputs, bool DisplayManagerActive, bool DisplayManagerAtBoot = false)
{
    /// <summary>The outputs with a screen on them, or that cannot tell (a panel may say unknown).</summary>
    public IReadOnlyList<string> Screens => [.. Outputs.Where(output => output.Status is "connected" or "unknown").Select(output => output.Name)];
}

/// <summary>Docker, as the installer found it.</summary>
public sealed record DockerSurvey
{
    public static DockerSurvey Missing { get; } = new() { Problem = "Docker is not installed." };

    /// <summary>The engine's version, when it answered.</summary>
    public string? Version { get; init; }

    /// <summary>Compose v2's version, when the plugin is there.</summary>
    public string? ComposeVersion { get; init; }

    /// <summary>Why Docker cannot be used, when it cannot: not installed, not running, or not allowed.</summary>
    public string? Problem { get; init; }

    /// <summary>The limits Docker says it can put on a container, and the CPUs and memory it reports; null when it did not answer.</summary>
    public DockerLimits? Limits { get; init; }

    /// <summary>The controller's desired limits the deploy script keeps for its next deploy; null when there are none.</summary>
    public DesiredLimits? DesiredLimits { get; init; }

    public bool IsUsable => Problem is null && Version is not null;
}

/// <summary>
/// What one <c>docker info</c> says about the limits Docker enforces on a container (MemoryLimit, CPUCfsQuota, PidsLimit
/// and SwapLimit), about the machine (NCPU, MemTotal), its cgroup version, the host name Docker gives its machine, and
/// Docker's own warnings; null for what it did not say. Docker drops a limit it cannot enforce with only a warning, so
/// the installer refuses such a machine before it deploys the controller with its limits, as the deploy script does.
/// <see cref="CgroupControllers"/> is the kernel's cgroup v2 controllers (/sys/fs/cgroup/cgroup.controllers), read by
/// the survey only when Docker runs on this machine.
/// </summary>
public sealed record DockerLimits(bool? Memory, bool? Cpu, bool? Pids, bool? Swap, int? Cpus, long? MemoryBytes)
{
    /// <summary>
    /// The <c>docker info --format</c> template that reads them: the fields comma-separated on the first line, so an empty
    /// field keeps its place, then Docker's warnings, one a line. Docker 29's field is CPUCfsQuota (its JSON key CpuCfsQuota).
    /// </summary>
    public const string Format = "{{.MemoryLimit}},{{.CPUCfsQuota}},{{.PidsLimit}},{{.SwapLimit}},{{.NCPU}},{{.MemTotal}},{{.CgroupVersion}},{{.Name}}\n{{range .Warnings}}{{println .}}{{end}}";

    /// <summary>The cgroup version Docker reports (CgroupVersion), such as 2.</summary>
    public string? CgroupVersion { get; init; }

    /// <summary>The host name Docker gives the machine it runs on (Name).</summary>
    public string? HostName { get; init; }

    /// <summary>Docker's warnings (Warnings), such as "WARNING: No memory limit support".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The kernel's cgroup v2 controllers, when Docker runs on this machine and the file could be read.</summary>
    public string? CgroupControllers { get; init; }

    /// <summary>The same answer: the warnings compared one by one, not as the same list.</summary>
    public bool Equals(DockerLimits? other)
        => other is not null
            && (Memory, Cpu, Pids, Swap, Cpus, MemoryBytes, CgroupVersion, HostName, CgroupControllers)
                == (other.Memory, other.Cpu, other.Pids, other.Swap, other.Cpus, other.MemoryBytes, other.CgroupVersion, other.HostName, other.CgroupControllers)
            && Warnings.SequenceEqual(other.Warnings);

    public override int GetHashCode()
        => HashCode.Combine(HashCode.Combine(Memory, Cpu, Pids, Swap, Cpus, MemoryBytes), CgroupVersion, HostName, CgroupControllers, Warnings.Count);

    /// <summary>The answer to <see cref="Format"/>; null when it is not one.</summary>
    public static DockerLimits? Parse(string output)
    {
        var lines = (output ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var fields = lines[0].Trim().Split(',');
        if (fields.Length != 8)
        {
            return null;
        }

        static bool? Flag(string value) => bool.TryParse(value, out var flag) ? flag : null;
        static string? Text(string value) => value.Trim() is { Length: > 0 } text && text != "<no value>" ? text : null;
        return new DockerLimits(
            Flag(fields[0]),
            Flag(fields[1]),
            Flag(fields[2]),
            Flag(fields[3]),
            int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var cpus) ? cpus : null,
            long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var memory) ? memory : null)
        {
            CgroupVersion = Text(fields[6]),
            HostName = Text(fields[7]),
            Warnings = [.. lines.Skip(1).Select(line => line.Trim()).Where(line => line.Length > 0)]
        };
    }
}

/// <summary>
/// The controller's desired CPU and memory limits, as the deploy script keeps them for its next deploy (#149, review
/// ROOF-151-R1 F2): the ones the last deploy or rollback gave, whether or not the host could enforce them, in the labels
/// of a Docker volume of their own (<c>roof-controller-limits</c>), which nothing mounts. The installer gives the deploy
/// script the limits its record holds; it reads these to say when someone gave the script others by hand.
/// </summary>
public sealed record DesiredLimits(string? Cpus, string? Memory)
{
    /// <summary>The <c>docker volume inspect --format</c> template that reads them, comma-separated.</summary>
    public const string Format = "{{index .Labels \"io.hvo.roof.desired-cpus\"}},{{index .Labels \"io.hvo.roof.desired-memory\"}}";

    /// <summary>The volume that holds the desired limits of the controller container <paramref name="container"/>.</summary>
    public static string Volume(string container) => $"{container}-limits";

    /// <summary>The answer to <see cref="Format"/>; null when it holds neither.</summary>
    public static DesiredLimits? Parse(string output)
    {
        var fields = (output ?? string.Empty).Trim().Split(',');
        static string? Text(string value) => value.Trim() is { Length: > 0 } text && text != "<no value>" ? text : null;
        return fields.Length == 2 && (Text(fields[0]), Text(fields[1])) is var (cpus, memory) && (cpus ?? memory) is not null
            ? new DesiredLimits(cpus, memory)
            : null;
    }
}

/// <summary>Who made a container.</summary>
public enum ContainerOrigin
{
    /// <summary>The deploy script (or this installer through it): the installer adopts it.</summary>
    DeployScript,

    /// <summary>Docker Compose: the installer explains it and does not replace it.</summary>
    Compose
}

/// <summary>A container, as <c>docker container inspect</c> described it.</summary>
public sealed record ContainerSurvey
{
    public required string Name { get; init; }

    /// <summary>The image it runs, as it was given (with its digest when deployed by one, its ID when loaded from an archive).</summary>
    public required string Image { get; init; }

    /// <summary>
    /// The ID Docker gives the image it runs (the container's own <c>Image</c>, sha256:…), however it was given and
    /// whatever its labels say; null when Docker does not say.
    /// </summary>
    public string? ImageId { get; init; }

    /// <summary>
    /// The release's image its label (<see cref="MachineSurveyor.ReleaseImageLabel"/>) names, by its reference: the
    /// deploy script and the installer give a container that label when they run an image loaded from the release's
    /// offline archive by its ID. Null when it has none. Anyone can set a label, so it only claims the release:
    /// <see cref="Runs"/> checks the claim against <see cref="ImageId"/>.
    /// </summary>
    public string? ReleaseImage { get; init; }

    /// <summary>running, exited, created…</summary>
    public required string State { get; init; }

    public required ContainerOrigin Origin { get; init; }

    /// <summary>The Compose project that made it, when Compose did.</summary>
    public string? ComposeProject { get; init; }

    /// <summary>The HAT emulator it uses (host:port), when it was deployed for one; null for the real HAT.</summary>
    public string? HatEmulator { get; init; }

    /// <summary>The image's version label (org.opencontainers.image.version), when it has one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// The image's state contract label (io.hvo.roof.state-contracts), when it has one: what the release it runs writes
    /// and reads of each store, for telling whether an upgrade makes a breaking change.
    /// </summary>
    public string? StateContracts { get; init; }

    /// <summary>The image's identity schema label (io.hvo.roof.identity-schema), which images from before state contracts have.</summary>
    public string? IdentitySchema { get; init; }

    /// <summary>The ports it publishes on the host, in order.</summary>
    public IReadOnlyList<int> PublishedPorts { get; init; } = [];

    /// <summary>Whether it serves HTTPS (from ASPNETCORE_URLS, as the deploy script sets it); null when that is not set.</summary>
    public bool? ServesHttps { get; init; }

    /// <summary>The host names it answers to (AllowedHosts, as the deploy script's ALLOWED_HOSTS sets it); null for any (<c>*</c>).</summary>
    public string? AllowedHosts { get; init; }

    /// <summary>The one address it publishes its ports on (the deploy script's PUBLISH_ADDRESS); null for every address.</summary>
    public string? PublishAddress { get; init; }

    /// <summary>The Docker network it is on (the rig's hvo-emulator); null for Docker's default.</summary>
    public string? Network { get; init; }

    /// <summary>The key file the web UI's Stop reads (RoofWeb__StopKeyFile), as the container names it; null when none is set.</summary>
    public string? WebStopKeyFile { get; init; }

    /// <summary>Where it exports telemetry (OTEL_EXPORTER_OTLP_ENDPOINT): empty when export is off; null when it is not set.</summary>
    public string? TelemetryEndpoint { get; init; }

    /// <summary>Whether the optional host authentication adapter is enabled.</summary>
    public bool LinuxAuthEnabled { get; init; }

    /// <summary>The HAT emulator's time scale (Emulator__TimeScale), when set.</summary>
    public string? EmulatorTimeScale { get; init; }

    /// <summary>The HAT emulator's camera frame rate (Emulator__CameraFramesPerSecond), when set.</summary>
    public string? EmulatorCameraFramesPerSecond { get; init; }

    /// <summary>When it last started (State.StartedAt); null when it never has. It read its secrets then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Its CPU limit as Docker counts it (HostConfig.NanoCpus); 0 for none.</summary>
    public long NanoCpus { get; init; }

    /// <summary>Its memory limit in bytes (HostConfig.Memory); 0 for none.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>Its process limit (HostConfig.PidsLimit); null for none.</summary>
    public long? PidsLimit { get; init; }

    /// <summary>Whether its root filesystem is read-only (HostConfig.ReadonlyRootfs), as the deploy script runs the controller.</summary>
    public bool ReadOnlyRootFilesystem { get; init; }

    public bool IsRunning => State == "running";

    /// <summary>
    /// True when it has stopped and stays stopped: made, exited or dead. A paused or restarting container is neither
    /// running nor stopped: it may run the roof again at any moment, and Docker does not remove it.
    /// </summary>
    public bool IsStopped => State is "created" or "exited" or "dead";

    /// <summary>The digest it was deployed by (sha256:…); null for a tag alone, or an image run by its ID.</summary>
    public string? ImageDigest => DigestOf(Image);

    /// <summary>
    /// True when it runs <paramref name="image"/>, a release's image, for <paramref name="platform"/> (linux/arm64):
    /// deployed by the image's digest, or loaded from the release's offline archive for the platform and run by its ID.
    /// For the second, its label claims the release's image, and Docker's ID for the image it runs
    /// (<see cref="ImageId"/>) must be one the release gives that archive (<see cref="Deployment.ReleaseImage.LoadedIds"/>):
    /// a container of another image carrying the label (set by hand, copied, or inherited from its image) does not run
    /// the release's.
    /// </summary>
    public bool Runs(Deployment.ReleaseImage image, string platform)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (ImageDigest is { } digest)
        {
            return digest == image.Digest;
        }

        return ReleaseImage is { } claimed && DigestOf(claimed) == image.Digest
            && image.ArchiveFor(platform) is { } archive
            && ImageId is { } id && image.LoadedIds(archive).Contains(id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Why it does not run <paramref name="image"/> though its label says it does (<see cref="Runs"/>): Docker gives the
    /// image it runs an ID the release does not give its archive for <paramref name="platform"/>. Null when it runs the
    /// image, or its label does not name it.
    /// </summary>
    public string? FalseClaim(Deployment.ReleaseImage image, string platform)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ImageDigest is null && ReleaseImage is { } claimed && DigestOf(claimed) == image.Digest && !Runs(image, platform)
            ? $"its label names {claimed}, but it runs {(ImageId is { } id ? $"the image {id}" : "an image Docker gives no ID")}, not one with an ID release.json gives that image for {platform}"
            : null;
    }

    private static string? DigestOf(string image)
        => image.LastIndexOf('@') is > 0 and var at && image[(at + 1)..].StartsWith("sha256:", StringComparison.Ordinal) ? image[(at + 1)..] : null;
}

/// <summary>A systemd service.</summary>
public sealed record ServiceSurvey(string Unit, bool Enabled, bool Active);

/// <summary>A program the installer put in place, or found.</summary>
public sealed record ProgramSurvey(string Path, string? Version);

/// <summary>The controller's certificate, as the installer found it: what it says, never its key or password.</summary>
public sealed record CertificateSurvey
{
    public required string Path { get; init; }

    /// <summary>Why it cannot be checked: only root reads it, or its password does not open it.</summary>
    public string? Problem { get; init; }

    /// <summary>True when it could not be read because only root reads it: nothing is known about it.</summary>
    public bool NeedsRoot { get; init; }

    /// <summary>Its subject's common name.</summary>
    public string? Subject { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Its SHA-256 fingerprint.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The common name of the CA that issued it, or null when it signed itself.</summary>
    public string? Issuer { get; init; }

    /// <summary>True when this machine's CA (<see cref="MachineSurvey.Authority"/>) issued it.</summary>
    public bool FromAuthority { get; init; }

    /// <summary>
    /// True when a CA the installer made issued it, going by the issuer's name, even when that CA is no longer this
    /// machine's (its files were removed, or a new one made, or it was imported from another machine).
    /// </summary>
    public bool FromInstallerCa { get; init; }

    /// <summary>
    /// True when a CA other than this machine's issued it. With nothing recorded it is taken to be the person's own, so the
    /// installer never replaces it unasked; the record, when there is one, says whose it is.
    /// </summary>
    public bool IsTheirs => Issuer is not null && !FromAuthority;

    /// <summary>The DNS names and addresses it is for.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>The names clients use for the controller (as recorded, or by default) that it is not for.</summary>
    public IReadOnlyList<string> Uncovered { get; init; } = [];
}

/// <summary>The installer's certificate authority, as the installer found its certificate (its key is not read).</summary>
public sealed record AuthoritySurvey
{
    public required string Path { get; init; }

    /// <summary>Why it cannot be read.</summary>
    public string? Problem { get; init; }

    /// <summary>Its subject's common name.</summary>
    public string? Subject { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Its SHA-256 fingerprint: what a person checks when a client asks them to trust it.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The names and networks it may issue for (its name constraints); empty when it does not limit them.</summary>
    public IReadOnlyList<string> Permits { get; init; } = [];
}
