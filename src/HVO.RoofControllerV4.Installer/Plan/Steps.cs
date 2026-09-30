using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>A folder with its mode: made when missing, its mode set when it differs. Its contents are never touched.</summary>
public sealed class FolderStep(string path, UnixFileMode mode, string purpose) : PlanStep
{
    public override StepKind Kind => StepKind.Folder;

    public override string Target => path;

    public override string Purpose => purpose;

    public UnixFileMode Mode => mode;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var current = context.Machine.GetMode(path);
        return Task.FromResult(current switch
        {
            null => new StepCheck(StepChange.Create, Modes.Octal(mode)),
            _ when context.Machine.FileExists(path) => new StepCheck(StepChange.Blocked, $"{path} is a file, not a folder"),
            _ when current.Value != mode => new StepCheck(StepChange.Change, $"{Modes.Octal(current.Value)} → {Modes.Octal(mode)}"),
            _ => StepCheck.Unchanged(Modes.Octal(mode))
        });
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        if (check.Change == StepChange.Create)
        {
            context.Machine.CreateDirectory(path, mode);
            context.Log.Write($"Made {path} ({Modes.Octal(mode)}).");
        }
        else
        {
            context.Machine.SetMode(path, mode);
            context.Log.Write($"Set {path} to {Modes.Octal(mode)}.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The install record: written last, once everything before it is in place, and only when it would say something new
/// (the times it holds do not count).
/// </summary>
public sealed class RecordStep(InstallScope scope, string path, Func<InstallContext, InstallRecord?, InstallRecord> build) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => path;

    public override string Purpose => "the install record: the roles, choices and versions (no secrets)";

    public InstallScope Scope => scope;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var (existing, problem) = InstallRecord.Load(context.Machine, path);
        var record = build(context, existing);
        var mode = ModeFor(scope);
        return Task.FromResult(
            problem is not null ? new StepCheck(StepChange.Change, "replaces the record that could not be read")
            : existing is null ? new StepCheck(StepChange.Create, Modes.Octal(mode))
            : !record.SameAs(existing) ? new StepCheck(StepChange.Change, DescribeChange(existing, record))
            : context.Machine.GetMode(path) != mode ? new StepCheck(StepChange.Change, $"{Modes.Octal(context.Machine.GetMode(path)!.Value)} → {Modes.Octal(mode)}")
            : StepCheck.Unchanged(Modes.Octal(mode)));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var (existing, _) = InstallRecord.Load(context.Machine, path);
        var now = context.Time.GetUtcNow();
        var record = build(context, existing) with { InstalledAt = existing?.InstalledAt ?? now, UpdatedAt = now };
        var folder = Path.GetDirectoryName(path)!;
        if (!context.Machine.DirectoryExists(folder))
        {
            context.Machine.CreateDirectory(folder, scope == InstallScope.System ? Modes.Folder : Modes.PrivateFolder);
        }

        context.Machine.WriteAtomically(path, record.ToJson(), ModeFor(scope));
        context.Log.Write($"Wrote {path}: {InstallRoles.Describe(record.Roles)}, version {record.Version}.");
        return Task.CompletedTask;
    }

    private static UnixFileMode ModeFor(InstallScope scope) => scope == InstallScope.System ? Modes.File : Modes.PrivateFile;

    private static string DescribeChange(InstallRecord before, InstallRecord after)
    {
        if (!before.Roles.SequenceEqual(after.Roles))
        {
            return $"roles: {string.Join(", ", before.Roles.Select(InstallRoles.Name))} → {string.Join(", ", after.Roles.Select(InstallRoles.Name))}";
        }

        return before.Version != after.Version ? $"version {before.Version} → {after.Version}" : "the choices changed";
    }
}

/// <summary>
/// A container: the controller or a rig's HAT emulator. One the deploy script made is adopted as it is; one Docker Compose
/// made is explained and never replaced. This installer does not deploy one yet (by digest, through the deploy script):
/// the step says what it would make, and the install is refused before anything changes when it would have to.
/// </summary>
public sealed class ContainerStep(string name, HatMode? hat, string purpose) : PlanStep
{
    public override StepKind Kind => StepKind.Container;

    public override string Target => name;

    public override string Purpose => purpose;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var container = await MachineSurveyor.SurveyContainerAsync(context.Machine, name, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return new StepCheck(StepChange.Create, "deployed by digest with the deploy script");
        }

        if (container.Origin == ContainerOrigin.Compose)
        {
            return new StepCheck(
                StepChange.Blocked,
                $"Docker Compose made it (project {container.ComposeProject}), and the installer does not replace it. To move it to the installer, see \"Moving between Compose and the deploy script\" in docs/deployment.md.");
        }

        if (hat is { } wanted && (container.HatEmulator is null ? HatMode.Real : HatMode.Emulated) != wanted)
        {
            return new StepCheck(StepChange.Change, wanted == HatMode.Real ? "redeployed for the real HAT" : "redeployed against the HAT emulator");
        }

        return StepCheck.Unchanged($"adopted: {container.State}{(container.Version is { } version ? $", version {version}" : string.Empty)}");
    }

    public override bool CanApply => false;

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
        => throw new InstallerException($"This installer cannot deploy {name} yet. Deploy it with the deploy script (docs/deployment.md), then run the installer again: it adopts it.");
}

/// <summary>A TCP port a container publishes. It is free, or the installer's own container has it; anything else listening there blocks the install.</summary>
public sealed class PortStep(int port, string purpose, string container) : PlanStep
{
    public override StepKind Kind => StepKind.Port;

    public override string Target => port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override string Purpose => purpose;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var owner = await MachineSurveyor.SurveyContainerAsync(context.Machine, container, cancellationToken).ConfigureAwait(false);
        if (owner is { IsRunning: true, Origin: ContainerOrigin.DeployScript })
        {
            return new StepCheck(StepChange.Info, $"{container} listens on it");
        }

        return context.Machine.IsPortInUse(port)
            ? new StepCheck(StepChange.Blocked, $"something else listens on port {port}: stop it, or choose another port")
            : new StepCheck(StepChange.Info, $"free; {container} will listen on it");
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A part of a role that a later installer issue builds (the kiosk's service, hvo-roof, the Mac app). It is shown in the
/// plan as what it will be, found when already there, and the install is refused before anything changes when it would
/// have to be made.
/// </summary>
public sealed class LaterStep(StepKind kind, string target, string purpose, Func<InstallContext, bool> isThere) : PlanStep
{
    public override bool CanApply => false;

    public override StepKind Kind => kind;

    public override string Target => target;

    public override string Purpose => purpose;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.FromResult(isThere(context) ? StepCheck.Unchanged("already there") : new StepCheck(StepChange.Create));

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
        => throw new InstallerException($"This installer cannot install {target} yet.");
}
