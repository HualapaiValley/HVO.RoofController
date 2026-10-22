using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The folders under <c>~/.net</c> that earlier releases' single-file programs unpacked their native libraries into, which
/// nothing removed (#96): <c>hvo-roof</c> and every <c>hvo-roof-*</c> beside it (.NET names the folder after the program's
/// file), removed by every uninstall. For a system install, in root's home, the home of the person who ran sudo and the
/// kiosk account's; for a person's install, in theirs. A <c>.net</c>, or a folder in it, that is a symbolic link is left
/// alone, never followed, so root never deletes through a link someone put in a home. <c>.net</c> itself goes when this
/// step leaves it empty.
/// </summary>
public sealed class LegacyExtractionStep(InstallScope scope) : PlanStep
{
    public override StepKind Kind => StepKind.Folder;

    public override string Target => "~/.net/hvo-roof*";

    public override string Purpose => "the native libraries earlier releases unpacked, which nothing removed";

    /// <summary>True for the name of a folder our programs unpack into under <c>~/.net</c>.</summary>
    public static bool IsOurs(string name) => name == "hvo-roof" || name.StartsWith("hvo-roof-", StringComparison.Ordinal);

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

    // The homes to look in: the installer's own, and for a system install root's, the sudo caller's and the kiosk account's.
    private async Task<IReadOnlyList<string>> HomesAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var mac = machine.Os == InstallerOs.MacOS;
        var homes = new List<string> { machine.Home };
        if (scope == InstallScope.System)
        {
            homes.Add(await HomeOfAsync(machine, "root", cancellationToken).ConfigureAwait(false) ?? (mac ? "/var/root" : "/root"));
            if (machine.IsRoot && machine.Environment("SUDO_USER") is { Length: > 0 } user && user != "root" && !user.Contains('/', StringComparison.Ordinal))
            {
                homes.Add(await HomeOfAsync(machine, user, cancellationToken).ConfigureAwait(false) ?? (mac ? $"/Users/{user}" : $"/home/{user}"));
            }

            if (await HomeOfAsync(machine, KioskSteps.User, cancellationToken).ConfigureAwait(false) is { } kiosk)
            {
                homes.Add(kiosk);
            }
        }

        return [.. homes.Where(home => home.Length > 1 && home.StartsWith('/')).Distinct(StringComparer.Ordinal)];
    }

    // The account's home in the account database (Linux); null when there is no such account or no database to ask.
    private static async Task<string?> HomeOfAsync(InstallerMachine machine, string user, CancellationToken cancellationToken)
    {
        if (machine.Os != InstallerOs.Linux)
        {
            return null;
        }

        var lookup = await machine.Commands.RunAsync(new CommandLine("getent", "passwd", user), cancellationToken).ConfigureAwait(false);
        var fields = lookup.Output.Trim().Split(':');
        return lookup.Succeeded && fields.Length == 7 && fields[0] == user && fields[5].StartsWith('/') ? fields[5] : null;
    }
}
