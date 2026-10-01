using System.Globalization;
using System.Runtime.InteropServices;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The programs the deploy script runs on this machine: bash, curl, setsid or perl, and jq or python3. Docker the
/// installer checks before it plans. A missing one blocks the install before anything changes.
/// </summary>
public sealed class DeployToolsStep : PlanStep
{
    // What each needs, and the Debian package that has it.
    private static readonly (string[] Programs, string Package)[] Needs =
    [
        (["bash"], "bash"),
        (["curl"], "curl"),
        (["setsid", "perl"], "util-linux"),
        (["jq", "python3"], "jq")
    ];

    public override StepKind Kind => StepKind.Package;

    public override string Target => "bash, curl, setsid or perl, jq or python3";

    public override string Purpose => "what the deploy script runs";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var missing = new List<(string Programs, string Package)>();
        foreach (var (programs, package) in Needs)
        {
            if (programs.FirstOrDefault(program => context.Machine.Commands.Find(program) is not null) is { } program)
            {
                found.Add(program);
            }
            else
            {
                missing.Add((string.Join(" or ", programs), package));
            }
        }

        if (missing.Count == 0)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, $"found {string.Join(", ", found)}"));
        }

        var install = context.Machine.Os == InstallerOs.MacOS
            ? "install them (Homebrew has each)"
            : $"install them (sudo apt-get install {string.Join(' ', missing.Select(need => need.Package).Distinct())})";
        return Task.FromResult(new StepCheck(StepChange.Blocked, $"missing {string.Join(", ", missing.Select(need => need.Programs))}: {install}, then run the installer again"));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A rig's HAT emulator: the release's image, pulled by its digest and run on a Docker network of its own
/// (<see cref="Network"/>), where the controller reaches its register port; its unauthenticated control API and camera
/// are published on this machine's loopback address only. One that differs from the answers or the release is
/// replaced (the emulated roof starts again where the emulator starts it); one Docker Compose made is never touched.
/// </summary>
public sealed class HatEmulatorStep(RigSettings rig) : PlanStep
{
    public const string Network = "hvo-emulator";
    public const int ControlPort = 5290;
    public const int RegisterPort = 5291;
    public const string ControlAddress = "127.0.0.1";

    /// <summary>The emulator's register port as the controller reaches it: HAT_EMULATOR_ENDPOINT.</summary>
    public static string Endpoint { get; } = string.Create(CultureInfo.InvariantCulture, $"{MachineSurveyor.HatEmulatorContainer}:{RegisterPort}");

    public override StepKind Kind => StepKind.Container;

    public override string Target => MachineSurveyor.HatEmulatorContainer;

