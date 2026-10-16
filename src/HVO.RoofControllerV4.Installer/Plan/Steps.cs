using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// A folder with its mode, and its owner when one is given: made when missing, its mode and owner set when they differ.
/// Its contents are never touched. With <paramref name="keepsMode"/>, a folder that is there is left as it is: one of the
/// person's own, such as <c>~/.local/bin</c>, which the installer only makes when it is missing.
/// </summary>
public sealed class FolderStep(string path, UnixFileMode mode, string purpose, string? owner = null, bool keepsMode = false) : PlanStep
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

        if (keepsMode)
        {
            return StepCheck.Unchanged("there already, and kept as it is");
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
/// (the times it holds do not count), with its folder synced so that it says so after a power loss
/// (<see cref="InstallerMachine.WriteDurably"/>). An upgrade marks it first (<see cref="MarkUpgradeStarted"/>).
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
        var record = build(context, existing);

        // A reinstall after uninstall is installed afresh.
        var reinstalled = existing?.UninstalledAt is not null && record.UninstalledAt is null;
        record = record with { InstalledAt = existing is null || reinstalled ? now : existing.InstalledAt, UpdatedAt = now };
        var folder = Path.GetDirectoryName(path)!;
        if (!context.Machine.DirectoryExists(folder))
        {
            context.Machine.CreateDirectory(folder, scope == InstallScope.System ? Modes.Folder : Modes.PrivateFolder);
        }

        context.Machine.WriteDurably(path, record.ToJson(), ModeFor(scope));
        context.Log.Write($"Wrote {path}: {InstallRoles.Describe(record.Roles)}, version {record.Version}.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Before the run's first change, when the record here has an older release than the run's: the record says the run's
    /// release from then on (<see cref="InstallRecord.UpgradeStarted"/>). A run that stops part way, with the new controller
    /// deployed and the kiosk not, say, then never leaves the newer release under a record of the older one, which an
    /// upgrade or an older installer would take as the release to go back to. This step clears the mark at the end.
    /// The record's folder is synced before any change, even when the mark was written by a run before this one: a power
    /// loss must not take the record back to the older release once a newer controller may be deployed. When that cannot
    /// be made sure of, nothing else changes.
    /// </summary>
    public void MarkUpgradeStarted(InstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (existing, _) = InstallRecord.Load(context.Machine, path);
        if (existing is null)
        {
            return;
        }

        try
        {
            if (RoofSemVer.IsValid(context.Version) && RoofSemVer.IsOlder(existing.Version, context.Version))
            {
                var record = existing.UpgradeStarted(context.Version) with { UpdatedAt = context.Time.GetUtcNow() };
                context.Machine.WriteDurably(path, record.ToJson(), ModeFor(scope));
                context.Log.Write($"Wrote {path}: version {record.Version}, its upgrade from {existing.Version} not finished yet.");
            }
            else
            {
                // A run that stopped before its sync (below) may have left the mark, or a restore the record, unsynced.
                context.Machine.SyncDirectory(Path.GetDirectoryName(path)!);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var message = $"Before its first change, the installer could not make sure that {path} says {context.Version} after a power loss: {error.Message} "
                + "Nothing else was changed.";
            context.Log.Write($"Failed: {message}");
            throw new InstallerException(message, error);
        }
    }

    private static UnixFileMode ModeFor(InstallScope scope) => scope == InstallScope.System ? Modes.File : Modes.PrivateFile;

    private static string DescribeChange(InstallRecord before, InstallRecord after)
    {
        if (!before.Roles.SequenceEqual(after.Roles))
        {
            return $"roles: {Names(before.Roles)} → {Names(after.Roles)}";
        }

        return before.Version != after.Version ? $"version {before.Version} → {after.Version}"
            : before.UpgradingFrom is { } from && after.UpgradingFrom is null ? $"the upgrade from {from} to {after.Version} finishes"
            : "the choices changed";
    }

    private static string Names(IReadOnlyList<InstallRole> roles) => roles.Count == 0 ? "none" : string.Join(", ", roles.Select(InstallRoles.Name));
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
