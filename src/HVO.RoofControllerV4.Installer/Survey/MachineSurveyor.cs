using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;

namespace HVO.RoofControllerV4.Installer.Survey;

/// <summary>Looks at the machine and says what it found (<see cref="MachineSurvey"/>). It only reads and asks; it changes nothing.</summary>
public static partial class MachineSurveyor
{
    /// <summary>The controller's container, as the deploy script names it.</summary>
    public const string ControllerContainer = "roof-controller";

    /// <summary>A test rig's HAT emulator container.</summary>
    public const string HatEmulatorContainer = "hat-emulator";

    /// <summary>The kiosk's systemd unit.</summary>
    public const string KioskUnit = "hvo-roof-kiosk.service";

    /// <summary>Where the kiosk's unit file is.</summary>
    public const string KioskUnitFile = "/etc/systemd/system/" + KioskUnit;

    /// <summary>The Mac app's bundle name.</summary>
    public const string MacAppBundle = "HVO Roof.app";

    /// <summary>The label Docker Compose puts on each container it makes.</summary>
    public const string ComposeProjectLabel = "com.docker.compose.project";

    public const string VersionLabel = "org.opencontainers.image.version";

    public static async Task<MachineSurvey> SurveyAsync(InstallerMachine machine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var problems = new List<string>();
        var (systemRecord, systemProblem) = InstallRecord.Load(machine, InstallPaths.SystemRecord);
        var (userRecord, userProblem) = machine.IsRoot ? (null, null) : InstallRecord.Load(machine, InstallPaths.UserRecord(machine));
        problems.AddRange(new[] { systemProblem, userProblem }.OfType<string>());

        var docker = await SurveyDockerAsync(machine, cancellationToken).ConfigureAwait(false);
        var controller = docker.IsUsable ? await SurveyContainerAsync(machine, ControllerContainer, cancellationToken).ConfigureAwait(false) : null;
        var emulator = docker.IsUsable ? await SurveyContainerAsync(machine, HatEmulatorContainer, cancellationToken).ConfigureAwait(false) : null;

        return new MachineSurvey
        {
            Os = machine.Os,
            Architecture = machine.Architecture,
            RuntimeIdentifier = machine.RuntimeIdentifier,
            HostName = machine.HostName,
            IsRoot = machine.IsRoot,
            UserName = machine.UserName,
            OsName = await OsNameAsync(machine, cancellationToken).ConfigureAwait(false),
            PiModel = machine.ReadText(HatDevices.PiModel)?.Trim('\0', ' ', '\n') is { Length: > 0 } model ? model : null,
            HasI2c = machine.FileExists(HatDevices.I2c),
            HasGpioMemory = machine.FileExists(HatDevices.GpioMemory),
            HasThermalSensor = machine.FileExists(HatDevices.ThermalSensor),
            SystemRecord = systemRecord,
            UserRecord = userRecord,
            RecordProblems = problems,
            HasSystemConfiguration = machine.DirectoryExists("/etc/hvo-roof"),
            Docker = docker,
            Controller = controller,
            HatEmulator = emulator,
            Kiosk = await SurveyKioskAsync(machine, cancellationToken).ConfigureAwait(false),
            Cli = await SurveyCliAsync(machine, cancellationToken).ConfigureAwait(false),
            MacApp = SurveyMacApp(machine)
        };
    }

    private static async Task<string?> OsNameAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        if (machine.Os == InstallerOs.MacOS)
        {
            var version = await machine.Commands.RunAsync(new CommandLine("sw_vers", "-productVersion"), cancellationToken).ConfigureAwait(false);
            return version.Succeeded ? $"macOS {version.Output.Trim()}" : "macOS";
        }

        var release = machine.ReadText("/etc/os-release");
        var match = release is null ? null : PrettyName().Match(release);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    private static async Task<DockerSurvey> SurveyDockerAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var server = await machine.Commands.RunAsync(new CommandLine("docker", "version", "--format", "{{.Server.Version}}"), cancellationToken).ConfigureAwait(false);
        if (server.ExitCode == CommandResult.NotFound)
        {
            return DockerSurvey.Missing;
        }

