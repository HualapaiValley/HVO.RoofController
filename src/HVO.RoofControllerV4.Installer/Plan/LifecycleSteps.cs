using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The installer itself, from the release, kept where <c>hvo-roof-install upgrade</c>, <c>rollback</c>, <c>backup</c>
/// and <c>uninstall</c> find it: <see cref="InstallPaths.Installer"/>. The one it replaces is kept as <c>.previous</c>.
/// A release without an installer for this machine (a build of one's own) leaves it as it is.
/// </summary>
public sealed class InstallerProgramStep(InstallScope scope, InstallerMachine machine) : PlanStep
{
    /// <summary>The release's installer: its asset's kind in release.json (its platform is the machine's).</summary>
    public const string AssetKind = "installer";

    public override StepKind Kind => StepKind.File;

    public override string Target { get; } = InstallPaths.Installer(scope, machine);

    public override string Purpose => "hvo-roof-install: upgrade, rollback, backup and uninstall";

    public InstallScope Scope => scope;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (ReleaseFiles.Find(release, AssetKind, context.Machine.RuntimeIdentifier) is not { } asset)
        {
            return new StepCheck(StepChange.Info, $"release {release.Version} has no installer for {context.Machine.RuntimeIdentifier}: the one there, if any, is kept");
        }

        if (context.Machine.DirectoryExists(Target))
        {
            return new StepCheck(StepChange.Blocked, $"{Target} is a folder: move it aside");
        }

        return await ProgramFile.CheckAsync(context.Machine, Target, asset.Sha256, release.Version, cancellationToken).ConfigureAwait(false);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var asset = ReleaseFiles.Find(release, AssetKind, machine.RuntimeIdentifier)
            ?? throw new InstallerException($"Release {release.Version} has no installer for {machine.RuntimeIdentifier}.");
        var folder = Path.GetDirectoryName(Target)!;
        if (!machine.DirectoryExists(folder))
        {
            machine.CreateDirectory(folder, Modes.Folder);
            context.Log.Write($"Made {folder} ({Modes.Octal(Modes.Folder)}).");
        }

        await ProgramFile.ApplyAsync(context, Target, asset.Sha256, "installer", (staged, token) => ReleaseFiles.CopyProgramAsync(context, release, asset, staged, token), cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Installed hvo-roof-install from {asset.Name} (release {release.Version}) as {Target}.");
    }
}

/// <summary>
/// The controller put back to the release before (<c>hvo-roof-install rollback</c>): the deploy script's
/// <c>--rollback</c> swaps it with the one it kept as <c>roof-controller-previous</c>, after the same verified Stop of the
/// roof a deploy makes, and checks the one put back as it checks a new one. Nothing is pulled: the one kept is the
/// release before's, by digest, or there is nothing to go back to.
/// </summary>
public sealed class ControllerRollbackStep(ControllerStep controller) : PlanStep
{
    /// <summary>The controller the deploy script kept when it replaced it: what <c>--rollback</c> puts back.</summary>
    public const string PreviousContainer = MachineSurveyor.ControllerContainer + "-previous";

    /// <summary>What the deploy script names the controller for a moment while it swaps the two.</summary>
    public const string SwapContainer = MachineSurveyor.ControllerContainer + "-swap";

    private string? _deployKeyFile;

    public override StepKind Kind => StepKind.Container;

    public override string Target => MachineSurveyor.ControllerContainer;

    public override string Purpose => $"the controller, swapped with {PreviousContainer} by the deploy script's --rollback";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _deployKeyFile = null;
        var machine = context.Machine;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var current = await MachineSurveyor.SurveyContainerAsync(machine, Target, cancellationToken).ConfigureAwait(false);
        if (current is { IsRunning: true } && current.ImageDigest == release.Controller.Digest)
        {
            return StepCheck.Unchanged($"runs release {release.Version}");
        }

