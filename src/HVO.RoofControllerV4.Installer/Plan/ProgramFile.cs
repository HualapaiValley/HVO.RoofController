using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// One program file from a release: put in place as a new file (macOS checks a program's signature by its file), with the
/// one it replaces kept as <c>.previous</c>. When the one kept is the one wanted (a rollback, or an upgrade again after
/// one), the two change places instead: nothing is downloaded, and the one replaced is kept in its turn.
/// </summary>
internal static class ProgramFile
{
    public static string Previous(string target) => target + ".previous";

    /// <summary>What putting the program with SHA-256 <paramref name="sha256"/> at <paramref name="target"/> would change.</summary>
    public static async Task<StepCheck> CheckAsync(InstallerMachine machine, string target, string sha256, string version, CancellationToken cancellationToken)
    {
        var current = await machine.Sha256Async(target, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new StepCheck(StepChange.Create, $"release {version}'s, {Modes.Octal(Modes.Program)}");
        }

        if (current != sha256)
        {
            var kept = Path.GetFileName(Previous(target));
            return new StepCheck(
                StepChange.Change,
                await machine.Sha256Async(Previous(target), cancellationToken).ConfigureAwait(false) == sha256
                    ? $"release {version}'s, kept as {kept}: the two change places"
                    : $"release {version}'s; the one there now is kept as {kept}");
        }

        return machine.GetMode(target) is { } mode && mode != Modes.Program
            ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.Program)}")
            : StepCheck.Unchanged($"release {version}'s");
    }

    /// <summary>
    /// Puts the program with SHA-256 <paramref name="sha256"/> at <paramref name="target"/>: its mode set when it is there
    /// already, swapped with the one kept when that is it, else written by <paramref name="write"/> (to the path it is
    /// given, checked against the SHA-256, with <see cref="Modes.Program"/>) and moved into place.
    /// </summary>
    public static async Task ApplyAsync(
        InstallContext context,
        string target,
        string sha256,
        string what,
        Func<string, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (await machine.Sha256Async(target, cancellationToken).ConfigureAwait(false) == sha256)
        {
            machine.SetMode(target, Modes.Program);
            context.Log.Write($"Set {target} to {Modes.Octal(Modes.Program)}.");
            return;
        }

        var previous = Previous(target);
        if (machine.FileExists(target) && await machine.Sha256Async(previous, cancellationToken).ConfigureAwait(false) == sha256)
        {
            var aside = $"{target}.swap";
            machine.MoveFile(target, aside);
            machine.MoveFile(previous, target);
            machine.MoveFile(aside, previous);
            machine.SetMode(target, Modes.Program);
            context.Log.Write($"Put the {what} kept as {previous} back as {target}, and kept the one it replaces as {previous}.");
            return;
        }

        var staged = $"{target}.new";
        try
        {
            await write(staged, cancellationToken).ConfigureAwait(false);
            if (machine.FileExists(target))
            {
                await machine.CopyFileAsync(target, previous, Modes.Program, cancellationToken).ConfigureAwait(false);
                context.Log.Write($"Kept the {what} it replaces as {previous}.");
            }

            // A new file in its place, not the old one written over: macOS checks a program's signature by its file.
            machine.MoveFile(staged, target);
        }
        finally
        {
            if (machine.FileExists(staged))
            {
                machine.DeleteFile(staged);
            }
        }
    }
}