        if (!server.Succeeded)
        {
            var reason = server.Reason;
            var problem = reason.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? $"Docker is installed, but {machine.UserName} may not use it. Run the installer with sudo, or add {machine.UserName} to the docker group."
                : reason.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) || reason.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)
                    ? "Docker is installed, but it is not running. Start it (sudo systemctl start docker, or open Docker Desktop)."
                    : $"Docker did not answer: {FirstLine(reason)}";
            return new DockerSurvey { Problem = problem };
        }

        var compose = await machine.Commands.RunAsync(new CommandLine("docker", "compose", "version", "--short"), cancellationToken).ConfigureAwait(false);
        return new DockerSurvey
        {
            Version = server.Output.Trim(),
            ComposeVersion = compose.Succeeded ? compose.Output.Trim() : null
        };
    }

    /// <summary>
    /// The container named <paramref name="name"/>, or null when there is none. Its description holds the container's
    /// environment, which may hold a secret, so the command is secret: the log shows neither.
    /// </summary>
    public static async Task<ContainerSurvey?> SurveyContainerAsync(InstallerMachine machine, string name, CancellationToken cancellationToken = default)
    {
        var inspect = await machine.Commands.RunAsync(
            new CommandLine("docker", "container", "inspect", name) { Secret = true },
            cancellationToken).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inspect.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var container = document.RootElement[0];
            var config = container.GetProperty("Config");
            var labels = config.TryGetProperty("Labels", out var labelElement) && labelElement.ValueKind == JsonValueKind.Object
                ? labelElement.EnumerateObject().ToDictionary(label => label.Name, label => label.Value.GetString() ?? string.Empty)
                : [];
            var environment = config.TryGetProperty("Env", out var envElement) && envElement.ValueKind == JsonValueKind.Array
                ? envElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                    .Select(item => item.Split('=', 2))
                    .Where(pair => pair.Length == 2)
                    .GroupBy(pair => pair[0], StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal)
                : [];

            var composeProject = labels.GetValueOrDefault(ComposeProjectLabel);
            string? emulator = null;
            if (string.Equals(environment.GetValueOrDefault("HatEmulator__Enabled"), "true", StringComparison.OrdinalIgnoreCase))
            {
                emulator = $"{environment.GetValueOrDefault("HatEmulator__Host") ?? "?"}:{environment.GetValueOrDefault("HatEmulator__Port") ?? "?"}";
            }

            return new ContainerSurvey
            {
                Name = name,
                Image = config.TryGetProperty("Image", out var image) ? image.GetString() ?? string.Empty : string.Empty,
                State = container.TryGetProperty("State", out var state) && state.TryGetProperty("Status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                Origin = string.IsNullOrEmpty(composeProject) ? ContainerOrigin.DeployScript : ContainerOrigin.Compose,
                ComposeProject = string.IsNullOrEmpty(composeProject) ? null : composeProject,
                HatEmulator = emulator,
                Version = labels.GetValueOrDefault(VersionLabel) is { Length: > 0 } version ? version : null
            };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Never quote the output: it holds the container's environment.
            throw new InstallerException($"docker container inspect {name} gave an answer the installer cannot read.");
        }
    }

    private static async Task<ServiceSurvey?> SurveyKioskAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        if (machine.Os != InstallerOs.Linux || !machine.FileExists(KioskUnitFile))
        {
            return null;
        }

        var enabled = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-enabled", KioskUnit), cancellationToken).ConfigureAwait(false);
        var active = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-active", KioskUnit), cancellationToken).ConfigureAwait(false);
        return new ServiceSurvey(KioskUnit, enabled.Output.Trim() == "enabled", active.Output.Trim() == "active");
    }

    private static async Task<ProgramSurvey?> SurveyCliAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var path = machine.Commands.Find("hvo-roof")
            ?? new[] { Path.Join(machine.Home, ".local", "bin", "hvo-roof"), "/usr/local/bin/hvo-roof" }.FirstOrDefault(machine.FileExists);
        if (path is null)
        {
            return null;
        }

        var version = await machine.Commands.RunAsync(new CommandLine(path, "--version") { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        return new ProgramSurvey(path, version.Succeeded ? FirstLine(version.Output) : null);
    }

    private static ProgramSurvey? SurveyMacApp(InstallerMachine machine)
    {
        if (machine.Os != InstallerOs.MacOS)
        {
            return null;
        }

        foreach (var folder in new[] { "/Applications", Path.Join(machine.Home, "Applications") })
        {
            var bundle = Path.Join(folder, MacAppBundle);
            if (machine.DirectoryExists(bundle))
            {
                var plist = machine.ReadText(Path.Join(bundle, "Contents", "Info.plist"));
                var match = plist is null ? null : ShortVersion().Match(plist);
                return new ProgramSurvey(bundle, match is { Success: true } ? match.Groups[1].Value : null);
            }
        }

        return null;
    }

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    [GeneratedRegex("^PRETTY_NAME=\"?([^\"\\n]*)\"?\\s*$", RegexOptions.Multiline)]
    private static partial Regex PrettyName();

    [GeneratedRegex("<key>CFBundleShortVersionString</key>\\s*<string>([^<]*)</string>")]
    private static partial Regex ShortVersion();
}
