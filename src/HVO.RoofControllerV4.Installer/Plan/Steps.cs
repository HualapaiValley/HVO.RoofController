using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// A folder with its mode, and its owner when one is given: made when missing, its mode and owner set when they differ.
/// Its contents are never touched.
/// </summary>
public sealed class FolderStep(string path, UnixFileMode mode, string purpose, string? owner = null) : PlanStep
{
    public override StepKind Kind => StepKind.Folder;

    public override string Target => path;

    public override string Purpose => purpose;

    public UnixFileMode Mode => mode;

    /// <summary>Who owns it (<c>user:group</c>), when that matters; null leaves it to whoever makes it.</summary>
    public string? Owner => owner;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var wanted = owner is null ? Modes.Octal(mode) : $"{Modes.Octal(mode)}, {owner}";
        var current = context.Machine.GetMode(path);
        if (current is null)
        {
            return new StepCheck(StepChange.Create, wanted);
        }

        if (context.Machine.FileExists(path))
        {
            return new StepCheck(StepChange.Blocked, $"{path} is a file, not a folder");
        }

        var changes = new List<string>();
        if (current.Value != mode)
        {
            changes.Add($"{Modes.Octal(current.Value)} → {Modes.Octal(mode)}");
        }

        if (owner is not null && await Ownership.GetAsync(context.Machine, path, cancellationToken).ConfigureAwait(false) is var found && found != owner)
        {
            changes.Add($"{found ?? "an unknown owner"} → {owner}");
        }

        return changes.Count > 0 ? new StepCheck(StepChange.Change, string.Join(", ", changes)) : StepCheck.Unchanged(wanted);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(check);
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

        if (owner is not null)
        {
            await Ownership.SetAsync(context, path, owner, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Who owns a file or folder (<c>user:group</c>), told with <c>stat</c> and set with <c>chown</c>.</summary>
public static class Ownership
{
    /// <summary>Who owns <paramref name="path"/>; null when it cannot be told.</summary>
    public static async Task<string?> GetAsync(InstallerMachine machine, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var result = await machine.Commands.RunAsync(new CommandLine("stat", "-c", "%U:%G", "--", path), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Output.Trim() : null;
    }

    /// <summary>Gives <paramref name="path"/> to <paramref name="owner"/>.</summary>
    public static async Task SetAsync(InstallContext context, string path, string owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await context.Machine.Commands.RunAsync(new CommandLine("chown", owner, "--", path), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InstallerException($"Could not give {path} to {owner}: {result.Reason}");
        }

        context.Log.Write($"Gave {path} to {owner}.");
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
        if (problem is not null && InstallRecord.SchemaOf(context.Machine, path) is > InstallRecord.CurrentSchema and var schema)
        {
            return Task.FromResult(new StepCheck(
                StepChange.Blocked,
                $"a newer installer wrote it (schema {schema}), and this one does not replace it: install with that installer, or a newer one"));
        }

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

/// <summary>A TCP port a container publishes. It is free, or the installer's own container has it; anything else listening there blocks the install.</summary>
public sealed class PortStep(int port, string purpose, string container) : PlanStep
{
    public override StepKind Kind => StepKind.Port;

    public override string Target => port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override string Purpose => purpose;

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var owner = await MachineSurveyor.SurveyContainerAsync(context.Machine, container, cancellationToken).ConfigureAwait(false);
        if (owner is { IsRunning: true, Origin: ContainerOrigin.DeployScript } && owner.PublishedPorts.Contains(port))
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