    public override string Purpose => "the HAT emulator: the roof, drive and limit switches the rig drives";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var container = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return new StepCheck(StepChange.Create, $"release {release.Version}'s image by digest, on the {Network} network, its control API on {ControlAddress}:{ControlPort}");
        }

        if (container.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(
                StepChange.Blocked,
                $"Docker Compose made it (project {container.ComposeProject}), and the installer does not replace it. Remove it with that project (docker compose down), then run the installer again.");
        }

        var reason = container.ImageDigest != release.HatEmulator.Digest ? $"replaced with release {release.Version}'s image"
            : container.Network != Network ? $"replaced on the {Network} network"
            : !container.PublishedPorts.SequenceEqual([ControlPort]) || container.PublishAddress != ControlAddress ? $"replaced with its control API on {ControlAddress}:{ControlPort} only"
            : !Same(container.EmulatorTimeScale, RigSettings.DefaultTimeScale, rig.TimeScale) ? $"replaced to run {Number(rig.TimeScale)} times as fast as real time"
            : !Same(container.EmulatorCameraFramesPerSecond, RigSettings.DefaultCameraFramesPerSecond, rig.CameraFramesPerSecond) ? $"replaced with its camera at {Number(rig.CameraFramesPerSecond)} frames a second"
            : !container.IsRunning ? $"replaced: it is {container.State}"
            : null;
        return reason is null
            ? StepCheck.Unchanged($"adopted: {container.State}{(container.Version is { } version ? $", version {version}" : string.Empty)}")
            : new StepCheck(StepChange.Change, reason);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var image = release.HatEmulator.Reference;
        await Docker(context, "pull the HAT emulator's image", TimeSpan.FromMinutes(20), cancellationToken, "pull", "--platform", ControllerStep.Platform(context.Machine), image).ConfigureAwait(false);
        if (!await StopEmulatedControllerAsync(context, cancellationToken).ConfigureAwait(false))
        {
            await StartAsync(context, check, image, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await StartAsync(context, check, image, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error)
        {
            throw new InstallerException(
                $"{error.Message} {MachineSurveyor.ControllerContainer}, which drives the emulated roof, was stopped to replace the emulator and stays stopped: "
                + "run the installer again to start both.",
                error.ExitCode);
        }
    }

    private async Task StartAsync(InstallContext context, StepCheck check, string image, CancellationToken cancellationToken)
    {
        var network = await context.Machine.Commands.RunAsync(new CommandLine("docker", "network", "inspect", Network), cancellationToken).ConfigureAwait(false);
        if (!network.Succeeded)
        {
            await Docker(context, $"make the {Network} network", TimeSpan.FromMinutes(1), cancellationToken, "network", "create", Network).ConfigureAwait(false);
        }

        if (check.Change == StepChange.Change)
        {
            await Docker(context, $"stop the old {Target}", TimeSpan.FromMinutes(1), cancellationToken, "stop", Target).ConfigureAwait(false);
            await Docker(context, $"remove the old {Target}", TimeSpan.FromMinutes(1), cancellationToken, "rm", Target).ConfigureAwait(false);
        }

        await Docker(
            context,
            $"start {Target}",
            TimeSpan.FromMinutes(2),
            cancellationToken,
            "run", "-d", "--name", Target, "--network", Network, "--restart", "unless-stopped",
            "-p", string.Create(CultureInfo.InvariantCulture, $"{ControlAddress}:{ControlPort}:{ControlPort}"),
            "--env", $"{MachineSurveyor.EmulatorTimeScaleSetting}={Number(rig.TimeScale)}",
            "--env", $"{MachineSurveyor.EmulatorCameraFramesSetting}={Number(rig.CameraFramesPerSecond)}",
            image).ConfigureAwait(false);
        context.Log.Write($"Started {Target} from {image}.");

        // Its health check asks its control API; the controller's deploy needs it answering.
        var status = string.Empty;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), context.Time, cancellationToken).ConfigureAwait(false);
            }

            var health = await context.Machine.Commands.RunAsync(
                new CommandLine("docker", "container", "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", Target),
                cancellationToken).ConfigureAwait(false);
            status = health.Succeeded ? health.Output.Trim() : string.Empty;
            if (status == "healthy")
            {
                return;
            }

            if (status is "unhealthy" or "exited" or "dead")
            {
                break;
            }
        }

        throw new InstallerException($"{Target} did not become healthy (it is {(status.Length > 0 ? status : "not there")}): see docker logs {Target}.");
    }

    // A controller that drives the emulator loses its HAT when the emulator is replaced, and could not then report the
    // verified Stop the deploy script needs; it drives no real roof, so it is stopped here, and the controller's step
    // starts it again. A controller on the real HAT is never stopped here.
    private static async Task<bool> StopEmulatedControllerAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var controller = await MachineSurveyor.SurveyContainerAsync(context.Machine, MachineSurveyor.ControllerContainer, cancellationToken).ConfigureAwait(false);
        if (controller is not { IsRunning: true, HatEmulator: not null, Origin: ContainerOrigin.DeployScript })
        {
            return false;
        }

        await Docker(context, $"stop {MachineSurveyor.ControllerContainer}", TimeSpan.FromMinutes(2), cancellationToken, "stop", MachineSurveyor.ControllerContainer).ConfigureAwait(false);
        context.Log.Write($"Stopped {MachineSurveyor.ControllerContainer}, which drives the emulated roof, before replacing the HAT emulator.");
        return true;
    }

    private static async Task Docker(InstallContext context, string doing, TimeSpan timeout, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await context.Machine.Commands.RunAsync(new CommandLine("docker", arguments) { Timeout = timeout }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InstallerException($"Could not {doing}: {result.Reason}");
        }
    }

    // A setting the emulator was started without is its default, which the installer's defaults match.
    private static bool Same(string? current, double unset, double wanted)
        => current is null ? unset == wanted : double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == wanted;

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// The controller's container, deployed by the release's digest through the deploy script (docs/deployment.md): it
/// checks the new image on this machine before it stops anything, stops the roof with a verified Stop before it
/// replaces a running controller, checks the new one (ready, the HAT it expects, the web UI, an authenticated Status
/// and a verified Stop at its published address), and puts the old one back when a check fails. The installer itself
/// never moves the roof, and never replaces a controller while the roof moves.
/// </summary>
/// <remarks>
/// A controller the deploy script made is adopted as it is when it already runs the release with the answers' ports,
/// HTTPS, host names, certificate, telemetry, camera, settings, HAT and keys; otherwise it is redeployed, and the check says why. A
/// running one must know a key the deploy script can use for its verified Stop, and the new one must read it too: an
/// operator or admin key in the secrets folder (<see cref="ControllerProbe.Candidates"/>). One Docker Compose made is
/// explained and never replaced.
/// </remarks>
public sealed class ControllerStep(
    ControllerLayout layout,
    ControllerSettings settings,
    CertificateNames names,
    bool rig,
    IControllerCertificateStep? certificate,
    IReadOnlyList<ApiKeyAllocation> keys,
    HatEmulatorStep? emulator = null,
    IReadOnlyList<CameraFileStep>? camera = null,
    SettingsImportStep? import = null) : PlanStep
{
    // The key file the last check found the running controller knows: the deploy script's key for its verified Stop.
    private string? _deployKeyFile;

    public override StepKind Kind => StepKind.Container;

    public override string Target => MachineSurveyor.ControllerContainer;

    public override string Purpose => rig ? "the controller, against the HAT emulator" : "the controller, driving the real HAT";

    /// <summary>The platform the release's image is pulled for: the machine's processor, as Docker names it.</summary>
    public static string Platform(InstallerMachine machine)
        => machine.Architecture == Architecture.X64 ? "linux/amd64" : "linux/arm64";

    /// <summary>The one address a rig publishes its ports on, unless it is open to the network; null for every address.</summary>
    public string? PublishAddress => rig && settings.Rig is not { OpenToLan: true } ? HatEmulatorStep.ControlAddress : null;

    /// <summary>
    /// True when the last check found the certificate is all that differs: a new one is not served yet. <c>hvo-roof-install
    /// cert</c> redeploys the controller only then.
    /// </summary>
    public bool OnlyCertificateDiffers { get; private set; }

    /// <summary>
    /// Set by <c>hvo-roof-install cert</c> once the person agreed to a redeploy for the certificate alone: a later check
    /// (the one just before it runs) refuses to go ahead when more than the certificate differs by then.
    /// </summary>
    public bool CertificateOnly { get; set; }

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        MoreThanCertificateChanged = false;
        var check = await CheckChangeAsync(context, cancellationToken).ConfigureAwait(false);
        if (check.MakesChange && RefusedKey(context.Machine) is { } refused)
        {
            return refused;
        }

        if (CertificateOnly && check.MakesChange && !OnlyCertificateDiffers)
        {
            MoreThanCertificateChanged = true;
            return new StepCheck(StepChange.Blocked, $"more than its certificate would change now: {check.Detail}");
        }

        return check;
    }

    /// <summary>
    /// Whether the last check, with <see cref="CertificateOnly"/>, found that more than the certificate would change: only
    /// the installer, which shows all of it, redeploys the controller then.
    /// </summary>
    public bool MoreThanCertificateChanged { get; private set; }

    // An API key entry the controller refuses fails the deploy script's pre-flight check, so a deploy is not tried with
    // one there; one of the installer's own that it will finish (ApiKeyFiles.Allocate) is not in the way.
    private StepCheck? RefusedKey(InstallerMachine machine)
    {
        IReadOnlyList<ConfiguredApiKey> configured;
        try
        {
            configured = ApiKeyFiles.Read(machine, layout.Secrets);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var refused = configured.FirstOrDefault(key => key.Problem is not null && keys.All(own => own.Index != key.Index));
        return refused is null
            ? null
            : new StepCheck(
                StepChange.Blocked,
                string.Create(CultureInfo.InvariantCulture, $"API key entry {refused.Index} in {layout.Secrets}{(refused.Name is { } name ? $" ({name})" : string.Empty)} is one the controller refuses: {refused.Problem}. ")
                + string.Create(CultureInfo.InvariantCulture, $"The deploy script's pre-flight check fails on it: correct or remove its files ({ApiKeyFiles.Prefix}{refused.Index}__*), then run the installer again."));
    }

    private async Task<StepCheck> CheckChangeAsync(InstallContext context, CancellationToken cancellationToken)
    {
        _deployKeyFile = null;
        OnlyCertificateDiffers = false;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var container = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return new StepCheck(StepChange.Create, $"release {release.Version} by digest, through the deploy script");
        }

        if (container.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(
                StepChange.Blocked,
                $"Docker Compose made it (project {container.ComposeProject}), and the installer does not replace it. To move it to the installer, see \"Moving between Compose and the deploy script\" in docs/deployment.md.");
        }

        var replacesEmulator = emulator is not null
            && (context.HasApplied(emulator) || (await emulator.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange);
        var reason = await ReasonAsync(context, container, release, replacesEmulator, cancellationToken).ConfigureAwait(false);
        var namesChanged = !SameHosts(container.AllowedHosts, names.AllowedHosts);
        if (reason is null && await CertificateReasonAsync(context, container, namesChanged, cancellationToken).ConfigureAwait(false) is { } certificateReason)
        {
            // The names it answers to change with its certificate's: hvo-roof-install cert redeploys it for both.
            reason = certificateReason;
            OnlyCertificateDiffers = true;
        }

        reason ??= namesChanged ? "redeployed to answer to its names as they are now" : null;

        if (container.State is "paused" or "restarting")
        {
            // Neither answers a Stop, and the deploy script's verified Stop needs one before it replaces a controller.
            return new StepCheck(
                StepChange.Blocked,
                container.State == "paused"
                    ? $"it is paused, and the deploy script's verified Stop needs it to answer: unpause it (docker unpause {Target}), or stop it yourself once the roof is idle (docker stop {Target}), then run the installer again"
                    : $"it is restarting, and the deploy script's verified Stop needs it to answer: wait until it runs, or stop it yourself (docker stop {Target}), then run the installer again");
        }

        if (!container.IsRunning)
        {
            // Nothing runs to stop: the deploy script starts the new one without a Stop.
            return new StepCheck(StepChange.Change, reason ?? $"started again as release {release.Version}: it is {container.State}");
        }

        if (replacesEmulator && container.HatEmulator is not null)
        {
            // The emulator's step stops it first (HatEmulatorStep.StopEmulatedControllerAsync): it drives no real roof.
            return new StepCheck(StepChange.Change, reason!);
        }

        return await CheckRunningAsync(context, container, reason, cancellationToken).ConfigureAwait(false);
    }

    // Why the container must be deployed again, from what Docker says about it; null when nothing differs.
    private async Task<string?> ReasonAsync(InstallContext context, ContainerSurvey container, ReleaseManifest release, bool replacesEmulator, CancellationToken cancellationToken)
    {
        var emulated = container.HatEmulator is not null;
        if (emulated != rig)
        {
            return rig ? "redeployed against the HAT emulator" : "redeployed for the real HAT";
        }

        if (rig && container.HatEmulator != HatEmulatorStep.Endpoint)
        {
            return $"redeployed against the HAT emulator at {HatEmulatorStep.Endpoint}";
        }

        if (container.ServesHttps is { } https && https != settings.UsesHttps)
        {
            return settings.UsesHttps ? "redeployed to serve HTTPS" : "redeployed to serve HTTP";
        }

        int[] ports = [.. new[] { settings.ApiPort, settings.WebPort }.Order()];
        if (!container.PublishedPorts.SequenceEqual(ports))
        {
            var now = container.PublishedPorts.Count == 0 ? "none" : string.Join(" and ", container.PublishedPorts);
            return $"redeployed on ports {settings.ApiPort} and {settings.WebPort} (it publishes {now})";
        }

        if (container.PublishAddress != PublishAddress)
        {
            return PublishAddress is null ? "redeployed to publish its ports on every address" : $"redeployed to publish its ports on {PublishAddress} only";
        }

        var network = rig ? HatEmulatorStep.Network : null;
        if (container.Network != network)
        {
            return $"redeployed on {(network is null ? "Docker's default network" : $"the {network} network")}";
        }

        // A list that differs is weighed with the certificate (CheckChangeAsync): the two change together.
        if (container.AllowedHosts is null)
        {
            return "redeployed to answer only to its names (it answers to any)";
        }

        if (container.ImageDigest != release.Controller.Digest)
        {
            return $"redeployed as release {release.Version} (it runs {(container.Version is { } version ? $"version {version}" : "another image")})";
        }

        if (keys.FirstOrDefault(key => key.Use == ApiKeyUse.WebStop) is { IsKnown: true } webStop && container.WebStopKeyFile != ApiKeyFiles.ContainerKeyFile(webStop.Index))
        {
            return "redeployed so the web UI's Stop has a key of its own";
        }

        if (container.TelemetryEndpoint != (settings.TelemetryEndpoint ?? string.Empty))
        {
            return settings.TelemetryEndpoint is { } endpoint ? $"redeployed to send telemetry to {endpoint}" : "redeployed with telemetry off";
        }

        if (replacesEmulator)
        {
            return "redeployed: the HAT emulator is replaced";
        }

        if (camera is { Count: > 0 } && await CameraSteps.ChangedSinceAsync(context, camera, container.StartedAt, cancellationToken).ConfigureAwait(false))
        {
            return "redeployed to read the camera's new settings";
        }

        // To be imported, imported by this run, or by one that stopped before it redeployed the controller.
        if (import is not null
            && (context.HasApplied(import)
                || (await import.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange
                || (import.Imported(context) && CameraSteps.WrittenSince(context, layout.SettingsFile, container.StartedAt))))
        {
            return "redeployed to read the imported settings";
        }

        return null;
    }

    // Why the container must be deployed again for its certificate: a new one is put in place, or it serves another. With
    // namesChanged, it also answers to names it no longer has.
    private async Task<string?> CertificateReasonAsync(InstallContext context, ContainerSurvey container, bool namesChanged, CancellationToken cancellationToken)
    {
        if (certificate is null)
        {
            return null;
        }

        if (certificate.Wrote || certificate.WillWrite(context))
        {
            return namesChanged ? "redeployed with the new certificate, and to answer to its names as they are now" : "redeployed with the new certificate";
        }

        return settings.UsesHttps && container.IsRunning && await ServesAnotherCertificateAsync(context, cancellationToken).ConfigureAwait(false)
            ? $"redeployed: it serves another certificate than {certificate.Target}{(namesChanged ? ", and answers to its names as they were" : string.Empty)}"
            : null;
    }

    // A running controller: whether it knows the installer's keys (it reads new ones only when it starts), and which key
    // the deploy script can stop it with.
    private async Task<StepCheck> CheckRunningAsync(InstallContext context, ContainerSurvey container, string? reason, CancellationToken cancellationToken)
    {
        var adopted = $"adopted: {container.State}{(container.Version is { } version ? $", version {version}" : string.Empty)}";
        IReadOnlyList<DeployKeyCandidate> candidates;
        try
        {
            candidates = keys.Any(key => !key.IsKnown) ? throw new UnauthorizedAccessException() : ControllerProbe.Candidates(context.Machine, layout, keys);
        }
        catch (UnauthorizedAccessException)
        {
            return reason is null
                ? new StepCheck(StepChange.Info, $"{adopted}; only root can read its keys: run with sudo to check it knows them")
                : new StepCheck(StepChange.Change, reason);
        }

        var probes = new Dictionary<string, ProbeResult>(StringComparer.Ordinal);
        async Task<ProbeResult?> ProbeAsync(string file)
        {
            if (!probes.TryGetValue(file, out var probe))
            {
                if (ControllerProbe.ReadKey(context.Machine, file) is not { } key)
                {
                    return null;
                }

                probes[file] = probe = await ControllerProbe.StatusAsync(context, Target, key, cancellationToken).ConfigureAwait(false);
            }

            return probe;
        }

        foreach (var key in keys)
        {
            var probe = await ProbeAsync(ApiKeyFiles.KeyFile(layout.Secrets, key.Index)).ConfigureAwait(false);
            if (probe is { Outcome: ProbeOutcome.NoAnswer })
            {
                return NoAnswer(probe);
            }

            if (probe is not { IsAccepted: true })
            {
                const string NewKeys = "to read the installer's new API keys (a controller reads its keys when it starts)";
                reason = reason is null ? $"redeployed {NewKeys}" : OnlyCertificateDiffers ? $"{reason}, and {NewKeys}" : reason;
                OnlyCertificateDiffers = false;
            }
        }

        if (reason is null)
        {
            return StepCheck.Unchanged(adopted);
        }

        var (blocked, keyFile) = await StopKeyAsync(context, candidates, ProbeAsync, reason, "replaces the controller").ConfigureAwait(false);
        _deployKeyFile = keyFile;
        return blocked ?? new StepCheck(StepChange.Change, reason);
    }

    /// <summary>
    /// The key file the deploy script's verified Stop can use on the running controller before it <paramref name="doing"/>
    /// (rolls it back, stops it): the first operator or admin key in the secrets folder it takes. Otherwise why that cannot
    /// go ahead: it does not answer, the roof is moving, or it knows no such key. Only root reads the keys: throws
    /// <see cref="UnauthorizedAccessException"/> otherwise.
    /// </summary>
    internal Task<(StepCheck? Blocked, string? KeyFile)> StopKeyAsync(InstallContext context, string reason, string doing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = ControllerProbe.Candidates(context.Machine, layout, keys);
        return StopKeyAsync(
            context,
            candidates,
            async file => ControllerProbe.ReadKey(context.Machine, file) is { } key
                ? await ControllerProbe.StatusAsync(context, Target, key, cancellationToken).ConfigureAwait(false)
                : null,
            reason,
            doing);
    }

    private async Task<(StepCheck? Blocked, string? KeyFile)> StopKeyAsync(
        InstallContext context,
        IReadOnlyList<DeployKeyCandidate> candidates,
        Func<string, Task<ProbeResult?>> probeAsync,
        string reason,
        string doing)
    {
        foreach (var candidate in candidates)
        {
            var probe = await probeAsync(candidate.File).ConfigureAwait(false);
            if (probe is { Outcome: ProbeOutcome.NoAnswer })
            {
                return (NoAnswer(probe), null);
            }

            if (probe is { IsAccepted: true })
            {
                if (probe.IsMoving)
                {
                    return (new StepCheck(
                        StepChange.Blocked,
                        $"the roof is moving{(probe.Motion is { } motion && motion != "None" ? $" ({motion})" : string.Empty)}, and the deploy script stops it before it {doing}: run the installer again once the roof is idle"), null);
                }

                context.Log.Write($"The deploy script will stop {Target} with {candidate.Description}, a key it knows.");
                return (null, candidate.File);
            }
        }

        return (new StepCheck(
            StepChange.Blocked,
            $"{reason}, but it runs, and it knows no operator or admin key in {layout.Secrets}, which the deploy script's verified Stop needs before it {doing}. "
            + $"Put its operator key there (\"API keys\" in docs/deployment.md), or stop the controller yourself once the roof is idle (docker stop {Target}), then run the installer again."), null);
    }

    private StepCheck NoAnswer(ProbeResult probe)
        => new(
            StepChange.Blocked,
            $"it runs but does not answer an authenticated Status ({probe.Reason}), and the deploy script's verified Stop needs it to. "
            + $"If it is starting, wait a minute; otherwise stop it yourself once the roof is idle (docker stop {Target}). Then run the installer again.");

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        using var work = new DeployScript.WorkFolder(context.Machine);
        var environment = await EnvironmentAsync(context, work, release.Controller.Reference, _deployKeyFile, cancellationToken).ConfigureAwait(false);
        await RunScriptAsync(context, work, environment, cancellationToken).ConfigureAwait(false);
        var deployed = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, CancellationToken.None).ConfigureAwait(false);
        if (deployed is not { IsRunning: true } || deployed.ImageDigest != release.Controller.Digest)
        {
            throw new InstallerException($"The deploy script finished, but {Target} does not run release {release.Version}'s image: see docker ps and the install log.");
        }

        context.Log.Write($"Deployed {Target}: release {release.Version}, verified by the deploy script.");
    }

    /// <summary>
    /// What the deploy script runs with for this controller: <paramref name="imageReference"/> (a release's image by
    /// digest; <c>--rollback</c> and <c>--stop</c> ignore it), and <paramref name="keyFile"/> for its verified Stop (the
    /// operator key's file when null). The settings, names and trust are the plan's, so every mode checks the controller
    /// the same way.
    /// </summary>
    internal async Task<Dictionary<string, string>> EnvironmentAsync(
        InstallContext context,
        DeployScript.WorkFolder work,
        string imageReference,
        string? keyFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        var operatorKey = keys.First(key => key.Use == ApiKeyUse.Operator);
        var webStop = keys.First(key => key.Use == ApiKeyUse.WebStop);
        var (host, trust) = RemoteCheck(context.Machine, work);
        var dockerContext = await context.Machine.Commands.RunAsync(new CommandLine("docker", "context", "show"), cancellationToken).ConfigureAwait(false);
        var extra = $"--env {MachineSurveyor.WebStopKeyFileSetting}={ApiKeyFiles.ContainerKeyFile(webStop.Index)}{(rig ? $" --network {HatEmulatorStep.Network}" : string.Empty)}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The script runs here, against this machine's Docker, and checks the controller at this machine's loopback by a
            // name its certificate has, which this machine need not resolve.
            ["PI_HOST"] = host,
            ["REMOTE_CONNECT_TO"] = host == "localhost" ? string.Empty : "::127.0.0.1:",
            ["DOCKER_CONTEXT"] = dockerContext.Succeeded && dockerContext.Output.Trim() is { Length: > 0 } name ? name : "default",
            ["IMAGE_REF"] = imageReference,
            ["BUILD_PLATFORM"] = Platform(context.Machine),
            ["CONTAINER_NAME"] = Target,
            ["HOST_PORT"] = Port(settings.HttpPort),
            ["HTTPS_HOST_PORT"] = Port(settings.HttpsPort),
            ["WEB_HOST_PORT"] = Port(settings.WebPort),
            ["PUBLISH_ADDRESS"] = PublishAddress ?? string.Empty,
            ["EXTRA_DOCKER_ARGS"] = extra,
            ["HAT_EMULATOR_ENDPOINT"] = rig ? HatEmulatorStep.Endpoint : string.Empty,
            ["ALLOW_EMULATED_HAT"] = rig ? "true" : "false",
            ["HVO_FORCE_RASPBERRY_PI"] = "true",
            ["IGNORE_PHYSICAL_LIMIT_SWITCHES"] = "false",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = settings.TelemetryEndpoint ?? string.Empty,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["SECRETS_DIR"] = layout.Secrets,
            ["IDENTITY_DIR"] = layout.Identity,
            ["CONFIG_DIR"] = layout.Settings,
            ["MANAGED_SECRETS_DIR"] = layout.SettingsSecrets,
            ["HTTPS_CERT_DIR"] = settings.UsesHttps ? layout.Https : string.Empty,
            ["HTTPS_CERT_FILE"] = Path.GetFileName(layout.Pfx),
            ["ALLOW_INSECURE_HTTP"] = settings.UsesHttps ? "false" : "true",
            ["ALLOWED_HOSTS"] = names.AllowedHosts,
            ["REMOTE_CA_CERT"] = trust ?? string.Empty,
            ["SKIP_REMOTE_CHECK"] = "false",

            // The script's Stop refuses a roof that moves, or that its Status cannot show idle, before it replaces the controller.
            ["REQUIRE_IDLE_ROOF"] = "true",

            // The key for the Stop and Status checks, as a file only root reads; never the key itself.
            ["OPERATOR_KEY_FILE"] = keyFile ?? ApiKeyFiles.KeyFile(layout.Secrets, operatorKey.Index),
            ["ROOF_OPERATOR_API_KEY"] = string.Empty
        };
        return environment;
    }

    /// <summary>
    /// Runs the deploy script with <paramref name="arguments"/> (none deploys), and says what runs now when it fails. It is
    /// never killed: see the comment inside.
    /// </summary>
    internal static async Task RunScriptAsync(
        InstallContext context,
        DeployScript.WorkFolder work,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        params IReadOnlyList<string> arguments)
    {
        // The installer never kills the script: killed part-way through the switch, it would leave the old controller stopped.
        // A Ctrl-C at the terminal reaches the script too, which stops and puts the old controller back itself (its EXIT
        // trap), unless the new controller has passed its checks: from there it runs to its end, as it does for an
        // interrupt only the installer gets. The plan then stops before its next step that changes something.
        using (cancellationToken.Register(() => context.Progress?.Invoke(StoppingMessage)))
        {
            try
            {
                await DeployScript.RunAsync(context, work, environment, CancellationToken.None, arguments).ConfigureAwait(false);
            }
            catch (InstallerException error)
            {
                var stopped = cancellationToken.IsCancellationRequested || error.ExitCode == InstallerExitCode.Cancelled;
                throw new InstallerException(
                    $"{error.Message} {await NowAsync(context).ConfigureAwait(false)}",
                    stopped ? InstallerExitCode.Cancelled : error.ExitCode);
            }
        }
    }

    /// <summary>What an interrupt while the deploy script runs says: the script is not killed, so it finishes or puts the old controller back.</summary>
    public const string StoppingMessage = "Stopping once the deploy script ends. It is not killed: if it was replacing the controller, it finishes or puts the old one back.";

    /// <summary>What runs as the controller now, as a sentence: after the deploy script stopped, or the install did.</summary>
    internal static async Task<string> NowAsync(InstallContext context)
    {
        const string Target = MachineSurveyor.ControllerContainer;
        ContainerSurvey? now;
        try
        {
            now = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InstallerException or IOException or InvalidOperationException)
        {
            return $"Docker did not say what runs now ({error.Message}): `docker ps` shows it.";
        }

        return now switch
        {
            null => $"There is no {Target} now.",
            { IsRunning: true } => $"{Target} runs now{(now.Version is { } version ? $", version {version}" : string.Empty)}.",
            _ => $"{Target} is {now.State} now."
        };
    }

    // The name the deploy script checks the controller by: the first of its names that its certificate has (the person's
    // own may not have localhost). And what verifies that certificate from this machine: the CA's certificate, or the
    // controller's own (a self-signed one, or the person's, which this machine may not otherwise trust).
    private (string Host, string? Trust) RemoteCheck(InstallerMachine machine, DeployScript.WorkFolder work)
    {
        if (!settings.UsesHttps)
        {
            return ("localhost", null);
        }

        using var served = certificate?.Current(machine);
        var host = served is null ? "localhost" : names.DnsNames.FirstOrDefault(name => served.MatchesHostname(name)) ?? "localhost";
        if (settings.Connection == ConnectionMode.PrivateCa)
        {
            return (host, layout.CaCertificate);
        }

        if (served is null)
        {
            return (host, null);
        }

        var file = Path.Join(work.Path, "roof-controller.crt");
        machine.WriteAtomically(file, served.ExportCertificatePem() + "\n", Modes.File);
        return (host, file);
    }

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);

    // AllowedHosts compared as a set: unset is any host (*).
    private static bool SameHosts(string? current, string wanted)
        => current is not null
            && current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(wanted.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // The certificate the controller presents on its API's port, against the one in the file; false when either cannot be had.
    private async Task<bool> ServesAnotherCertificateAsync(InstallContext context, CancellationToken cancellationToken)
    {
        using var inFile = certificate!.Current(context.Machine);
        if (inFile is null)
        {
            return false;
        }

        using var served = await context.Machine.ServedCertificateAsync(settings.ApiPort, cancellationToken).ConfigureAwait(false);
        return served is not null && !served.RawData.AsSpan().SequenceEqual(inFile.RawData);
    }
}