        if (await MachineSurveyor.SurveyContainerAsync(machine, SwapContainer, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new StepCheck(StepChange.Blocked, SwapProblem);
        }

        var previous = await MachineSurveyor.SurveyContainerAsync(machine, PreviousContainer, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return new StepCheck(StepChange.Blocked, $"there is no {PreviousContainer} to go back to: the deploy script keeps the controller it replaces, and has not kept one here");
        }

        if (previous.ImageDigest != release.Controller.Digest)
        {
            return new StepCheck(
                StepChange.Blocked,
                $"{PreviousContainer} is {(previous.Version is { } version ? $"version {version}" : "another image")}, not release {release.Version}'s controller: the deploy script kept another one, so there is no going back to {release.Version} this way");
        }

        if (previous.Origin == ContainerOrigin.Compose || current?.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(StepChange.Blocked, "Docker Compose made the controller, and the installer does not change it: see \"Moving between Compose and the deploy script\" in docs/deployment.md");
        }

        if (previous.State != "exited" && previous.State != "created")
        {
            return new StepCheck(StepChange.Blocked, $"{PreviousContainer} is {previous.State}: two controllers must never drive the HAT. Stop it once the roof is idle (docker stop {PreviousContainer}), then run it again");
        }

        var detail = $"back to release {release.Version}, kept as {PreviousContainer}; the one it replaces is kept there in its turn";
        if (current is not { IsRunning: true })
        {
            return new StepCheck(StepChange.Change, detail);
        }

        try
        {
            var (blocked, keyFile) = await controller.StopKeyAsync(context, "it goes back to the release before", "rolls it back", cancellationToken).ConfigureAwait(false);
            _deployKeyFile = keyFile;
            return blocked ?? new StepCheck(StepChange.Change, $"{detail}, after a verified Stop of the roof");
        }
        catch (UnauthorizedAccessException)
        {
            return new StepCheck(StepChange.Change, $"{detail}; only root can read the keys its verified Stop needs");
        }
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        using var work = new DeployScript.WorkFolder(context.Machine);
        var environment = await controller.EnvironmentAsync(context, work, release.Controller.Reference, _deployKeyFile, cancellationToken).ConfigureAwait(false);
        await ControllerStep.RunScriptAsync(context, work, environment, cancellationToken, "--rollback").ConfigureAwait(false);
        var now = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, CancellationToken.None).ConfigureAwait(false);
        if (now is not { IsRunning: true } || now.ImageDigest != release.Controller.Digest)
        {
            throw new InstallerException($"The deploy script's --rollback finished, but {Target} does not run release {release.Version}'s image: see docker ps -a and the install log.");
        }

        context.Log.Write($"Rolled {Target} back to release {release.Version}, verified by the deploy script; the one it replaced is kept as {PreviousContainer}.");
    }

    /// <summary>Why nothing goes ahead while the deploy script's swap container is there.</summary>
    internal static string SwapProblem
        => $"{SwapContainer} is there: an earlier rollback stopped halfway. Find out which version it is (docker ps -a --filter name={MachineSurveyor.ControllerContainer}), "
            + $"rename it to whichever of {MachineSurveyor.ControllerContainer} and {PreviousContainer} is free (docker rename {SwapContainer} <name>) or remove it, then run it again";
}

/// <summary>
/// The controller stopped before it is removed (<c>hvo-roof-install uninstall</c>): the deploy script's <c>--stop</c>
/// makes the same verified Stop of the roof a deploy makes, then stops the container gracefully. The roof is never left
/// moving with no controller.
/// </summary>
public sealed class ControllerStopStep(ControllerStep controller) : PlanStep
{
    private string? _deployKeyFile;

    public override StepKind Kind => StepKind.Container;

    public override string Target => MachineSurveyor.ControllerContainer;

    public override string Purpose => "the controller, stopped by the deploy script's --stop after a verified Stop of the roof";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _deployKeyFile = null;
        var machine = context.Machine;
        if (await MachineSurveyor.SurveyContainerAsync(machine, ControllerRollbackStep.SwapContainer, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new StepCheck(StepChange.Blocked, ControllerRollbackStep.SwapProblem);
        }

        var current = await MachineSurveyor.SurveyContainerAsync(machine, Target, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return StepCheck.Unchanged("not there");
        }

        if (current.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(StepChange.Blocked, $"Docker Compose made it (project {current.ComposeProject}), and the installer does not remove it: stop it with that project (docker compose down)");
        }

        if (current.IsStopped)
        {
            return StepCheck.Unchanged($"stopped: it is {current.State}");
        }

        if (!current.IsRunning)
        {
            return new StepCheck(StepChange.Blocked, $"it is {current.State}, and the deploy script stops only a running controller: stop it once the roof is idle (docker stop {Target}), then run it again");
        }

        try
        {
            var (blocked, keyFile) = await controller.StopKeyAsync(context, "uninstall removes it", "stops it", cancellationToken).ConfigureAwait(false);
            _deployKeyFile = keyFile;
            return blocked ?? new StepCheck(StepChange.Change, "stopped after a verified Stop of the roof");
        }
        catch (UnauthorizedAccessException)
        {
            return new StepCheck(StepChange.Change, "stopped after a verified Stop of the roof; only root can read the keys it needs");
        }
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var work = new DeployScript.WorkFolder(context.Machine);

        // --stop deploys no image, and ignores IMAGE_REF.
        var environment = await controller.EnvironmentAsync(context, work, string.Empty, _deployKeyFile, cancellationToken).ConfigureAwait(false);
        await ControllerStep.RunScriptAsync(context, work, environment, cancellationToken, "--stop").ConfigureAwait(false);
        var now = await MachineSurveyor.SurveyContainerAsync(context.Machine, Target, CancellationToken.None).ConfigureAwait(false);
        if (now is { IsStopped: false })
        {
            throw new InstallerException($"The deploy script's --stop finished, but {Target} is {now.State}: see docker ps and the install log.");
        }

        context.Log.Write($"Stopped {Target} after a verified Stop of the roof.");
    }
}

