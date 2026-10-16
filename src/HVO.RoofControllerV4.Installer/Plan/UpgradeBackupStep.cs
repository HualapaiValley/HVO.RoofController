using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The controller's and the kiosk's small stores, backed up before an upgrade changes anything: the archive
/// <c>hvo-roof-install backup</c> writes (the settings, the identity store, the secrets, the certificate and CA, the
/// kiosk's device key and the record; no log, and no program or image, which come from the release), in
/// <see cref="BackupCommands.DefaultFolder"/> as <c>hvo-roof-&lt;host&gt;-&lt;UTC time&gt;-before-&lt;version&gt;.tar.gz</c>. The
/// record names it (<c>upgradeBackup</c>): a rollback across a breaking state change puts back from it the stores the
/// release before cannot read (<c>rollback --restore-backup</c>). Nothing is copied but these small files. A store that is
/// there but that a backup cannot hold (a link) refuses the upgrade before anything changes. An older installer takes it
/// before it replaces itself and hands over (<paramref name="taken"/>): this one then checks it is that backup, from one
/// read of the file. A backups' folder someone other than root could change refuses the upgrade too
/// (<see cref="BackupCommands.FolderProblemAsync"/>): another archive could take the place of the one taken.
/// </summary>
/// <param name="from">The release installed: what the backup is of.</param>
/// <param name="to">The release the upgrade installs.</param>
/// <param name="path">The backup to write.</param>
/// <param name="taken">The backup the installer before this one took and handed over: its file and SHA-256.</param>
public sealed class UpgradeBackupStep(string from, string to, string path, (string Path, string Sha256)? taken = null) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => taken?.Path ?? path;

    public override string Purpose => $"the settings, identity store and secrets as {from} has them, backed up before the upgrade (no logs)";

    /// <summary>
    /// The backup the record names once the upgrade is done: this one, or the one an earlier run of the same upgrade took
    /// before it replaced the controller. Null when there is none. Set by the last check.
    /// </summary>
    public string? Kept { get; private set; }

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Kept = null;
        var machine = context.Machine;
        if (!machine.IsRoot)
        {
            return new StepCheck(StepChange.Info, "only root reads the stores: run with sudo, and the upgrade backs them up first");
        }

        if (await BackupCommands.FolderProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } folder)
        {
            return new StepCheck(StepChange.Blocked, folder);
        }

        if (taken is { } handed)
        {
            if (await HandedOverProblemAsync(context, handed, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return new StepCheck(StepChange.Blocked, $"{handed.Path}, the backup the installer before this one took before it handed over, {problem}: run the upgrade again");
            }

            Kept = handed.Path;
            return StepCheck.Unchanged($"taken before the installer was replaced, {Modes.Octal(Modes.PrivateFile)}, root's");
        }

        if (machine.FileExists(path))
        {
            Kept = path;
            return StepCheck.Unchanged($"{Modes.Octal(Modes.PrivateFile)}, root's");
        }

        // An earlier run of this upgrade replaced the controller already, and the new one may have moved its stores on:
        // the backup from before it is the one to keep, not one of the stores as they are now.
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var controller = await MachineSurveyor.SurveyContainerAsync(machine, MachineSurveyor.ControllerContainer, cancellationToken).ConfigureAwait(false);
        if (controller?.ImageDigest == release.Controller.Digest)
        {
            Kept = await EarlierAsync(context, cancellationToken).ConfigureAwait(false);
            return Kept is null
                ? new StepCheck(StepChange.Info, $"the controller runs {to} already, so the stores are not backed up now: there is no backup from before it")
                : StepCheck.Unchanged($"taken before an earlier run of this upgrade: {Kept}");
        }

        foreach (var store in BackupCommands.StoreFiles)
        {
            if (await BackupCommands.CannotHoldAsync(machine, store, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return new StepCheck(StepChange.Blocked, problem);
            }
        }

        Kept = path;
        return new StepCheck(StepChange.Create, $"{Modes.Octal(Modes.PrivateFile)}, root's");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var written = await BackupCommands.WriteArchiveAsync(
            machine,
            context.Log,
            context.Survey.HostName,
            context.Version,
            from,
            context.Time.GetUtcNow(),
            async () =>
            {
                await BackupCommands.MakeDefaultFolderAsync(machine, cancellationToken).ConfigureAwait(false);
                return path;
            },
            cancellationToken,
            storesRequired: true).ConfigureAwait(false);
        Kept = written.Path;
        context.Log.Write($"Backed up the stores of {from} to {written.Path} before the upgrade to {to}.");
    }

    // Why the backup handed over is not the one the installer before this one took, of the release installed here: null
    // when it is (in the backups' folder, its SHA-256 as handed over, root's and whole, this host's, of that release). The
    // file is opened and read once: the bytes hashed are the bytes checked, whatever takes the path's place meanwhile.
    private async Task<string?> HandedOverProblemAsync(InstallContext context, (string Path, string Sha256) handed, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (Path.GetDirectoryName(handed.Path) != BackupCommands.DefaultFolder)
        {
            return $"is not in {BackupCommands.DefaultFolder}";
        }

        if (!machine.FileExists(handed.Path))
        {
            return "is not there";
        }

        try
        {
            var archive = await BackupCommands.ReadPrivateAsync(machine, handed.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archive)), handed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "is not the file it took (its SHA-256 differs)";
            }

            var backup = await BackupCommands.ParseAsync(archive, handed.Path, cancellationToken).ConfigureAwait(false);
            return backup.Manifest.RecordVersion != from || !string.Equals(backup.Manifest.Host, context.Survey.HostName, StringComparison.OrdinalIgnoreCase)
                ? $"is not this machine's backup of {from}"
                : null;
        }
        catch (InstallerRefusedException error)
        {
            return $"cannot be read as a backup ({error.Message.TrimEnd('.')})";
        }
    }

    // The newest backup an earlier run of this upgrade took: one of this host's before this release, of the release installed.
    private async Task<string?> EarlierAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (!machine.DirectoryExists(BackupCommands.DefaultFolder))
        {
            return null;
        }

        var prefix = $"hvo-roof-{context.Survey.HostName.Split('.')[0]}-";
        var suffix = $"-before-{to}.tar.gz";
        foreach (var name in machine.ListNames(BackupCommands.DefaultFolder)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
            .OrderDescending(StringComparer.Ordinal))
        {
            var candidate = Path.Join(BackupCommands.DefaultFolder, name);
            try
            {
                var backup = await BackupCommands.ReadAsync(machine, candidate, cancellationToken).ConfigureAwait(false);
                if (backup.Manifest.RecordVersion == from)
                {
                    return candidate;
                }
            }
            catch (InstallerRefusedException error)
            {
                context.Log.Write($"Not the backup from before this upgrade: {error.Message}");
            }
        }

        return null;
    }
}
