using System.Globalization;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// The state contracts (docs/install.md, "State contracts"): before <c>upgrade</c> and <c>rollback</c> change anything,
/// the schemas of the stores the controller keeps here against what the target release reads. A target that cannot read
/// them is refused (3), nothing changed; a rollback across a breaking change goes ahead only putting back, from the
/// backup the upgrade took, the stores the release before cannot read (<c>--restore-backup</c>), said plainly first.
/// </summary>
internal static partial class LifecycleCommands
{
    /// <summary>
    /// Before an upgrade to <paramref name="target"/> from <paramref name="from"/> (the release installed, or the one
    /// an unfinished upgrade started from): refuses one that cannot read the stores here, and otherwise says what they are,
    /// and when <paramref name="tell"/>, whether the target makes a breaking change from the release the controller here
    /// runs. Nothing to check where the record runs no controller.
    /// </summary>
    private static void CheckUpgradeState(InstallerHost host, InstallerSession session, InstallRecord record, string from, ReleaseManifest target, bool tell)
    {
        if (!InstallRoles.RunsController(record.Roles))
        {
            return;
        }

        var machine = session.Machine;
        var states = StateContractGuard.Read(machine, ControllerLayout.For(machine));
        if (StateContractGuard.Incompatible(states, target) is { Count: > 0 } problems)
        {
            session.Log.Write($"Refused: release {target.Version} cannot read the stores here: {string.Join("; ", problems)}.");
            throw new InstallerRefusedException(
                $"Release {target.Version} cannot read the data here: {string.Join("; ", problems)}. "
                + $"Upgrade through a release in between first (the upgrade notes of {target.Version} say which), or stay on {from}. Nothing was changed.");
        }

        if (!tell)
        {
            return;
        }

        host.Out.WriteLine();
        host.Out.WriteLine($"The stores here: {DescribeStates(states)}. Release {target.Version} reads them.");
        var installed = session.Survey.Controller is { } running ? StateContract.FromLabels(running.StateContracts, running.IdentitySchema) : null;
        if (installed is null || target.StateContract.BreakingFrom(installed) is not { Count: > 0 } breaking)
        {
            return;
        }

        var changes = string.Join(" and ", breaking.Select(change =>
            $"{StateContract.Describe(change.Store)} to schema {change.Target.Writes}, which {from} does not read (it reads {change.Installed.DescribeReads()})"));
        var backup = machine.Os == InstallerOs.Linux && ScopeFor(machine) == InstallScope.System;
        host.Out.WriteLine(
            $"Release {target.Version} makes a breaking state change: it may move {changes}. "
            + (backup
                ? $"The upgrade backs up the stores first. Once a store has moved, a rollback to {from} puts it back from that backup ({Sudo(InstallScope.System)}{Installer.CommandName} rollback --restore-backup), and what changed in it after the upgrade is lost."
                : $"Once a store has moved, there is no going back to {from}: only forward, to a release that reads it."));
        session.Log.Write($"Release {target.Version} makes a breaking state change from {from}: {changes}.");
    }

    /// <summary>
    /// Set by <c>upgrade</c>, for the new release's installer it hands over to, to the backup it took before it replaced
    /// itself (with <see cref="UpgradeBackupSha256Variable"/>): that installer checks it and keeps it in place of taking
    /// another.
    /// </summary>
    public const string UpgradeBackupVariable = "HVO_ROOF_INSTALL_UPGRADE_BACKUP";

    /// <summary>The SHA-256 of the backup <see cref="UpgradeBackupVariable"/> names.</summary>
    public const string UpgradeBackupSha256Variable = "HVO_ROOF_INSTALL_UPGRADE_BACKUP_SHA256";