/// <summary>
/// A container the installer made, removed (<c>hvo-roof-install uninstall</c>) with its image when no other container
/// uses it. The controller is removed only once stopped (<paramref name="stoppedBy"/>); the HAT emulator, which drives no
/// roof, is stopped here (<paramref name="stops"/>). One Docker Compose made is never removed.
/// </summary>
public sealed class ContainerRemovalStep(string name, string purpose, PlanStep? stoppedBy = null, bool stops = false) : PlanStep
{
    public override StepKind Kind => StepKind.Container;

    public override string Target => name;

    public override string Purpose => purpose;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var container = await MachineSurveyor.SurveyContainerAsync(context.Machine, name, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return StepCheck.Unchanged("not there");
        }

        if (container.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(StepChange.Blocked, $"Docker Compose made it (project {container.ComposeProject}), and the installer does not remove it: remove it with that project (docker compose down)");
        }

        if (!container.IsStopped && !stops && (stoppedBy is null || !await StoppedByAsync(context, cancellationToken).ConfigureAwait(false)))
        {
            return new StepCheck(StepChange.Blocked, $"it is {container.State}: stop it once the roof is idle (docker stop {name}), then run it again");
        }

        return new StepCheck(StepChange.Remove, container.IsStopped ? "removed with its image" : "stopped, then removed with its image");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var container = await MachineSurveyor.SurveyContainerAsync(machine, name, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return;
        }

        if (!container.IsStopped)
        {
            if (!stops)
            {
                throw new InstallerException($"{name} is {container.State} again: stop it once the roof is idle (docker stop {name}), then run it again.");
            }

            await DockerAsync(context, $"stop {name}", cancellationToken, "stop", name).ConfigureAwait(false);
        }

        await DockerAsync(context, $"remove {name}", cancellationToken, "rm", name).ConfigureAwait(false);
        context.Log.Write($"Removed {name}.");

        // Another container (the one kept for a rollback) may use the same image: it then stays, and goes with that one.
        var image = await machine.Commands.RunAsync(new CommandLine("docker", "image", "rm", container.Image) { Timeout = TimeSpan.FromMinutes(2) }, cancellationToken).ConfigureAwait(false);
        context.Log.Write(image.Succeeded ? $"Removed its image {container.Image}." : $"Kept its image {container.Image}: {image.Reason}");
    }

    // The step before stops it in this run: applied, or, for --plan, about to be.
    private async Task<bool> StoppedByAsync(InstallContext context, CancellationToken cancellationToken)
        => context.HasApplied(stoppedBy!) || (await stoppedBy!.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange;

    internal static async Task DockerAsync(InstallContext context, string doing, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await context.Machine.Commands.RunAsync(new CommandLine("docker", arguments) { Timeout = TimeSpan.FromMinutes(2) }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InstallerException($"Could not {doing}: {result.Reason}");
        }
    }
}

/// <summary>The rig's Docker network (<see cref="HatEmulatorStep.Network"/>), removed once its containers are.</summary>
public sealed class NetworkRemovalStep : PlanStep
{
    public override StepKind Kind => StepKind.Container;

    public override string Target => $"network {HatEmulatorStep.Network}";

