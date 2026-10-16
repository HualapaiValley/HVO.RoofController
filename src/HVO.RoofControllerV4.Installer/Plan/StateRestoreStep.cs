using System.Globalization;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// One of a store's files put back as the backup the upgrade took has it (<c>hvo-roof-install rollback
/// --restore-backup</c>): the release before cannot read the store as it is now. The file it replaces is kept beside it,
/// only root's, as <c>&lt;file&gt;.rolled-back-from-&lt;version&gt;-&lt;UTC time&gt;</c>; a file the backup does not have
/// is moved there. Nothing else in its folder changes (a kiosk's retired commissioning stays retired). It runs only with
/// the controller stopped: the deploy script's verified Stop and <c>--stop</c> come first, and Docker must say the
/// controller is stopped or not there before anything is written.
/// </summary>
/// <param name="path">The store's file.</param>
/// <param name="purpose">What the store is, for the plan.</param>
/// <param name="content">The file as the backup has it; null when the backup has none (the store was never saved then).</param>
/// <param name="mode">Its mode in the backup.</param>
/// <param name="owner">Its owner in the backup (user:group), which the controller must still be to read it; null when unknown.</param>
/// <param name="rolledBackFrom">The release the rollback goes back from, for the name of the file kept.</param>
public sealed class StateRestoreStep(string path, string purpose, byte[]? content, UnixFileMode mode, string? owner, string rolledBackFrom) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => path;

    public override string Purpose => purpose;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        if (machine.DirectoryExists(path))
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, $"{path} is a folder: a backup puts back only the file. Move it aside, then run the rollback again"));
        }

        byte[]? current;
        try
        {
            current = machine.ReadBytes(path);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, machine.IsRoot ? $"{path} cannot be read, even as root" : "only root can read it: run with sudo"));
        }

        if (content is null)
        {
            return Task.FromResult(current is null
                ? StepCheck.Unchanged("not in the backup, and not here")
                : new StepCheck(StepChange.Remove, "moved aside: the backup from before the upgrade has none"));
        }

        return Task.FromResult(current is null ? new StepCheck(StepChange.Create, $"as the backup has it, {Modes.Octal(mode)}")
            : !current.AsSpan().SequenceEqual(content) ? new StepCheck(StepChange.Change, "put back as the backup has it; the one here kept beside it")
            : machine.GetMode(path) != mode ? new StepCheck(StepChange.Change, $"{Modes.Octal(machine.GetMode(path)!.Value)} → {Modes.Octal(mode)}")
            : StepCheck.Unchanged("as the backup has it"));
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;

        // Never under a running controller: it would save its own over the file, or run on a store it cannot read. Docker
        // must say it is stopped or not there; when it cannot say, nothing is written.
        ContainerSurvey? controller;
        try
        {
            controller = await MachineSurveyor.ConfirmContainerAsync(machine, MachineSurveyor.ControllerContainer, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error)
        {
            throw new InstallerException($"{path} is put back only with the controller stopped, and {error.Message} Nothing was put back.", error);
        }

        if (controller is { IsStopped: false })
        {
            throw new InstallerException($"{MachineSurveyor.ControllerContainer} is {controller.State}: {path} is put back only with the controller stopped. Nothing was put back; run the rollback again.");
        }

        if (machine.ReadBytes(path) is { } current && (content is null || !current.AsSpan().SequenceEqual(content)))
        {
            var kept = $"{path}.rolled-back-from-{rolledBackFrom}-{context.Time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
            machine.WriteAtomically(kept, current, Modes.PrivateFile);
            context.Log.Write($"Kept {path} as {kept} ({Modes.Octal(Modes.PrivateFile)}).");
        }

        if (content is null)
        {
            if (machine.FileExists(path))
            {
                machine.DeleteFile(path);
            }

            context.Log.Write($"Moved {path} aside: the backup from before the upgrade has none.");
            return;
        }

        machine.WriteAtomically(path, content, mode);
        if (owner is not null && await Ownership.GetAsync(machine, path, cancellationToken).ConfigureAwait(false) != owner)
        {
            await Ownership.SetAsync(context, path, owner, cancellationToken).ConfigureAwait(false);
        }

        context.Log.Write($"Put back {path} ({Modes.Octal(mode)}) from the backup taken before the upgrade.");
    }
}

/// <summary>
/// What <c>rollback --restore-backup</c> puts back from <paramref name="Backup"/>: a step for each file of the
/// <paramref name="Stores"/> the release before cannot read, and those stores' schemas once they are back.
/// </summary>
public sealed record StateRestore(string Backup, IReadOnlyList<string> Stores, IReadOnlyList<StateRestoreStep> Steps, IReadOnlyList<StoreState> States);
