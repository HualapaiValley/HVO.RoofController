using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

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

    /// <summary>Where the kernel lists the display outputs.</summary>
    public const string DrmFolder = "/sys/class/drm";

    /// <summary>A desktop's login screen (lightdm on Raspberry Pi OS with a desktop).</summary>
    public const string DisplayManagerUnit = "display-manager.service";

    /// <summary>The boot target that starts a desktop's display manager.</summary>
    public const string GraphicalTarget = "graphical.target";

    /// <summary>The Mac app's bundle name.</summary>
    public const string MacAppBundle = "HVO Roof.app";

    /// <summary>The label Docker Compose puts on each container it makes.</summary>
    public const string ComposeProjectLabel = "com.docker.compose.project";

    public const string VersionLabel = "org.opencontainers.image.version";

    /// <summary>The web UI's setting that names the key file its Stop sends.</summary>
    public const string WebStopKeyFileSetting = "RoofWeb__StopKeyFile";

    /// <summary>Where the controller exports telemetry; empty for none.</summary>
    public const string TelemetryEndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The HAT emulator's time scale.</summary>
    public const string EmulatorTimeScaleSetting = "Emulator__TimeScale";

    /// <summary>The HAT emulator's camera frame rate.</summary>
    public const string EmulatorCameraFramesSetting = "Emulator__CameraFramesPerSecond";

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
        var (certificate, authority) = SurveyCertificates(machine, systemRecord?.Controller ?? userRecord?.Controller);

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
            SystemRecordProblem = systemProblem,
            HasSystemConfiguration = machine.DirectoryExists("/etc/hvo-roof"),
            Docker = docker,
            Controller = controller,
            HatEmulator = emulator,
            Kiosk = await SurveyKioskAsync(machine, cancellationToken).ConfigureAwait(false),
            Display = await SurveyDisplayAsync(machine, systemRecord?.Roles.Contains(InstallRole.Kiosk) == true, cancellationToken).ConfigureAwait(false),
            Cli = await SurveyCliAsync(machine, cancellationToken).ConfigureAwait(false),
            MacApp = SurveyMacApp(machine),
            Certificate = certificate,
            Authority = authority
        };
    }

    /// <summary>
    /// The controller's certificate and the installer's CA on this machine, and the names clients use for the controller
    /// (with <paramref name="settings"/>, as recorded, or the defaults) that the certificate is not for.
    /// </summary>
    public static (CertificateSurvey? Certificate, AuthoritySurvey? Authority) SurveyCertificates(InstallerMachine machine, ControllerSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var layout = ControllerLayout.For(machine);
        using var authority = LoadAuthority(machine, layout, out var authoritySurvey);
        return (SurveyCertificate(machine, layout, (settings ?? new ControllerSettings()).Normalised(), authority), authoritySurvey);
    }

    private static X509Certificate2? LoadAuthority(InstallerMachine machine, ControllerLayout layout, out AuthoritySurvey? survey)
    {
        string? pem;
        try
        {
            pem = machine.ReadText(layout.CaCertificate);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            survey = new AuthoritySurvey { Path = layout.CaCertificate, Problem = "it cannot be read" };
            return null;
        }

        if (pem is null)
        {
            survey = null;
            return null;
        }

        X509Certificate2 authority;
        try
        {
            authority = X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            survey = new AuthoritySurvey { Path = layout.CaCertificate, Problem = "it is not a certificate" };
            return null;
        }

        var constraints = NameConstraints.Of(authority);
        survey = new AuthoritySurvey
        {
            Path = layout.CaCertificate,
            Subject = authority.GetNameInfo(X509NameType.SimpleName, false),
            NotAfter = new DateTimeOffset(authority.NotAfter.ToUniversalTime()),
            Fingerprint = ControllerCertificates.Fingerprint(authority),
            Permits = constraints is null ? [] : [.. constraints.DnsNames, .. constraints.Networks.Select(network => network.ToString())]
        };
        return authority;
    }

    private static CertificateSurvey? SurveyCertificate(InstallerMachine machine, ControllerLayout layout, ControllerSettings settings, X509Certificate2? authority)
    {
        byte[]? pfx;
        string? password;
        try
        {
            pfx = machine.ReadBytes(layout.Pfx);
            if (pfx is null)
            {
                return null;
            }

            password = machine.ReadText(layout.PfxPassword);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return new CertificateSurvey { Path = layout.Pfx, Problem = "only root can read it", NeedsRoot = true };
        }

        using var certificate = string.IsNullOrEmpty(password) ? null : ControllerCertificates.LoadPfx(pfx, password);
        if (certificate is null)
        {
            return new CertificateSurvey
            {
                Path = layout.Pfx,
                Problem = string.IsNullOrEmpty(password) ? $"there is no password for it in {layout.PfxPassword}" : $"the password in {layout.PfxPassword} does not open it"
            };
        }

        var (dnsNames, addresses) = ControllerCertificates.NamesIn(certificate);
        return new CertificateSurvey
        {
            Path = layout.Pfx,
            Subject = certificate.GetNameInfo(X509NameType.SimpleName, false),
            NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            Fingerprint = ControllerCertificates.Fingerprint(certificate),
            Issuer = ControllerCertificates.IsSelfSigned(certificate) ? null : certificate.GetNameInfo(X509NameType.SimpleName, true),
            FromAuthority = authority is not null && ControllerCertificates.IsIssuedBy(certificate, authority),
            FromInstallerCa = certificate.GetNameInfo(X509NameType.SimpleName, true).StartsWith(ControllerCertificates.AuthorityNamePrefix, StringComparison.Ordinal),
            Names = [.. dnsNames, .. addresses.Select(address => address.ToString())],
            Uncovered = ControllerCertificates.CompareNames(certificate, CertificateNames.For(machine, settings)).Missing
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

            var hasHostConfig = container.TryGetProperty("HostConfig", out var hostConfig) && hostConfig.ValueKind == JsonValueKind.Object;
            var hostBindings = hasHostConfig
                && hostConfig.TryGetProperty("PortBindings", out var bindings)
                && bindings.ValueKind == JsonValueKind.Object
                    ? bindings.EnumerateObject()
                        .Where(binding => binding.Value.ValueKind == JsonValueKind.Array)
                        .SelectMany(binding => binding.Value.EnumerateArray())
                        .Select(host => (
                            Port: host.TryGetProperty("HostPort", out var port) && int.TryParse(port.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0,
                            Address: host.TryGetProperty("HostIp", out var address) ? address.GetString() ?? string.Empty : string.Empty))
                        .Where(binding => binding.Port > 0)
                        .ToArray()
                    : [];
            var published = hostBindings.Select(binding => binding.Port).Distinct().Order().ToArray();
            var addresses = hostBindings.Select(binding => binding.Address is "0.0.0.0" or "::" ? string.Empty : binding.Address).Distinct().ToArray();
            var network = hasHostConfig && hostConfig.TryGetProperty("NetworkMode", out var mode) && mode.GetString() is { Length: > 0 } networkMode
                && networkMode is not ("default" or "bridge")
                    ? networkMode
                    : null;
            var urls = environment.GetValueOrDefault("ASPNETCORE_URLS");

            return new ContainerSurvey
            {
                Name = name,
                PublishedPorts = published,
                ServesHttps = urls is null ? null : urls.Contains("https://", StringComparison.OrdinalIgnoreCase),
                AllowedHosts = environment.GetValueOrDefault("AllowedHosts") is { Length: > 0 } allowed && allowed != "*" ? allowed : null,
                Image = config.TryGetProperty("Image", out var image) ? image.GetString() ?? string.Empty : string.Empty,
                State = container.TryGetProperty("State", out var state) && state.TryGetProperty("Status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                Origin = string.IsNullOrEmpty(composeProject) ? ContainerOrigin.DeployScript : ContainerOrigin.Compose,
                ComposeProject = string.IsNullOrEmpty(composeProject) ? null : composeProject,
                HatEmulator = emulator,
                Version = labels.GetValueOrDefault(VersionLabel) is { Length: > 0 } version ? version : null,
                PublishAddress = addresses is [{ Length: > 0 } only] ? only : null,
                Network = network,
                WebStopKeyFile = environment.GetValueOrDefault(WebStopKeyFileSetting) is { Length: > 0 } stopKey ? stopKey : null,
                TelemetryEndpoint = environment.GetValueOrDefault(TelemetryEndpointSetting),
                EmulatorTimeScale = environment.GetValueOrDefault(EmulatorTimeScaleSetting),
                EmulatorCameraFramesPerSecond = environment.GetValueOrDefault(EmulatorCameraFramesSetting),
                StartedAt = container.TryGetProperty("State", out var started) ? StartedAt(started) : null
            };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Never quote the output: it holds the container's environment.
            throw new InstallerException($"docker container inspect {name} gave an answer the installer cannot read.");
        }
    }

    // Docker's State.StartedAt: RFC 3339 with up to nine digits of the second, and the year 1 for a container never started.
    private static DateTimeOffset? StartedAt(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("StartedAt", out var element) || element.ValueKind != JsonValueKind.String || element.GetString() is not { } text)
        {
            return null;
        }

        var match = StartedAtPattern().Match(text);
        return match.Success
            && DateTimeOffset.TryParse(match.Groups["time"].Value + match.Groups["fraction"].Value + match.Groups["zone"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            && time.Year > 1
                ? time
                : null;
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?<fraction>\.\d{1,7})?\d*(?<zone>Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex StartedAtPattern();

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

    // Each output is a folder named for its card and connector (card1-HDMI-A-1), with a status file; the cards alone
    // (card1) and render nodes have none. A writeback output (the Pi's card1-Writeback-1) is not a screen. A desktop
    // that would hold the screen is surveyed when there is an output, or a kiosk installed here (its screen may be off).
    private static async Task<DisplaySurvey?> SurveyDisplayAsync(InstallerMachine machine, bool kioskInstalled, CancellationToken cancellationToken)
    {
        if (machine.RuntimeIdentifier != "linux-arm64")
        {
            return null;
        }

        var outputs = new List<(string Name, string Status)>();
        foreach (var name in machine.ListNames(DrmFolder))
        {
            if (OutputPattern().Match(name) is { Success: true } match && !match.Groups["output"].Value.StartsWith("Writeback", StringComparison.Ordinal))
            {
                string? status;
                try
                {
                    status = machine.ReadText(Path.Join(DrmFolder, name, "status"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    status = null;
                }

                outputs.Add((match.Groups["output"].Value, status?.Trim() is { Length: > 0 } text ? text : "unknown"));
            }
        }

        if (outputs.Count == 0 && !kioskInstalled)
        {
            return null;
        }

        // A desktop that is stopped for now still takes the screen at boot, when it is enabled and the Pi boots to it.
        var active = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-active", DisplayManagerUnit), cancellationToken).ConfigureAwait(false);
        var enabled = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-enabled", DisplayManagerUnit), cancellationToken).ConfigureAwait(false);
        var boots = await machine.Commands.RunAsync(new CommandLine("systemctl", "get-default"), cancellationToken).ConfigureAwait(false);
        return new DisplaySurvey(outputs, active.Output.Trim() == "active", enabled.Succeeded && boots.Output.Trim() == GraphicalTarget);
    }

    [GeneratedRegex("^card[0-9]+-(?<output>[A-Za-z0-9-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OutputPattern();

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
