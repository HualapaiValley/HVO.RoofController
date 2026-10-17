using System.Globalization;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// One of a store's files put back as the backup the upgrade took has it (<c>hvo-roof-install rollback
/// --restore-backup</c>): the release before cannot read the store as it is now. The file it replaces is kept beside it,
/// only root's, as <c>&lt;file&gt;.rolled-back-from-&lt;version&gt;-&lt;UTC time&gt;</c>; a file the backup's manifest says
/// was not there is moved there. Nothing else in its folder changes (a kiosk's retired commissioning stays retired). It
/// runs only with the controller stopped: the deploy script's verified Stop and <c>--stop</c> come first, and just before
/// anything is written Docker must say that the controller and the one kept for a rollback
/// (<see cref="ControllerRollbackStep.PreviousContainer"/>) are each stopped or not there, and that no swap was left
/// halfway (<see cref="ControllerRollbackStep.SwapContainer"/>). Each change is durable before the next, and all of them
/// before the deploy script starts the controller before (<see cref="InstallerMachine.WriteDurably(string, byte[], UnixFileMode)"/>,
/// <see cref="InstallerMachine.MoveFileDurably"/>, <see cref="InstallerMachine.DeleteFileDurably"/>).
/// </summary>
/// <param name="path">The store's file.</param>
/// <param name="purpose">What the store is, for the plan.</param>
/// <param name="content">
/// The file as the backup has it; null when the backup's manifest says it was not there (the store was never saved then).
/// </param>
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
            current = machine.ReadRegularBytes(path);
        }
        catch (NotARegularFileException error)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, $"{path} is {error.Kind}: a backup puts back only a file. Move it aside, then run the rollback again"));
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
        await ConfirmNoControllerRunsAsync(machine, cancellationToken).ConfigureAwait(false);
        try
        {
            // Each change is durable before the next (the file, then its folder): the file kept beside the store first, as
            // it holds every change made since the upgrade, then the store put back or moved aside.
            if (machine.ReadRegularBytes(path) is { } current && (content is null || !current.AsSpan().SequenceEqual(content)))
            {
                var kept = $"{path}.rolled-back-from-{rolledBackFrom}-{context.Time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
                machine.WriteDurably(kept, current, Modes.PrivateFile);
                context.Log.Write($"Kept {path} as {kept} ({Modes.Octal(Modes.PrivateFile)}).");
            }

            if (content is null)
            {
                if (machine.FileExists(path))
                {
                    machine.DeleteFileDurably(path);
                }

                context.Log.Write($"Moved {path} aside: the backup from before the upgrade has none.");
                return;
            }

            await PutBackAsync(context, content, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error) when (error is not NotARegularFileException)
        {
            throw new InstallerException(
                $"{path} could not be made sure of on disk ({error.Message}), so the controller before is not started on it: a power loss could take it back. Nothing was swapped; run the rollback again.",
                error);
        }

        context.Log.Write($"Put back {path} ({Modes.Octal(mode)}) from the backup taken before the upgrade.");
    }

    // The file staged beside the store with its content, its mode and its owner, then renamed over it durably (the staged
    // file synced, then the folder): the store's name never shows a file whose content or owner is not on disk yet.
    private async Task PutBackAsync(InstallContext context, byte[] content, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var staged = $"{path}.restoring";
        try
        {
            machine.WriteAtomically(staged, content, mode);
            if (owner is not null && await Ownership.GetAsync(machine, staged, cancellationToken).ConfigureAwait(false) != owner)
            {
                await Ownership.SetAsync(context, staged, owner, cancellationToken).ConfigureAwait(false);
            }

            machine.MoveFileDurably(staged, path, overwrite: true);
        }
        finally
        {
            if (machine.FileExists(staged))
            {
                machine.DeleteFile(staged);
            }
        }
    }

    // Never under a running controller: it would save its own over the file, or run on a store it cannot read. Either
    // controller container may run one (the deploy script's --stop leaves the one kept for a rollback alone, and someone
    // may start it once the plan is checked), and a swap left halfway may be either. Docker must say each is stopped or
    // not there (made, exited or dead: never paused or restarting), and that there is no swap's; when it cannot say,
    // nothing is written.
    private async Task ConfirmNoControllerRunsAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { MachineSurveyor.ControllerContainer, ControllerRollbackStep.PreviousContainer, ControllerRollbackStep.SwapContainer })
        {
            ContainerSurvey? container;
            try
            {
                container = await MachineSurveyor.ConfirmContainerAsync(machine, name, cancellationToken).ConfigureAwait(false);
            }
            catch (InstallerException error)
            {
                throw new InstallerException($"{path} is put back only with the controller stopped, and {error.Message} Nothing was put back.", error);
            }

            if (container is null)
            {
                continue;
            }

            if (name == ControllerRollbackStep.SwapContainer)
            {
                throw new StepBlockedException($"{path} was not put back: {ControllerRollbackStep.SwapProblem}.");
            }

            if (!container.IsStopped)
            {
                throw new StepBlockedException(
                    $"{name} is {container.State}: {path} is put back only with the controller stopped, and the one kept for a rollback too. "
                    + $"Nothing was put back; stop it once the roof is idle (docker stop {name}), then run the rollback again.");
            }
        }
    }
}

/// <summary>
/// What <c>rollback --restore-backup</c> puts back from <paramref name="Backup"/>: a step for each file of the
/// <paramref name="Stores"/> the release before cannot read, and those stores' schemas once they are back.
/// </summary>
public sealed record StateRestore(string Backup, IReadOnlyList<string> Stores, IReadOnlyList<StateRestoreStep> Steps, IReadOnlyList<StoreState> States);