    /// <summary>
    /// Before this installer is replaced by the release's it hands over to: the backup the upgrade takes first, so one that
    /// cannot be taken changes nothing (3). The release's installer is told its file and SHA-256 in
    /// <paramref name="environment"/>.
    /// </summary>
    private static async Task BackUpBeforeHandingOverAsync(InstallerHost host, InstallerSession session, IDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        if (session.UpgradeBackup() is not { } step)
        {
            return;
        }

        var checkedPlan = await new InstallPlan([step]).CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var check = checkedPlan.Steps[0].Check;
        if (check.Change == StepChange.Blocked)
        {
            session.Log.Write($"Refused: {check.Detail}.");
            throw new InstallerRefusedException($"{check.Detail}. Nothing was changed.");
        }

        if (check.MakesChange)
        {
            await checkedPlan.ApplyAsync(session.ContextFor(host.Out.WriteLine), host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }

        if (step.Kept is { } kept)
        {
            // Durable before this installer is replaced, whichever run took it; then read as the release's installer reads
            // it, once: the SHA-256 handed over is of the archive checked here.
            try
            {
                BackupCommands.MakeDurable(session.Machine, kept);
            }
            catch (IOException error)
            {
                var message = $"Before it hands over, the installer could not make sure that the backup {kept} is there after a power loss: {error.Message} Nothing else was changed.";
                session.Log.Write($"Failed: {message}");
                throw new InstallerException(message, error);
            }

            environment[UpgradeBackupVariable] = kept;
            environment[UpgradeBackupSha256Variable] = (await BackupCommands.ReadAsync(session.Machine, kept, cancellationToken).ConfigureAwait(false)).Sha256;
        }
    }

    /// <summary>The backup the installer before this one took and handed over for the upgrade to <paramref name="target"/>; null when it handed over none.</summary>
    private static (string Path, string Sha256)? HandedOverBackup(InstallerMachine machine, string target)
        => machine.Environment(ContinuedVariable) == target && machine.Environment(UpgradeBackupVariable) is { Length: > 0 } path
            && machine.Environment(UpgradeBackupSha256Variable) is { Length: > 0 } sha256
                ? (path, sha256)
                : null;

    // "the identity store, schema 2; the settings files, none saved".
    private static string DescribeStates(IReadOnlyList<StoreState> states)
        => string.Join("; ", states.Select(state => $"{StateContract.Describe(state.Store)}, " + (state switch
        {
            { Schema: { } schema } => string.Create(CultureInfo.InvariantCulture, $"schema {schema}"),
            { Unreadable: true } => "which only root reads",
            _ => "none saved"
        })));

    /// <summary>
    /// Before a rollback to <paramref name="target"/>: nothing when it reads the stores here (the rollback is a swap). When
    /// it does not, a refusal, unless <paramref name="restoreBackup"/> (<c>--restore-backup</c>, with the backup's file or
    /// empty for the one the record names): then what to put back from that backup, once checked, and agreed to.
    /// </summary>
    private static async Task<StateRestore?> CheckRollbackStateAsync(
        InstallerHost host,
        InstallerSession session,
        InstallRecord record,
        ReleaseManifest target,
        string? restoreBackup,
        bool planOnly,
        bool yes,
        CancellationToken cancellationToken)
    {
        var machine = session.Machine;
        var scope = ScopeFor(machine);
        var layout = ControllerLayout.For(machine);
        var states = InstallRoles.RunsController(record.Roles) ? StateContractGuard.Read(machine, layout) : [];
        var problems = StateContractGuard.Incompatible(states, target);
        if (problems.Count == 0)
        {
            if (restoreBackup is not null)
            {
                host.Out.WriteLine($"Release {target.Version} reads the stores here as they are: nothing is put back from a backup, and the rollback is a swap.");
                host.Out.WriteLine();
            }

            return null;
        }

        var cannot = $"release {target.Version} cannot read the data here: {string.Join("; ", problems)}.";
        var refused = $"Rollback is refused before replacing the controller: {cannot}";
        var canRestore = machine.Os == InstallerOs.Linux && scope == InstallScope.System;
        if (restoreBackup is null)
        {
            session.Log.Write($"Refused: {refused}");
            var given = $"a backup you made before the upgrade ({Installer.CommandName} backup) can be given: sudo {Installer.CommandName} rollback --restore-backup FILE.";
            throw new InstallerRefusedException(
                $"{refused} "
                + (!canRestore
                    ? $"Stay on {record.Version}, or go forward to a release that reads it."
                    : record.UpgradeBackup is not { } backup
                        ? $"No backup from before the upgrade to {record.Version} is recorded here. Stay on {record.Version}, or go forward to a release that reads it; {given}"
                        : !machine.FileExists(backup)
                            ? $"The backup the upgrade to {record.Version} took, {backup}, is not there. Stay on {record.Version}, or go forward to a release that reads it; {given}"
                            : $"The upgrade to {record.Version} backed the stores up first, to {backup}. To go back to {target.Version} with the stores it cannot read as that backup has them, "
                                + "losing every change made to them since the upgrade (people, passwords, PINs, API keys, sessions and settings; access removed since comes back): "
                                + $"sudo {Installer.CommandName} rollback --restore-backup. Or stay on {record.Version}, or go forward to a release that reads it.")
                + " Nothing was changed.");
        }

        if (!canRestore)
        {
            throw new InstallerUsageException($"--restore-backup puts back the controller's stores as root on Linux, where the upgrade backs them up. {refused}");
        }

        var file = restoreBackup.Length > 0 ? Path.GetFullPath(restoreBackup, machine.CurrentDirectory) : record.UpgradeBackup;
        if (file is null)
        {
            throw new InstallerRefusedException(
                $"{refused} No backup from before the upgrade to {record.Version} is recorded here: give one, --restore-backup FILE. Nothing was changed.");
        }

        var restore = await PrepareRestoreAsync(session, layout, record, target, states, file, cancellationToken).ConfigureAwait(false);
        var stores = string.Join(" and ", restore.Stores.Select(StateContract.Describe));
        host.Out.WriteLine(StateContractGuard.Sentence(cannot));
        host.Out.WriteLine(
            $"With --restore-backup, the controller is stopped (after a verified Stop of the roof), and {stores} put back from {file}, "
            + $"the backup taken before the upgrade to {record.Version}. Every change made to {(restore.Stores.Count == 1 ? "it" : "them")} since then is lost: "
            + "people, passwords, PINs, API keys, sessions and settings added or changed, and access removed since comes back. "
            + $"The files it replaces are kept beside them (*.rolled-back-from-{record.Version}-*), only root's.");
        host.Out.WriteLine();
        if (!planOnly && !yes)
        {
            if (!host.IsInteractive || host.Confirm($"Put back {stores} from {file}, losing the changes since the upgrade?") is not { } answer)
            {
                throw new InstallerUsageException("rollback --restore-backup asks before it puts the stores back, and there is no one to ask: give --yes to go ahead without asking.");
            }

            if (!answer)
            {
                throw new InstallerException("Nothing was changed.", InstallerExitCode.Cancelled);
            }
        }

        session.Log.Write($"Rolling back to {target.Version} with {stores} put back from {file}: {string.Join("; ", problems)}.");
        return restore;
    }

    // The backup checked as restore checks one (root's and 0600, whole, as its manifest says), and that it is this
    // machine's, of the release the rollback goes to, with stores that release reads and no commissioning pending; then
    // a step for each file of the stores the release cannot read as they are here.
    private static async Task<StateRestore> PrepareRestoreAsync(
        InstallerSession session,
        ControllerLayout layout,
        InstallRecord record,
        ReleaseManifest target,
        IReadOnlyList<StoreState> states,
        string file,
        CancellationToken cancellationToken)
    {
        var machine = session.Machine;
        var backup = await BackupCommands.ReadAsync(machine, file, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(backup.Manifest.Host, session.Survey.HostName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerRefusedException($"{file} is the backup of {backup.Manifest.Host}, and this machine is {session.Survey.HostName}: a rollback puts back only this machine's own. Nothing was changed.");
        }

        var of = backup.Manifest.RecordVersion ?? backup.Record?.Version;
        if (of != target.Version)
        {
            throw new InstallerRefusedException(
                $"{file} is a backup of {of ?? "no recorded release"}, not of {target.Version}, the release the rollback goes back to: its stores are not the ones {target.Version} left. Nothing was changed.");
        }

        BackupCommands.RefusePendingCommissioning(backup);
        var incompatible = states.Where(state => StateContractGuard.Incompatible([state], target).Count > 0).Select(state => state.Store).ToHashSet(StringComparer.Ordinal);
        var steps = new List<StateRestoreStep>();
        var restored = new List<StoreState>();
        foreach (var (store, files) in StateContractGuard.Files(layout).Where(store => incompatible.Contains(store.Store)))
        {
            var entries = files.Select(path => (Path: path, Entry: backup.Entries.FirstOrDefault(entry => entry.Path == path && entry.Type == BackupEntry.File))).ToList();

            // A file the backup does not hold goes only when its manifest says it was not there: one it left out (a link,
            // or a backup from before it said) may be the store itself, and is never taken for one never saved.
            if (entries.Where(item => item.Entry is null && !(backup.Manifest.Absent ?? []).Contains(item.Path, StringComparer.Ordinal)).Select(item => item.Path).FirstOrDefault() is { } unknown)
            {
                throw new InstallerRefusedException(
                    $"{file} does not hold {unknown}, and does not say it was not there when the backup was made: it cannot put back {StateContract.Describe(store)}. Nothing was changed.");
            }

            var state = StateContractGuard.Read(store, entries.Select(item => (item.Path, item.Entry?.Content is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null, false)));
            if (StateContractGuard.Incompatible([state], target) is [var problem, ..])
            {
                throw new InstallerRefusedException($"{file} does not help: in it, {problem}. Nothing was changed.");
            }

            restored.Add(state);
            steps.AddRange(entries.Select(item => new StateRestoreStep(
                item.Path,
                $"{StateContract.Describe(store)}, put back as the backup from before the upgrade to {record.Version} has it",
                item.Entry?.Content,
                item.Entry?.FileMode ?? Modes.PrivateFile,
                item.Entry?.Owner,
                record.Version)));
        }

        return new StateRestore(file, [.. restored.Select(state => state.Store)], steps, restored);
    }
}
