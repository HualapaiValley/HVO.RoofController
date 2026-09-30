using System.Runtime.InteropServices;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;

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

    /// <summary>hvo-roof on the PATH, when it is there.</summary>
    public ProgramSurvey? Cli { get; init; }

    /// <summary>The Mac app, when it is in /Applications or ~/Applications.</summary>
    public ProgramSurvey? MacApp { get; init; }

    public bool IsPi => PiModel is not null;

    /// <summary>True when this machine can drive the real HAT: a 64-bit Linux Pi with the HAT's three devices.</summary>
    public bool HasHat => Os == InstallerOs.Linux && Architecture == Architecture.Arm64 && HasI2c && HasGpioMemory && HasThermalSensor;

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

    public bool IsUsable => Problem is null && Version is not null;
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

    /// <summary>The image it runs, as it was given (with its digest when deployed by one).</summary>
    public required string Image { get; init; }

    /// <summary>running, exited, created…</summary>
    public required string State { get; init; }

    public required ContainerOrigin Origin { get; init; }

    /// <summary>The Compose project that made it, when Compose did.</summary>
    public string? ComposeProject { get; init; }

    /// <summary>The HAT emulator it uses (host:port), when it was deployed for one; null for the real HAT.</summary>
    public string? HatEmulator { get; init; }

    /// <summary>The image's version label (org.opencontainers.image.version), when it has one.</summary>
    public string? Version { get; init; }

    /// <summary>The ports it publishes on the host, in order.</summary>
    public IReadOnlyList<int> PublishedPorts { get; init; } = [];

    /// <summary>Whether it serves HTTPS (from ASPNETCORE_URLS, as the deploy script sets it); null when that is not set.</summary>
    public bool? ServesHttps { get; init; }

    public bool IsRunning => State == "running";
}

/// <summary>A systemd service.</summary>
public sealed record ServiceSurvey(string Unit, bool Enabled, bool Active);

/// <summary>A program the installer put in place, or found.</summary>
public sealed record ProgramSurvey(string Path, string? Version);
