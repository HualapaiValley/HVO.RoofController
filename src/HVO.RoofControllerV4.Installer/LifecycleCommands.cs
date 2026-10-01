using System.CommandLine;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// What the installer does after an install, with the record of it: <c>upgrade</c> to a newer release (through that
/// release's own installer), <c>rollback</c> to the release before, and <c>uninstall</c>. As root they act on the
/// machine's roles (the controller, a rig, the kiosk); for a person, on their own (hvo-roof, the Mac app, a rig on a
/// Mac). The controller is only ever replaced or stopped by the deploy script, after a verified Stop of the roof: none of
/// them moves the roof.
/// </summary>
internal static class LifecycleCommands
{
    /// <summary>
    /// Set by <c>upgrade</c> for the new release's installer it hands over to, to the version it hands over for: an
    /// installer that is not that version stops, so the two never hand over to each other again and again.
    /// </summary>
    public const string ContinuedVariable = "HVO_ROOF_INSTALL_CONTINUED";

    public static IEnumerable<Command> Create(InstallerHost host)
    {
        yield return Upgrade(host);
        yield return Rollback(host);
        yield return Uninstall(host);
    }

    private static Command Upgrade(InstallerHost host)
    {
        var version = new Option<string?>("--version")
        {
            Description = "The release to upgrade to, as 4.0.1. Without it, the latest release (or the one in --release).",
            HelpName = "VERSION"
        };
        var plan = new Option<bool>("--plan") { Description = "Print what the upgrade would change, and change nothing." };
        var release = Installer.ReleaseOption("Read the release");
        var upgrade = new Command(
            "upgrade",
            "Upgrades what is installed here to a newer release, with the choices recorded when it was installed. The new release's "
            + "installer takes over: it replaces this one, then checks and deploys the controller through the deploy script (a "
            + "verified Stop of the roof first, the old controller kept for a rollback) and replaces the kiosk, hvo-roof and the "
            + "Mac app, keeping the ones they replace.");
        upgrade.Options.Add(version);
        upgrade.Options.Add(plan);
        upgrade.Options.Add(release);
        upgrade.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => UpgradeAsync(host, parseResult.GetValue(version), parseResult.GetValue(release), parseResult.GetValue(plan), token),
            token));
        return upgrade;
    }

    private static Command Rollback(InstallerHost host)
    {
        var plan = new Option<bool>("--plan") { Description = "Print what the rollback would change, and change nothing." };
        var release = Installer.ReleaseOption("Read the release before");
        var rollback = new Command(
            "rollback",
            "Goes back to the release installed before the last upgrade: the deploy script swaps the controller with the one it kept "
            + "(after a verified Stop of the roof), and the kiosk, hvo-roof, the Mac app and the installer go back to the ones kept "
            + "beside them. It goes back one release, once.");
        rollback.Options.Add(plan);
        rollback.Options.Add(release);
        rollback.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => RollbackAsync(host, parseResult.GetValue(release), parseResult.GetValue(plan), token),
            token));
        return rollback;
    }

    private static Command Uninstall(InstallerHost host)
    {
        var plan = new Option<bool>("--plan") { Description = "Print what it would remove, and remove nothing." };
        var purge = new Option<bool>("--purge")
        {
            Description = "Remove the data too: the controller's secrets, certificate, CA, people and settings, the kiosk's device key, and the record. It cannot be undone."
        };
        var backup = new Option<string?>("--backup")
        {
            Description = "With --purge: back up to this file first (hvo-roof-install backup --output FILE).",
            HelpName = "FILE"
        };
        var noBackup = new Option<bool>("--no-backup") { Description = "With --purge: remove the data without backing it up first." };
        var confirm = new Option<string?>("--confirm")
        {
            Description = "With --purge: this machine's name, in place of typing it when asked, to say the data here is to go.",
            HelpName = "HOST"
        };
        var yes = new Option<bool>("--yes") { Description = "Remove what the plan lists without asking." };
        var uninstall = new Command(
            "uninstall",
            "Removes what the installer installed here: the controller (stopped by the deploy script after a verified Stop of the "
            + "roof), the one kept for a rollback, a rig's HAT emulator, their images, the kiosk's service and program, hvo-roof, "
            + "the Mac app and the installer. The data stays for a reinstall, unless --purge.");
        uninstall.Options.Add(plan);
        uninstall.Options.Add(purge);
        uninstall.Options.Add(backup);
        uninstall.Options.Add(noBackup);
        uninstall.Options.Add(confirm);
        uninstall.Options.Add(yes);
        uninstall.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => UninstallAsync(
                host,
                new UninstallOptions(
                    parseResult.GetValue(plan),
                    parseResult.GetValue(purge),
                    parseResult.GetValue(backup),
                    parseResult.GetValue(noBackup),
                    parseResult.GetValue(confirm),
                    parseResult.GetValue(yes)),
                token),
            token));
        return uninstall;
    }

    /// <summary>
    /// <c>upgrade</c>. Run by an installer of another release, it puts the new release's installer in place and hands over
    /// to it; run by the new release's (or with that release already the latest), it installs what is recorded, as
    /// <c>--answers</c> installs an answers file. Going back is <c>rollback</c>'s: an older release is refused.
    /// </summary>
    private static async Task<int> UpgradeAsync(InstallerHost host, string? version, string? releaseFolder, bool planOnly, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        CertificateCommands.RefuseRootOnMac(machine, "upgrade");
        if (version is not null && !RoofSemVer.IsValid(version))
        {
            throw new InstallerUsageException($"--version {version} is not a release's version: give one as 4.0.1.");
        }

        var release = Installer.Release(host, releaseFolder);
        var scope = ScopeFor(machine);
        var session = await StartAsync(host, scope, planOnly, release, cancellationToken).ConfigureAwait(false);
        var record = Installed(host, session.Survey, scope, "upgrade", planOnly);
        var target = version ?? await release.LatestVersionAsync(machine, cancellationToken).ConfigureAwait(false);
        if (RoofSemVer.IsOlder(target, record.Version))
        {
            throw new InstallerRefusedException(
                $"{record.Version} is installed here, newer than {target}: an upgrade never goes back. To go back to the release before, {Sudo(scope)}{Installer.CommandName} rollback.");
        }

        var options = new Installer.RunOptions(null, planOnly, releaseFolder);
        var answers = record.ToAnswers();
        if (answers.Missing().FirstOrDefault() is { } missing)
        {
            throw new InstallerRefusedException($"{missing} The record of what is installed here leaves it out: run {Sudo(scope)}{Installer.CommandName} to choose it, then upgrade.");
        }

        if (target == session.Version)
        {
            // This installer is the release's: it installs what is recorded.
            if (record.Version == target)
            {
                host.Out.WriteLine($"{target} is installed here: checking that everything is as it should be.");
            }

            return planOnly
                ? await Installer.PlanAsync(host, session, answers, options, cancellationToken).ConfigureAwait(false)
                : await Installer.InstallAsync(host, session, answers, $"Upgrading to {target}", options, cancellationToken).ConfigureAwait(false);
        }

        var installer = InstallPaths.Installer(scope, machine);
        if (machine.Environment(ContinuedVariable) == target)
        {
            throw new InstallerException(
                $"{installer} was put in place as release {target}'s installer, but it is {session.Version}: download release {target}'s installer from {ReleaseManifest.PageUri(target)} and run it.");
        }

        host.Out.WriteLine(record.Version == target ? $"{target} is installed here: its installer checks it." : $"{record.Version} is installed here; {target} replaces it.");
        session.Version = target;
        var manifest = await session.Context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        // Each release's notes from the one installed to this one, oldest first: a release candidate's are its release's.
        var core = target.Split('-', '+')[0];
        var notes = manifest.UpgradeNotes
            .Where(note => record.Version != target && RoofSemVer.IsOlder(record.Version, note.Version) && !RoofSemVer.IsOlder(core, note.Version))
            .OrderBy(note => note.Version, Comparer<string>.Create(RoofSemVer.Compare));
        foreach (var note in notes)
        {
            host.Out.WriteLine();
            host.Out.WriteLine($"Upgrade notes for {note.Version}:");
            foreach (var line in note.Text.Split('\n'))
            {
                host.Out.WriteLine($"  {line.TrimEnd('\r')}");
            }
        }

        host.Out.WriteLine($"Release {target}: {ReleaseManifest.PageUri(target)}");
        host.Out.WriteLine();
        if (planOnly)
        {
            // The new installer is not put in place for a plan: this one plans the new release as it would.
            host.Out.WriteLine($"Release {target}'s installer makes the upgrade. As this installer ({InstallerSession.RoofVersion(host.Version)}) sees it:");
            return await Installer.PlanAsync(host, session, answers, options, cancellationToken).ConfigureAwait(false);
        }

        var step = new InstallerProgramStep(scope, machine);
        var checkedPlan = await new InstallPlan([step]).CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var check = checkedPlan.Steps[0].Check;
        if (check.Change is StepChange.Info or StepChange.Blocked)
        {
            throw new InstallerRefusedException($"{installer}: {check.Detail}. Download release {target}'s installer from {ReleaseManifest.PageUri(target)} and run its upgrade.");
        }

        if (check.MakesChange)
        {
            host.Out.WriteLine($"{installer}: {check.Detail}.");
            await checkedPlan.ApplyAsync(session.ContextFor(host.Out.WriteLine), host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<string> { "upgrade", "--version", target };
        if (release.FolderPath is { } folder)
        {
            arguments.AddRange(["--release", folder]);
        }

        session.Log.Write($"Handing over to release {target}'s installer: {installer} {string.Join(' ', arguments)}.");
        host.Out.WriteLine($"Handing over to release {target}'s installer.");
        host.Out.WriteLine();
        return await machine.RunProgramAsync(machine.OnDisk(installer), arguments, new Dictionary<string, string> { [ContinuedVariable] = target }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>rollback</c>: back to the release the record says was installed before the last upgrade, with that release's
    /// files and the ones each upgrade kept. The record then says what it went back from, and there is nothing further back.
    /// </summary>
    private static async Task<int> RollbackAsync(InstallerHost host, string? releaseFolder, bool planOnly, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        CertificateCommands.RefuseRootOnMac(machine, "rollback");
        var release = Installer.Release(host, releaseFolder);
        var scope = ScopeFor(machine);
        var session = await StartAsync(host, scope, planOnly, release, cancellationToken).ConfigureAwait(false);
        var record = Installed(host, session.Survey, scope, "rollback", planOnly);
        if (record.PreviousVersion is not { } previous)
        {
            if (record.RolledBackFrom is { } from)
            {
                host.Out.WriteLine($"{record.Version} is installed here: rolled back from {from} already. A rollback goes back one release, once; to go forward again, {Sudo(scope)}{Installer.CommandName} upgrade.");
                return (int)InstallerExitCode.Success;
            }

            throw new InstallerRefusedException($"{record.Version} is the first release installed here: there is no release before it to go back to.");
        }

        host.Out.WriteLine($"{record.Version} is installed here; going back to {previous}, the release before.");
        host.Out.WriteLine();
        session.Version = previous;
        var plan = PlanBuilder.BuildRollback(machine, session.Survey, record, record.Version);
        var checkedPlan = await plan.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        return await RunPlanAsync(host, session, checkedPlan, planOnly, $"release {previous}", "rolling back", cancellationToken).ConfigureAwait(false);
    }

    private sealed record UninstallOptions(bool PlanOnly, bool Purge, string? Backup, bool NoBackup, string? Confirm, bool Yes);

    /// <summary>
    /// <c>uninstall</c>: what the record says is installed here, removed, once the person agrees (or gave <c>--yes</c>).
    /// With <c>--purge</c> the data goes too, after a backup (or <c>--no-backup</c>) and the machine's name typed (or given).
    /// </summary>
    private static async Task<int> UninstallAsync(InstallerHost host, UninstallOptions options, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        CertificateCommands.RefuseRootOnMac(machine, "uninstall");
        if (!options.Purge && (options.Backup is not null || options.NoBackup || options.Confirm is not null))
        {
            throw new InstallerUsageException("--backup, --no-backup and --confirm go with --purge.");
        }

        if (options.Backup is not null && options.NoBackup)
        {
            throw new InstallerUsageException("Give --backup FILE or --no-backup, not both.");
        }

        var scope = ScopeFor(machine);
        if (options.Backup is not null && scope != InstallScope.System)
        {
            throw new InstallerUsageException($"A backup is of the controller and the kiosk, as root on Linux: back up yourself what is yours, then {Installer.CommandName} uninstall --purge --no-backup.");
        }

        var session = await StartAsync(host, scope, options.PlanOnly, ReleaseSource.GitHub(), cancellationToken).ConfigureAwait(false);
        var record = Installed(host, session.Survey, scope, "uninstall", options.PlanOnly, uninstalled: true);
        if (record.UninstalledAt is not null && !options.Purge)
        {
            host.Out.WriteLine($"It was uninstalled here on {record.UninstalledAt:yyyy-MM-dd}; its data is kept. To remove that too, {Sudo(scope)}{Installer.CommandName} uninstall --purge.");
            return (int)InstallerExitCode.Success;
        }

        var plan = PlanBuilder.BuildUninstall(machine, session.Survey, record, options.Purge);
        RefusePurgeUnderController(machine, session.Survey, record, plan, options.Purge);
        var checkedPlan = await plan.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var what = record.Roles.Count > 0 ? InstallRoles.Describe(record.Roles) : "what was installed";
        host.Out.WriteLine(options.Purge
            ? $"Uninstalling {what} from {session.Survey.HostName}, and removing its data:"
            : $"Uninstalling {what} from {session.Survey.HostName}; its data is kept:");
        host.Out.WriteLine();
        if (options.PlanOnly || checkedPlan.IsBlocked || !checkedPlan.HasChanges)
        {
            return await RunPlanAsync(host, session, checkedPlan, options.PlanOnly, "nothing installed", "uninstalling", cancellationToken).ConfigureAwait(false);
        }

        foreach (var line in PlanText.Lines(checkedPlan))
        {
            host.Out.WriteLine(line);
        }

        host.Out.WriteLine();
        if (!options.Yes && !Agrees(host, options.Purge ? "Remove all of this, data included? It cannot be undone." : "Remove these?", "uninstall"))
        {
            host.Error.WriteLine("Nothing was removed.");
            return (int)InstallerExitCode.Cancelled;
        }

        if (options.Purge)
        {
            await PurgeChecksAsync(host, session, scope, options, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyAsync(host, session, checkedPlan, "uninstalling", cancellationToken).ConfigureAwait(false);
    }

    // A purge removes the controller's files. When this uninstall does not stop the controller (it was uninstalled before,
    // and deployed again since, by hand or with Compose), they would go from under one that may be running the roof.
    private static void RefusePurgeUnderController(InstallerMachine machine, MachineSurvey survey, InstallRecord record, InstallPlan plan, bool purge)
    {
        if (!purge || plan.Steps.OfType<ControllerStopStep>().Any() || survey.Controller is not { } deployed)
        {
            return;
        }

        var layout = ControllerLayout.For(machine);
        if (PlanBuilder.PurgedFolders(machine, record, layout).Any(purged => purged.Folder == layout.Configuration || purged.Folder == layout.Data))
        {
            throw new InstallerRefusedException(
                $"A controller ({MachineSurveyor.ControllerContainer}, {deployed.State}) is deployed here, and this uninstall does not stop it: --purge would remove its files from under it. "
                + "Remove it with the deploy script first (it stops the roof), then purge. Nothing was removed.");
        }
    }

    // Before the data goes: a backup (or the person saying none), and the machine's name, so data is not removed from the
    // wrong machine over SSH.
    private static async Task PurgeChecksAsync(InstallerHost host, InstallerSession session, InstallScope scope, UninstallOptions options, CancellationToken cancellationToken)
    {
        var name = session.Survey.HostName;
        var given = options.Confirm;
        if (given is null)
        {
            if (!host.IsInteractive || host.Ask($"Type this machine's name, {name}, to remove its data:") is not { } typed)
            {
                throw new InstallerUsageException($"--purge needs this machine's name, typed when asked or given: --confirm {name}.");
            }

            given = typed;
        }

        if (!IsHostName(given, name))
        {
            throw new InstallerRefusedException($"{given} is not this machine's name ({name}): nothing was removed.");
        }

        var backup = options.Backup;
        if (backup is null && !options.NoBackup && scope == InstallScope.System)
        {
            var offer = host.IsInteractive ? host.Confirm($"Back up the data first, to {BackupCommands.DefaultFolder}?") : null;
            if (offer is null)
            {
                throw new InstallerUsageException("--purge removes the data: back it up first with --backup FILE, or give --no-backup.");
            }

            if (offer == true)
            {
                backup = string.Empty;
            }
        }

        if (backup is not null)
        {
            var written = await BackupCommands.WriteAsync(host, session, backup.Length == 0 ? null : backup, cancellationToken).ConfigureAwait(false);
            host.Out.WriteLine();
            session.Log.Write($"Backed up to {written} before removing the data.");
        }
    }

    // Short or full: roofcontrol and roofcontrol.lan are both this machine.
    private static bool IsHostName(string given, string name)
    {
        given = given.Trim();
        var shortName = name.Split('.')[0];
        return string.Equals(given, name, StringComparison.OrdinalIgnoreCase) || string.Equals(given, shortName, StringComparison.OrdinalIgnoreCase);
    }

    // Yes from the person at the terminal; without one, a usage error that names --yes.
    private static bool Agrees(InstallerHost host, string question, string command)
    {
        if (!host.IsInteractive || host.Confirm(question) is not { } answer)
        {
            throw new InstallerUsageException($"{command} asks before it removes anything, and there is no one to ask: give --yes to go ahead without asking.");
        }

        return answer;
    }

    /// <summary>
    /// The scope these commands act on: as root the machine's; otherwise the person's when they have a record (always on a
    /// Mac), else the machine's, which then needs root for anything but a plan.
    /// </summary>
    internal static InstallScope ScopeFor(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (machine.IsRoot)
        {
            return InstallScope.System;
        }

        return machine.Os == InstallerOs.MacOS || machine.FileExists(InstallPaths.UserRecord(machine)) ? InstallScope.User : InstallScope.System;
    }

    // Logged unless a plan, or refused for want of root (which logs nothing).
    private static Task<InstallerSession> StartAsync(InstallerHost host, InstallScope scope, bool planOnly, ReleaseSource release, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        var log = planOnly || (scope == InstallScope.System && !machine.IsRoot) ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time);
        return StartAndWarnAsync(host, log, release, cancellationToken);
    }

    private static async Task<InstallerSession> StartAndWarnAsync(InstallerHost host, InstallLog log, ReleaseSource release, CancellationToken cancellationToken)
    {
        var session = await InstallerSession.StartAsync(host.Machine, log, host.Version, host.Time, release, cancellationToken).ConfigureAwait(false);
        Installer.WriteWarnings(host, session);
        return session;
    }

    /// <summary>
    /// The record in <paramref name="scope"/>, refused when there is none, it cannot be read, or nothing is installed (an
    /// uninstalled one is taken when <paramref name="uninstalled"/>), and, for the machine's, without root unless planning.
    /// </summary>
    private static InstallRecord Installed(InstallerHost host, MachineSurvey survey, InstallScope scope, string command, bool planOnly, bool uninstalled = false)
    {
        var record = scope == InstallScope.System ? survey.SystemRecord : survey.UserRecord;
        var problem = scope == InstallScope.System ? survey.SystemRecordProblem : survey.RecordProblems.FirstOrDefault();
        if (record is null && problem is not null)
        {
            throw new InstallerRefusedException($"{problem} Fix it or remove it first: it says what is installed here.");
        }

        var installed = record is { UninstalledAt: null, Roles.Count: > 0 };
        if (record is null || (!installed && !(uninstalled && record.UninstalledAt is not null)))
        {
            var where = scope == InstallScope.User && host.Machine.Os != InstallerOs.MacOS ? " for you (as root it acts on the machine's)" : string.Empty;
            throw new InstallerRefusedException($"Nothing is recorded as installed here{where}: install with {Sudo(scope)}{Installer.CommandName}.");
        }

        if (!planOnly && scope == InstallScope.System && !host.Machine.IsRoot)
        {
            throw new InstallerRefusedException(
                $"What the installer installed here is root's: run it with sudo, sudo {Installer.CommandName} {command}. To see what it would do first, add --plan.");
        }

        return record;
    }

    private static string Sudo(InstallScope scope) => scope == InstallScope.System ? "sudo " : string.Empty;

    // The plan's lines; then, unless only planning, the changes.
    private static async Task<int> RunPlanAsync(
        InstallerHost host,
        InstallerSession session,
        CheckedPlan checkedPlan,
        bool planOnly,
        string what,
        string doing,
        CancellationToken cancellationToken)
    {
        var lines = PlanText.Lines(checkedPlan).ToList();
        if (!checkedPlan.IsBlocked && !checkedPlan.HasChanges)
        {
            lines[^1] = $"Nothing to change: {what} is in place as it should be.";
        }

        foreach (var line in lines)
        {
            host.Out.WriteLine(line);
        }

        if (checkedPlan.IsBlocked)
        {
            session.Log.Write($"Refused: {PlanText.Summary(checkedPlan)}");
            return (int)InstallerExitCode.Refused;
        }

        if (planOnly)
        {
            if (!session.Survey.IsRoot && ScopeFor(session.Machine) == InstallScope.System && checkedPlan.HasChanges)
            {
                host.Out.WriteLine("Making these changes needs root: run it with sudo.");
            }

            return (int)InstallerExitCode.Success;
        }

        if (!checkedPlan.HasChanges)
        {
            return (int)InstallerExitCode.Success;
        }

        host.Out.WriteLine();
        return await ApplyAsync(host, session, checkedPlan, doing, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ApplyAsync(InstallerHost host, InstallerSession session, CheckedPlan checkedPlan, string doing, CancellationToken cancellationToken)
    {
        session.Log.Write($"{char.ToUpperInvariant(doing[0])}{doing[1..]}: {PlanText.Summary(checkedPlan)}");
        try
        {
            await checkedPlan.ApplyAsync(session.ContextFor(host.Out.WriteLine), host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error) when (error is not InstallerRefusedException)
        {
            throw new InstallerException(
                $"It stopped {doing}: {error.Message}{Environment.NewLine}Nothing after that step was changed; the log is {session.Log.Path}. Run it again to carry on.",
                error.ExitCode);
        }

        session.Log.Write($"Done {doing}.");
        host.Out.WriteLine();
        host.Out.WriteLine($"Done {doing}.");
        return (int)InstallerExitCode.Success;
    }
}
