using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The folders under <c>~/.net</c> that earlier releases' single-file programs unpacked their native libraries into, which
/// nothing removed (#96): <c>hvo-roof</c> and every <c>hvo-roof-*</c> beside it (.NET names the folder after the program's
/// file), removed by every uninstall. For a system install, in every home on the machine (root's, whoever installed it,
/// the kiosk account's, anyone else's); for a person's install, in theirs. A <c>.net</c>, or a folder in it,
/// that is a symbolic link is left alone, never followed, so root never deletes through a link someone put in a home.
/// <c>.net</c> itself goes when this step leaves it empty.
/// </summary>
public sealed class LegacyExtractionStep(InstallScope scope) : PlanStep
{
    public override StepKind Kind => StepKind.Folder;

    public override string Target => "~/.net/hvo-roof*";

    public override string Purpose => "the native libraries earlier releases unpacked, which nothing removed";

    /// <summary>True for the name of a folder our programs unpack into under <c>~/.net</c>.</summary>
    public static bool IsOurs(string name) => name == "hvo-roof" || name.StartsWith("hvo-roof-", StringComparison.Ordinal);

    /// <summary>
    /// The folder of ours under a <c>.net</c> that the full path <paramref name="path"/> is, or is in; null when it is in
    /// none. Read from the path's spelling, as a backup's destination is checked: a backup kept there would go with it.
    /// </summary>
    public static string? FolderHolding(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == ".net" && IsOurs(parts[i + 1]))
            {
                return string.Join('/', parts[..(i + 2)]);
            }
        }

        return null;
    }

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folders = (await HomesAsync(context.Machine, cancellationToken).ConfigureAwait(false))
            .SelectMany(home => Folders(context.Machine, home)).ToList();
        return folders.Count == 0
            ? StepCheck.Unchanged("none there")
            : new StepCheck(StepChange.Remove, $"removed: {string.Join(", ", folders)}");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        foreach (var home in await HomesAsync(machine, cancellationToken).ConfigureAwait(false))
        {
            var folders = Folders(machine, home);
            foreach (var folder in folders)
            {
                machine.DeleteDirectory(folder);
                context.Log.Write($"Removed {folder} and everything in it.");
            }

            if (folders.Count > 0)
            {
                machine.DeleteDirectoryIfEmpty(Path.Join(home, ".net"));
            }
        }
    }

    // Our folders in the home's .net: real folders only, in a .net that is a real folder. None when it cannot be read (a
    // plan run without sudo, say).
    private static IReadOnlyList<string> Folders(InstallerMachine machine, string home)
    {
        var net = Path.Join(home, ".net");
        if (!machine.IsFolderNotLink(net))
        {
            return [];
        }

        try
        {
            return [.. machine.ListNames(net).Where(IsOurs).Select(name => Path.Join(net, name)).Where(machine.IsFolderNotLink)];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The homes to look in: the installer's own, and for a system install (Linux's alone) every home on the machine, since
    // whoever installed it need not be the one uninstalling it, nor the only one who ran our programs: every account's in
    // the account database, and the sudo caller's, which a network directory may answer for when asked by name though it
    // leaves the account out of the full list.
    private async Task<IReadOnlyList<string>> HomesAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var homes = new List<string> { machine.Home };
        if (scope == InstallScope.System)
        {
            homes.Add("/root");
            homes.AddRange(await AccountHomesAsync(machine, null, cancellationToken).ConfigureAwait(false));
            if (machine.IsRoot && machine.Environment("SUDO_USER") is { Length: > 0 } user && !user.Contains('/', StringComparison.Ordinal))
            {
                homes.AddRange(await AccountHomesAsync(machine, user, cancellationToken).ConfigureAwait(false));
            }
        }

        return [.. homes.Where(home => home.Length > 1 && home.StartsWith('/')).Distinct(StringComparer.Ordinal)];
    }

    // The homes in the account database (getent passwd): every account's, or the one user's. None when it cannot be asked.
    private static async Task<IReadOnlyList<string>> AccountHomesAsync(InstallerMachine machine, string? user, CancellationToken cancellationToken)
    {
        var lookup = await machine.Commands.RunAsync(
            user is null ? new CommandLine("getent", "passwd") : new CommandLine("getent", "passwd", user), cancellationToken).ConfigureAwait(false);
        return lookup.Succeeded
            ? [.. lookup.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(':'))
                .Where(fields => fields.Length == 7 && (user is null || fields[0] == user) && fields[5].StartsWith('/'))
                .Select(fields => fields[5])]
            : [];
    }
}
