using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The Alpaca identity bind source, created before deployment. Existing identities and the server's ownership stay
/// intact; a link or a directory other users could replace data in is refused rather than repaired.
/// </summary>
public sealed class AlpacaDataDirectoryStep(ControllerLayout layout) : PlanStep
{
    public override StepKind Kind => StepKind.Folder;

    public override string Target => layout.AlpacaData;

    public override string Purpose => "the Alpaca data directory: stable public device identities";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(machine.OnDisk(Target));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new StepCheck(StepChange.Create, "0755, owned by the installing user until the supervisor prepares it");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Blocked("its type cannot be verified");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
        {
            return Blocked("it must be a directory itself, never a symbolic link or a file");
        }

        var mode = machine.GetMode(Target);
        if (mode is null || (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return Blocked("only its owner may write, and its permissions must be readable");
        }

        var mac = machine.Os == InstallerOs.MacOS;
        var named = await machine.Commands.RunAsync(mac
            ? new CommandLine("stat", "-f", "%Su:%Sg", Target)
            : new CommandLine("stat", "-c", "%U:%G", "--", Target), cancellationToken).ConfigureAwait(false);
        var owner = named.Succeeded ? named.Output.Trim() : null;
        var accepted = owner == "root:root"
            || (mac && owner?.Split(':')[0] == machine.UserName);
        if (!accepted)
        {
            var numeric = await machine.Commands.RunAsync(mac
                ? new CommandLine("stat", "-f", "%u:%g", Target)
                : new CommandLine("stat", "-c", "%u:%g", "--", Target), cancellationToken).ConfigureAwait(false);
            accepted = numeric.Succeeded && numeric.Output.Trim() == "1655:1655";
        }

        if (!accepted)
        {
            return Blocked("its owner must be root or the Alpaca user (UID/GID 1655), or the installing user for a Mac rig");
        }

        return mode == Modes.Folder
            ? StepCheck.Unchanged("0755, existing owner and device identities kept")
            : new StepCheck(StepChange.Change, "set to 0755 for the preflight and server, preserving its owner and contents");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Recheck after earlier steps: a path that appeared after planning must pass the same ownership/type checks.
        var current = await CheckAsync(context, cancellationToken).ConfigureAwait(false);
        if (current.Change == StepChange.Blocked)
        {
            throw new InstallerRefusedException(current.Detail ?? "The Alpaca data directory is unsafe.");
        }

        if (current.Change == StepChange.Create)
        {
            context.Machine.CreateDirectory(Target, Modes.Folder);
            context.Log.Write($"Made {Target} (0755) for the Alpaca device identities.");
        }
        else if (current.Change == StepChange.Change)
        {
            context.Machine.SetMode(Target, Modes.Folder);
            context.Log.Write($"Set {Target} to 0755, keeping its owner and contents.");
        }
    }

    private static StepCheck Blocked(string reason)
        => new(StepChange.Blocked, $"The Alpaca data directory is unsafe: {reason}. Nothing is replaced.");
}