    public override string Purpose => "the network the controller reaches the HAT emulator on";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var network = await context.Machine.Commands.RunAsync(new CommandLine("docker", "network", "inspect", HatEmulatorStep.Network), cancellationToken).ConfigureAwait(false);
        return network.Succeeded ? new StepCheck(StepChange.Remove, "removed") : StepCheck.Unchanged("not there");
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
        => ContainerRemovalStep.DockerAsync(context, $"remove the {HatEmulatorStep.Network} network", cancellationToken, "network", "rm", HatEmulatorStep.Network);
}

/// <summary>The kiosk's service, stopped and disabled, and its unit removed (<c>hvo-roof-install uninstall</c>).</summary>
public sealed class KioskServiceRemovalStep : PlanStep
{
    public override StepKind Kind => StepKind.Service;

    public override string Target => MachineSurveyor.KioskUnit;

    public override string Purpose => "the kiosk's service: stopped, disabled and its unit removed";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (enabled, active) = await StateAsync(context.Machine, cancellationToken).ConfigureAwait(false);
        return context.Machine.FileExists(MachineSurveyor.KioskUnitFile) || enabled || active
            ? new StepCheck(StepChange.Remove, active ? "stopped, disabled and removed" : "disabled and removed")
            : StepCheck.Unchanged("not there");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var (enabled, active) = await StateAsync(machine, cancellationToken).ConfigureAwait(false);
        if (active)
        {
            await KioskSteps.Run(context, "stop the kiosk", new CommandLine("systemctl", "stop", Target), cancellationToken).ConfigureAwait(false);
        }

        if (enabled)
        {
            await KioskSteps.Run(context, "disable the kiosk", new CommandLine("systemctl", "disable", Target), cancellationToken).ConfigureAwait(false);
        }

        if (machine.FileExists(MachineSurveyor.KioskUnitFile))
        {
            machine.DeleteFile(MachineSurveyor.KioskUnitFile);
            await KioskSteps.Run(context, "reload systemd's units", new CommandLine("systemctl", "daemon-reload"), cancellationToken).ConfigureAwait(false);
        }

        context.Log.Write($"Stopped and disabled {Target}, and removed {MachineSurveyor.KioskUnitFile}.");
    }

    private async Task<(bool Enabled, bool Active)> StateAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var enabled = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-enabled", Target), cancellationToken).ConfigureAwait(false);
        var active = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-active", Target), cancellationToken).ConfigureAwait(false);
        return (enabled.Output.Trim() == "enabled", active.Output.Trim() is "active" or "activating");
    }
}

/// <summary>
/// A file or folder the installer put in place, removed (<c>hvo-roof-install uninstall</c>): a program with the one kept
/// for a rollback (<paramref name="previous"/>), or, with <c>--purge</c>, a folder of data. A folder holding the install
/// record names it (<paramref name="keepLast"/>): everything else in the folder goes first, so a removal that fails part
/// way leaves the record, and <c>uninstall --purge</c> can be run again.
/// </summary>
public sealed class RemovalStep(string path, string purpose, StepKind kind = StepKind.File, string? previous = null, string? keepLast = null) : PlanStep
{
    public override StepKind Kind => kind;

    public override string Target => path;

    public override string Purpose => purpose;

    private IEnumerable<string> Paths => previous is null ? [path] : [path, previous];

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var there = Paths.Where(item => context.Machine.FileExists(item) || context.Machine.DirectoryExists(item)).ToArray();
        return Task.FromResult(there.Length == 0
            ? StepCheck.Unchanged("not there")
            : new StepCheck(StepChange.Remove, there.Length == 2 ? $"removed, with {Path.GetFileName(there[1])}" : there[0] == path ? "removed" : $"{Path.GetFileName(there[0])} removed"));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var item in Paths)
        {
            if (context.Machine.DirectoryExists(item))
            {
                if (keepLast is not null)
                {
                    foreach (var name in context.Machine.ListNames(item).Where(name => name != keepLast))
                    {
                        var inside = Path.Join(item, name);
                        if (context.Machine.DirectoryExists(inside))
                        {
                            context.Machine.DeleteDirectory(inside);
                        }
                        else
                        {
                            context.Machine.DeleteFile(inside);
                        }
                    }
                }

                context.Machine.DeleteDirectory(item);
                context.Log.Write($"Removed {item} and everything in it.");
            }
            else if (context.Machine.FileExists(item))
            {
                context.Machine.DeleteFile(item);
                context.Log.Write($"Removed {item}.");
            }
        }

        return Task.CompletedTask;
    }
}
