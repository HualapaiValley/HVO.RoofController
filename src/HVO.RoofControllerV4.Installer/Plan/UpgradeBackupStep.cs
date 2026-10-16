using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The controller's and the kiosk's small stores, backed up before an upgrade changes anything: the archive
/// <c>hvo-roof-install backup</c> writes (the settings, the identity store, the secrets, the certificate and CA, the
/// kiosk's device key and the record; no log, and no program or image, which come from the release), in
/// <see cref="BackupCommands.DefaultFolder"/> as <c>hvo-roof-&lt;host&gt;-&lt;UTC time&gt;-before-&lt;version&gt;.tar.gz</c>. The
/// record names it (<c>upgradeBackup</c>): a rollback across a breaking state change puts back from it the stores the
/// release before cannot read (<c>rollback --restore-backup</c>). Nothing is copied but these small files.
/// </summary>
/// <param name="from">The release installed: what the backup is of.</param>
/// <param name="to">The release the upgrade installs.</param>
/// <param name="path">The backup to write.</param>
public sealed class UpgradeBackupStep(string from, string to, string path) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => path;

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
            () =>
            {
                BackupCommands.MakeDefaultFolder(machine);
                return path;
            },
            cancellationToken).ConfigureAwait(false);
        Kept = written.Path;
        context.Log.Write($"Backed up the stores of {from} to {written.Path} before the upgrade to {to}.");
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
