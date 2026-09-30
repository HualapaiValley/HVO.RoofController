using System.CommandLine;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// <c>hvo-roof-install cert</c>: the controller's HTTPS certificate on its own. With no subcommand it checks the
/// certificate and makes or renews what needs it (the CA, the file's password, the certificate), as the installer does;
/// <c>cert show</c> says what is in place; <c>cert import FILE</c> puts a person's own certificate in place. The
/// controller serves a new certificate once it is deployed again: for a controller the installer deployed, <c>cert</c>
/// and <c>cert import</c> then redeploy it through the deploy script, when the roof is idle and the person agrees (or
/// gave <c>--redeploy</c>). None of them moves the roof.
/// </summary>
internal static class CertificateCommands
{
    private const string DeployScriptRedeploy = "The controller serves it once it is deployed again: when the roof is idle, run the deploy script again (docs/deployment.md).";

    // Whether to redeploy the controller to serve a new certificate: ask the person, or as the options say.
    private enum RedeployChoice
    {
        Ask,
        Yes,
        No
    }

    public static Command Create(InstallerHost host)
    {
        var plan = new Option<bool>("--plan") { Description = "Print what it would make or change, and change nothing." };
        var renew = new Option<bool>("--renew") { Description = "Issue the certificate again even though it is still good." };
        var newCa = new Option<bool>("--new-ca")
        {
            Description = "Make a new certificate authority and issue the certificate from it. Every client must then trust the new CA."
        };
        var (redeploy, noRedeploy) = RedeployOptions();
        var release = Installer.ReleaseOption("Redeploy the controller from the release");
        var cert = new Command(
            "cert",
            "Checks the controller's HTTPS certificate and makes or renews what needs it: the CA, the certificate file's password "
            + "and the certificate. It changes only what differs. Then, to serve a new certificate, it redeploys the controller "
            + "the installer deployed, once the roof is idle and you agree; it never moves the roof.");
        cert.Options.Add(plan);
        cert.Options.Add(renew);
        cert.Options.Add(newCa);
        cert.Options.Add(redeploy);
        cert.Options.Add(noRedeploy);
        cert.Options.Add(release);
        cert.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => RenewAsync(
                host,
                parseResult.GetValue(plan),
                parseResult.GetValue(renew),
                parseResult.GetValue(newCa),
                Choice(parseResult, redeploy, noRedeploy),
                Installer.Release(host, parseResult.GetValue(release)),
                token),
            token));

        var show = new Command("show", "Shows the controller's certificate and CA: what they are for, until when, and their SHA-256 fingerprints. Changes nothing.");
        show.SetAction((_, token) => Installer.GuardAsync(host, () => Task.FromResult(Show(host)), token));
        cert.Subcommands.Add(show);

        var file = new Argument<string>("FILE")
        {
            Description = "Your certificate: a PKCS#12 file (.pfx, .p12) with its key, or PEM (.crt, .pem) with its chain and, unless --key gives it, its key."
        };
        var key = new Option<string?>("--key") { Description = "The certificate's private key (PEM), when FILE does not hold it.", HelpName = "FILE" };
        var passwordFile = new Option<string?>("--password-file")
        {
            Description = "A file holding the password of FILE or of its key, when it has one. Without it, the installer asks.",
            HelpName = "FILE"
        };
        var importPlan = new Option<bool>("--plan") { Description = "Check the certificate and print what it would change, and change nothing." };
        var (importRedeploy, importNoRedeploy) = RedeployOptions();
        var importRelease = Installer.ReleaseOption("Redeploy the controller from the release");
        var import = new Command(
            "import",
            "Puts your own certificate in place for the controller, with its chain, and records that the controller serves your own. "
            + "It refuses one that has expired, is not yet valid, is a CA's, or is not for a server. Then, to serve it, it redeploys "
            + "the controller the installer deployed, once the roof is idle and you agree.");
        import.Arguments.Add(file);
        import.Options.Add(key);
        import.Options.Add(passwordFile);
        import.Options.Add(importPlan);
        import.Options.Add(importRedeploy);
        import.Options.Add(importNoRedeploy);
        import.Options.Add(importRelease);
        import.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => ImportAsync(
                host,
                parseResult.GetValue(file)!,
                parseResult.GetValue(key),
                parseResult.GetValue(passwordFile),
                parseResult.GetValue(importPlan),
                Choice(parseResult, importRedeploy, importNoRedeploy),
                Installer.Release(host, parseResult.GetValue(importRelease)),
                token),
            token));
        cert.Subcommands.Add(import);
        return cert;
    }

    private static (Option<bool> Redeploy, Option<bool> NoRedeploy) RedeployOptions()
        => (new Option<bool>("--redeploy")
            {
                Description = "Redeploy the controller to serve the new certificate without asking, once the roof is idle; if it moves, change the certificate and stop there."
            },
            new Option<bool>("--no-redeploy") { Description = "Only put the certificate in place: the controller serves it once it is deployed again." });

    // Read inside the installer's guard, so giving both is a usage error as any other.
    private static RedeployChoice Choice(ParseResult parseResult, Option<bool> redeploy, Option<bool> noRedeploy)
        => (parseResult.GetValue(redeploy), parseResult.GetValue(noRedeploy)) switch
        {
            (true, true) => throw new InstallerUsageException("Give --redeploy or --no-redeploy, not both."),
            (true, false) => RedeployChoice.Yes,
            (false, true) => RedeployChoice.No,
            _ => RedeployChoice.Ask
        };

    /// <summary>
    /// <c>cert</c>: the controller's certificate, checked and made or renewed: as recorded, or with the defaults when nothing
    /// is recorded yet (a controller the deploy script runs, or one not yet installed).
    /// </summary>
    private static async Task<int> RenewAsync(InstallerHost host, bool planOnly, bool renew, bool newCa, RedeployChoice redeploy, ReleaseSource release, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        RefuseRootOnMac(machine, "cert");
        var log = planOnly || NeedsRoot(machine) ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time);
        var session = await InstallerSession.StartAsync(machine, log, host.Version, host.Time, release, cancellationToken).ConfigureAwait(false);
        Installer.WriteWarnings(host, session);
        var record = RecordedOrRefuse(machine, session.Survey)?.Record;
        RefuseRedeployUnrecorded(record, redeploy);
        if (record is null && session.Survey.Certificate is { NeedsRoot: true })
        {
            // Whether the certificate in place is the person's own decides what the defaults are: only root can tell.
            throw new InstallerRefusedException(
                $"Nothing here is recorded, and only root can read the certificate in place to tell whether it is yours: run it with sudo, sudo {Installer.CommandName} cert{(planOnly ? " --plan" : string.Empty)}.");
        }

        var settings = record?.Controller ?? session.DefaultController;
        if (settings.Connection == ConnectionMode.Http)
        {
            throw new InstallerRefusedException(
                $"The controller serves plain HTTP, so it has no certificate. To serve HTTPS, run {Installer.CommandName} again and choose private-ca, "
                + $"or put your own in place with {Installer.CommandName} cert import FILE.");
        }

        if (newCa && settings.Connection != ConnectionMode.PrivateCa)
        {
            throw new InstallerUsageException($"--new-ca is for a controller whose certificate the installer's CA issues, and this one's is {ConnectionName(settings.Connection)}.");
        }

        if (renew && settings.Connection == ConnectionMode.OwnCertificate)
        {
            throw new InstallerUsageException($"The controller serves your own certificate, which its issuer renews: put a new one in place with {Installer.CommandName} cert import FILE.");
        }

        RefuseWithoutRoot(machine, planOnly, "cert");
        session.Answers = record?.ToAnswers() ?? new InstallAnswers();
        var replacing = newCa ? session.Survey.Authority?.Fingerprint : null;
        var plan = PlanBuilder.BuildCertificate(machine, settings, replacing, renew);
        var checkedPlan = await plan.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var exit = await RunPlanAsync(host, session, checkedPlan, planOnly, "the controller's certificate", settings, cancellationToken).ConfigureAwait(false);
        if (exit != (int)InstallerExitCode.Success)
        {
            return exit;
        }

        if (record is not null)
        {
            var certificate = plan.Steps.OfType<CertificateStep>().Single();
            var (redeployed, serves) = await RedeployAsync(host, session, record.Roles, settings, certificate, checkedPlan.HasChanges, redeploy, planOnly, cancellationToken).ConfigureAwait(false);
            return serves && record.Roles.Contains(InstallRole.Kiosk)
                ? await KioskAsync(host, session, settings, planOnly, checkedPlan.HasChanges, redeployed, cancellationToken).ConfigureAwait(false)
                : redeployed;
        }

        if (!planOnly)
        {
            WriteDeployScriptRedeploy(host, checkedPlan);
            host.Out.WriteLine(
                $"Nothing here is recorded as running the controller yet: when you install it ({Installer.CommandName}), choose {(settings.Connection == ConnectionMode.OwnCertificate ? "own-certificate" : "private-ca")} to keep this certificate.");
        }

        return exit;
    }

    /// <summary><c>cert show</c>: the certificate and CA in place, and what needs doing about them. It only reads.</summary>
    private static int Show(InstallerHost host)
    {
        var machine = host.Machine;
        RefuseRootOnMac(machine, "cert show");
        var loaded = new[] { InstallPaths.SystemRecord, InstallPaths.UserRecord(machine) }.Select(path => InstallRecord.Load(machine, path)).ToArray();
        var recorded = loaded
            .Select(entry => entry.Record)
            .FirstOrDefault(record => record is { Controller: not null } && InstallRoles.RunsController(record.Roles));
        var settings = recorded?.Controller;
        var now = host.Time.GetUtcNow();
        var (certificate, authority) = MachineSurveyor.SurveyCertificates(machine, settings);
        foreach (var line in Describe(machine, certificate, authority, recorded, now))
        {
            host.Out.WriteLine(line);
        }

        foreach (var problem in loaded.Select(entry => entry.Problem).OfType<string>())
        {
            host.Error.WriteLine($"Warning: {problem}");
        }

        foreach (var warning in InstallerSession.CertificateWarnings(certificate, authority, now, settings?.Connection))
        {
            host.Error.WriteLine($"Warning: {warning}");
        }

        return (int)InstallerExitCode.Success;
    }

    /// <summary><c>cert import FILE</c>: the person's own certificate, checked and put in place.</summary>
    private static async Task<int> ImportAsync(
        InstallerHost host,
        string file,
        string? keyFile,
        string? passwordFile,
        bool planOnly,
        RedeployChoice redeploy,
        ReleaseSource release,
        CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        RefuseRootOnMac(machine, "cert import FILE");
        var log = planOnly || NeedsRoot(machine) ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time);
        var session = await InstallerSession.StartAsync(machine, log, host.Version, host.Time, release, cancellationToken).ConfigureAwait(false);
        RefuseWithoutRoot(machine, planOnly, "cert import FILE");
        var recorded = RecordedOrRefuse(machine, session.Survey);
        RefuseRedeployUnrecorded(recorded?.Record, redeploy);

        var path = Path.GetFullPath(InstallPaths.Expand(machine, file), machine.CurrentDirectory);
        var keyPath = keyFile is null ? null : Path.GetFullPath(InstallPaths.Expand(machine, keyFile), machine.CurrentDirectory);
        var given = passwordFile is null ? null : ReadPassword(machine, Path.GetFullPath(InstallPaths.Expand(machine, passwordFile), machine.CurrentDirectory));
        log.AddSecret(given);
        using var imported = ImportedCertificate.Read(
            path,
            ReadInput(machine, path, "certificate"),
            keyPath,
            keyPath is null ? null : ReadInput(machine, keyPath, "key"),
            what =>
            {
                var typed = given ?? host.ReadSecret(what);
                log.AddSecret(typed);
                return typed;
            });

        var problems = imported.Problems(host.Time.GetUtcNow());
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                log.Write($"Refused {path}: {problem}");
            }

            Installer.WriteRefusal(host, problems);
            return (int)InstallerExitCode.Refused;
        }

        var settings = recorded?.Record.Controller ?? session.DefaultController;
        foreach (var warning in imported.Warnings(CertificateNames.For(machine, settings.Normalised())))
        {
            host.Error.WriteLine($"Warning: {warning}");
        }

        session.Answers = recorded?.Record.ToAnswers() ?? new InstallAnswers();
        log.Write($"Importing {path}: {ImportCertificateStep.Describe(imported)}, SHA-256 {ControllerCertificates.Fingerprint(imported.Certificate)}.");
        var plan = PlanBuilder.BuildImport(machine, imported, path, recorded is var (scope, recordPath, _) ? (scope, recordPath) : null);
        var checkedPlan = await plan.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var own = settings with { Connection = ConnectionMode.OwnCertificate };
        var exit = await RunPlanAsync(host, session, checkedPlan, planOnly, "your certificate", own, cancellationToken).ConfigureAwait(false);
        if (exit != (int)InstallerExitCode.Success)
        {
            return exit;
        }

        if (recorded is var (_, _, record))
        {
            var certificate = plan.Steps.OfType<ImportCertificateStep>().Single();
            var (redeployed, serves) = await RedeployAsync(host, session, record.Roles, own, certificate, checkedPlan.HasChanges, redeploy, planOnly, cancellationToken).ConfigureAwait(false);
            return serves && record.Roles.Contains(InstallRole.Kiosk)
                ? await KioskAsync(host, session, own, planOnly, checkedPlan.HasChanges, redeployed, cancellationToken).ConfigureAwait(false)
                : redeployed;
        }

        if (!planOnly)
        {
            WriteDeployScriptRedeploy(host, checkedPlan);
            host.Out.WriteLine($"Nothing here is recorded as running the controller yet: when you install it ({Installer.CommandName}), choose own-certificate to serve this one.");
        }

        return exit;
    }

    // A controller nothing records (the deploy script runs it, or it is not installed yet) is not the installer's to redeploy.
    private static void RefuseRedeployUnrecorded(InstallRecord? record, RedeployChoice redeploy)
    {
        if (record is null && redeploy == RedeployChoice.Yes)
        {
            throw new InstallerRefusedException(
                "Nothing here is recorded as running the controller, so the installer does not redeploy it (--redeploy). "
                + "Put the certificate in place without --redeploy, then run the deploy script again when the roof is idle (docs/deployment.md).");
        }
    }

    private static void WriteDeployScriptRedeploy(InstallerHost host, CheckedPlan checkedPlan)
    {
        if (checkedPlan.HasChanges)
        {
            host.Out.WriteLine();
            host.Out.WriteLine(DeployScriptRedeploy);
        }
    }

    /// <summary>
    /// The controller the installer deployed, redeployed to serve the certificate <paramref name="certificate"/> put in
    /// place (or would put in place, with <paramref name="planOnly"/>): only when it runs, the roof is idle, the
    /// certificate is all that differs, and the person agrees or gave <c>--redeploy</c>. The deploy script stops the
    /// roof with a verified Stop before it replaces the controller, and puts the old one back when the new one fails a
    /// check. Otherwise it says why not, and how to finish once the roof is idle.
    /// </summary>
    private static async Task<(int Exit, bool Serves)> RedeployAsync(
        InstallerHost host,
        InstallerSession session,
        IReadOnlyCollection<InstallRole> roles,
        ControllerSettings settings,
        IControllerCertificateStep certificate,
        bool changed,
        RedeployChoice choice,
        bool planOnly,
        CancellationToken cancellationToken)
    {
        var machine = session.Machine;
        var sudo = ControllerLayout.For(machine) == ControllerLayout.System ? "sudo " : string.Empty;
        string[] again = session.Release.FolderPath is { } folder ? ["cert", "--redeploy", "--release", folder] : ["cert", "--redeploy"];
        var retry = sudo + new CommandLine(Installer.CommandName, again);
        string[] fromRelease = session.Release.FolderPath is { } from ? ["--release", from] : [];
        var install = sudo + new CommandLine(Installer.CommandName, fromRelease);
        var ok = (int)InstallerExitCode.Success;
        var notDone = choice == RedeployChoice.Yes ? (int)InstallerExitCode.Refused : ok;
        ContainerSurvey? container;
        try
        {
            container = await MachineSurveyor.SurveyContainerAsync(machine, MachineSurveyor.ControllerContainer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !planOnly)
        {
            throw await StoppedAsync(session, retry).ConfigureAwait(false);
        }

        // A paused or restarting one is checked as any other: the controller's step says what to do about it.
        if (container is null || container.State is not ("running" or "paused" or "restarting"))
        {
            if (changed || choice == RedeployChoice.Yes)
            {
                host.Out.WriteLine();
                host.Out.WriteLine(container is null
                    ? $"The controller is not deployed yet: {Installer.CommandName} deploys it, to serve this certificate."
                    : $"The controller is not running ({container.State}): it serves this certificate when it starts again.");
            }

            // With --redeploy, a controller that is not there is one that was not redeployed.
            return (container is null ? notDone : ok, container is not null);
        }

        var plan = PlanBuilder.BuildRedeploy(machine, session.Survey, roles, settings, certificate);
        var controller = plan.Steps.OfType<ControllerStep>().Single();
        CheckedPlan checkedPlan;
        try
        {
            checkedPlan = await plan.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error)
        {
            // Such as a release.json it could not get: the certificate is in place all the same.
            throw NotRedeployed(error, session, retry, error.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !planOnly)
        {
            throw await StoppedAsync(session, retry).ConfigureAwait(false);
        }

        var check = checkedPlan.Steps.Single(step => step.Step == controller).Check;
        if (!checkedPlan.HasChanges && !checkedPlan.IsBlocked)
        {
            // It serves the certificate in place already.
            return (ok, true);
        }

        host.Out.WriteLine();
        if (checkedPlan.Steps.FirstOrDefault(step => step.Check.Change == StepChange.Blocked) is { } blocked)
        {
            host.Out.WriteLine($"The controller is not redeployed to serve the new certificate: {blocked.Check.Detail?.TrimEnd('.')}.");
            host.Out.WriteLine($"The certificate is in place. Once that is dealt with, run {retry} to serve it.");
            session.Log.Write($"Not redeployed: {blocked.Check.Detail}");
            return (planOnly ? ok : notDone, false);
        }

        var more = checkedPlan.Steps.Where(step => step.Check.MakesChange && step.Step != controller).Select(step => $"{step.Step.Target} ({step.Check.Detail})").ToList();
        if (check.MakesChange && !controller.OnlyCertificateDiffers)
        {
            more.Add($"{controller.Target} ({check.Detail})");
        }

        if (more.Count > 0)
        {
            host.Out.WriteLine($"The controller is not redeployed from here, because more than its certificate would change: {string.Join("; ", more)}.");
            host.Out.WriteLine($"Run {install} to redeploy it, with the new certificate.");
            session.Log.Write($"Not redeployed: more than its certificate would change: {string.Join("; ", more)}.");
            return (planOnly ? ok : notDone, false);
        }

        if (!check.MakesChange)
        {
            // Only without root: whether it serves the new certificate needs its keys.
            host.Out.WriteLine($"The controller: {check.Detail}.");
            return (ok, false);
        }

        const string Safety = "The deploy script stops the roof with a verified Stop before it replaces the controller, and puts the old one back if the new one fails a check; nothing here moves the roof.";
        if (planOnly)
        {
            host.Out.WriteLine(choice == RedeployChoice.No
                ? $"The controller is not redeployed (--no-redeploy): it serves the new certificate once you run {retry}, when the roof is idle."
                : $"Then the controller is {check.Detail}{(choice == RedeployChoice.Ask ? ", once the roof is idle and you agree" : ", once the roof is idle")}. {Safety}");
            return (ok, choice != RedeployChoice.No);
        }

        if (choice == RedeployChoice.No)
        {
            host.Out.WriteLine($"The controller is not redeployed (--no-redeploy): it serves the new certificate once it is. When the roof is idle, run {retry}.");
            return (ok, false);
        }

        host.Out.WriteLine($"To serve it, the controller must be deployed again ({check.Detail}). {Safety}");
        if (choice == RedeployChoice.Ask)
        {
            var agreed = host.Confirm("Redeploy the controller now?");
            if (agreed is not true)
            {
                host.Out.WriteLine(agreed is null
                    ? $"No one at a terminal could agree to it, so the controller is not redeployed. When the roof is idle, run {retry}."
                    : $"The controller is not redeployed: it serves the new certificate once it is. When the roof is idle, run {retry}.");
                session.Log.Write(agreed is null ? "Not redeployed: no one at a terminal could agree to it." : "Not redeployed: the person did not agree.");
                return (ok, false);
            }
        }

        host.Out.WriteLine();
        session.Log.Write($"Redeploying {controller.Target}: {check.Detail}.");

        // What the person agreed to: the controller's step refuses to go ahead if, by now, more than its certificate differs.
        controller.CertificateOnly = true;
        try
        {
            // Each step is checked again just before it runs: a roof that started moving stops the redeploy there.
            await checkedPlan.ApplyAsync(session.ContextFor(host.Out.WriteLine), host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error)
        {
            // A redeploy the person asked for that the check just before it refuses (the roof started moving, or more than
            // the certificate changed while they decided) was not done: cert refuses (3), as --redeploy does when it finds
            // the refusal first. When more changed, only the installer shows it all first.
            throw controller.MoreThanCertificateChanged ? NotRedeployed(error, session, install, InstallerExitCode.Refused)
                : error is StepBlockedException ? NotRedeployed(error, session, retry, InstallerExitCode.Refused)
                : NotRedeployed(error, session, retry, error.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw await StoppedAsync(session, retry).ConfigureAwait(false);
        }

        session.Log.Write($"Redeployed {controller.Target} to serve the new certificate.");
        host.Out.WriteLine("The controller serves the new certificate.");
        return (ok, true);
    }

    /// <summary>
    /// The kiosk on this Pi, once the controller serves the certificate in place: its settings trust that one (its pin,
    /// or a new CA), and it starts again to read them. With <paramref name="planOnly"/>, what would happen.
    /// </summary>
    private static async Task<int> KioskAsync(
        InstallerHost host,
        InstallerSession session,
        ControllerSettings settings,
        bool planOnly,
        bool certificateChanges,
        int exit,
        CancellationToken cancellationToken)
    {
        if (planOnly && certificateChanges)
        {
            // Nothing is in place yet to check it against.
            host.Out.WriteLine($"Then the kiosk ({MachineSurveyor.KioskUnit}) trusts the certificate the controller serves, and starts again.");
            return exit;
        }

        var checkedPlan = await PlanBuilder.BuildKioskCertificate(session.Machine, settings).CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        if (!checkedPlan.HasChanges && !checkedPlan.IsBlocked)
        {
            return exit;
        }

        host.Out.WriteLine();
        var kiosk = await RunPlanAsync(host, session, checkedPlan, planOnly, "the kiosk's trust in the controller's certificate", settings, cancellationToken, describe: false).ConfigureAwait(false);
        return kiosk != (int)InstallerExitCode.Success ? kiosk : exit;
    }

    // Ctrl-C before the controller was redeployed (the installer never kills a deploy script that started): what runs
    // now, and how to finish.
    private static async Task<InstallerException> StoppedAsync(InstallerSession session, string retry)
    {
        session.Log.Write("Stopped before the controller was redeployed.");
        return new InstallerException(
            $"Stopped before the controller was redeployed. {await ControllerStep.NowAsync(session.Context).ConfigureAwait(false)}{Environment.NewLine}"
            + $"The certificate is in place{(session.Log.Path is { } log ? $"; the log is {log}" : string.Empty)}. To serve it, run {retry} once the roof is idle.",
            InstallerExitCode.Cancelled);
    }

    private static InstallerException NotRedeployed(InstallerException error, InstallerSession session, string retry, InstallerExitCode exitCode)
        => new(
            $"The controller was not redeployed: {error.Message}{Environment.NewLine}"
            + $"The certificate is in place{(session.Log.Path is { } log ? $"; the log is {log}" : string.Empty)}. To serve it, run {retry} once the roof is idle.",
            exitCode);

    // The plan's lines; then, unless only planning, the changes and what is in place after them.
    private static async Task<int> RunPlanAsync(
        InstallerHost host,
        InstallerSession session,
        CheckedPlan checkedPlan,
        bool planOnly,
        string what,
        ControllerSettings settings,
        CancellationToken cancellationToken,
        bool describe = true)
    {
        var lines = PlanText.Lines(checkedPlan).ToList();
        if (!checkedPlan.IsBlocked && !checkedPlan.HasChanges)
        {
            // Not the installer's "as the answers describe": there are none here.
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
            if (!session.Survey.IsRoot && ControllerLayout.For(session.Machine) == ControllerLayout.System && checkedPlan.HasChanges)
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
        session.Log.Write($"Putting {what} in place: {PlanText.Summary(checkedPlan)}");
        try
        {
            await checkedPlan.ApplyAsync(session.ContextFor(host.Out.WriteLine), host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error) when (error is not InstallerRefusedException)
        {
            throw new InstallerException($"It stopped: {error.Message}{Environment.NewLine}Nothing after that step was changed; the log is {session.Log.Path}. Run it again to carry on.", error.ExitCode);
        }

        session.Log.Write($"Put {what} in place.");
        if (!describe)
        {
            return (int)InstallerExitCode.Success;
        }

        host.Out.WriteLine();
        var (certificate, authority) = MachineSurveyor.SurveyCertificates(session.Machine, settings);
        var record = Recorded(session.Machine, session.Survey)?.Record;
        foreach (var line in Describe(session.Machine, certificate, authority, record, session.Time.GetUtcNow()))
        {
            host.Out.WriteLine(line);
        }

        return (int)InstallerExitCode.Success;
    }

    /// <summary>The certificate and CA in place, a line each detail, as <c>cert show</c> prints them.</summary>
    internal static IReadOnlyList<string> Describe(InstallerMachine machine, CertificateSurvey? certificate, AuthoritySurvey? authority, InstallRecord? record, DateTimeOffset now)
    {
        var layout = ControllerLayout.For(machine);
        var lines = new List<string>();
        if (certificate is null)
        {
            lines.Add($"Certificate: none in {layout.Pfx}");
        }
        else if (certificate.Problem is { } problem)
        {
            lines.Add($"Certificate: {certificate.Path}: {problem}{(certificate.NeedsRoot ? ": run with sudo to see it" : string.Empty)}");
        }
        else
        {
            lines.Add($"Certificate: {certificate.Path}");
            lines.Add($"  Subject:   {certificate.Subject}");
            lines.Add($"  Issued by: {(certificate.Issuer is null ? "itself (self-signed)" : certificate.FromAuthority ? $"{certificate.Issuer}, this machine's CA" : certificate.FromInstallerCa ? $"{certificate.Issuer}, an installer's CA that is not this machine's" : certificate.Issuer)}");
            lines.Add($"  Valid:     until {InstallerSession.Date(certificate.NotAfter)}, {Left(certificate.NotAfter, now)}");
            lines.Add($"  For:       {string.Join(", ", certificate.Names)}");
            if (certificate.Uncovered.Count > 0)
            {
                lines.Add($"  Not for:   {string.Join(", ", certificate.Uncovered)}");
            }

            lines.Add($"  SHA-256:   {certificate.Fingerprint}");
        }

        if (authority is null)
        {
            return lines;
        }

        if (authority.Problem is { } authorityProblem)
        {
            lines.Add($"CA:          {authority.Path}: {authorityProblem}");
            return lines;
        }

        lines.Add($"CA:          {authority.Path}");
        lines.Add($"  Subject:   {authority.Subject}");
        lines.Add($"  Valid:     until {InstallerSession.Date(authority.NotAfter)}, {Left(authority.NotAfter, now)}");
        lines.Add($"  Issues for: {(authority.Permits.Count == 0 ? "any name" : string.Join(", ", authority.Permits))}");
        lines.Add($"  SHA-256:   {authority.Fingerprint}");
        if (record?.Controller is { Connection: ConnectionMode.PrivateCa } controller)
        {
            var host = record.Roles.Contains(InstallRole.Rig) ? "localhost" : CertificateNames.LocalName(machine.HostName);
            lines.Add($"  Clients get it from https://{host}:{controller.ApiPort}/ca.crt, or from {authority.Path}");
        }

        return lines;
    }

    // The record of the controller (or a rig) here, and where it is.
    private static (InstallScope Scope, string Path, InstallRecord Record)? Recorded(InstallerMachine machine, MachineSurvey survey)
    {
        foreach (var (scope, record) in new[] { (InstallScope.System, survey.SystemRecord), (InstallScope.User, survey.UserRecord) })
        {
            if (record is { Controller: not null } && InstallRoles.RunsController(record.Roles))
            {
                return (scope, InstallPaths.RecordFor(scope, machine), record);
            }
        }

        return null;
    }

    // The record of the controller here, refused when one cannot be read: it may say how the controller serves HTTPS, and
    // the defaults could then replace a certificate it says to keep.
    private static (InstallScope Scope, string Path, InstallRecord Record)? RecordedOrRefuse(InstallerMachine machine, MachineSurvey survey)
    {
        // On Linux the machine's record says it; on a Mac, the person's.
        var recorded = Recorded(machine, survey);
        var problems = machine.Os == InstallerOs.MacOS ? survey.RecordProblems : [.. new[] { survey.SystemRecordProblem }.OfType<string>()];
        if (recorded is null && problems.Count > 0)
        {
            throw new InstallerRefusedException(
                $"{string.Join(" ", problems)} Fix it or remove it first: it may say how the controller serves HTTPS.");
        }

        return recorded;
    }

    // A rig on a Mac is the person's (its record and files), as every role there is: never root's.
    private static void RefuseRootOnMac(InstallerMachine machine, string command)
    {
        if (machine.IsRoot && machine.Os == InstallerOs.MacOS)
        {
            throw new InstallerRefusedException($"On a Mac the rig's files are yours: run it without sudo, {Installer.CommandName} {command}.");
        }
    }

    // The controller's files on Linux are root's: only a plan runs without it, and a run refused for it logs nothing.
    private static bool NeedsRoot(InstallerMachine machine) => !machine.IsRoot && ControllerLayout.For(machine) == ControllerLayout.System;

    private static void RefuseWithoutRoot(InstallerMachine machine, bool planOnly, string command)
    {
        if (!planOnly && NeedsRoot(machine))
        {
            throw new InstallerRefusedException($"The controller's certificate files are root's: run it with sudo, sudo {Installer.CommandName} {command}. To see what it would do first, add --plan.");
        }
    }

    private static byte[] ReadInput(InstallerMachine machine, string path, string what)
    {
        try
        {
            return machine.ReadBytes(path) ?? throw new InstallerUsageException($"There is no {what} file {path}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InstallerUsageException($"The {what} file {path} could not be read: {error.Message}");
        }
    }

    // The whole file but a trailing line break, as `echo … > file` leaves one.
    private static string ReadPassword(InstallerMachine machine, string path)
    {
        string? text;
        try
        {
            text = machine.ReadText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InstallerUsageException($"The password file {path} could not be read: {error.Message}");
        }

        return (text ?? throw new InstallerUsageException($"There is no password file {path}.")).TrimEnd('\r', '\n');
    }

    private static string Left(DateTimeOffset? notAfter, DateTimeOffset now)
    {
        if (notAfter is not { } expires)
        {
            return "?";
        }

        var days = (int)Math.Floor((expires - now).TotalDays);
        return expires <= now ? $"expired {-days} days ago" : days == 1 ? "1 day left" : $"{days} days left";
    }

    private static string ConnectionName(ConnectionMode mode) => mode switch
    {
        ConnectionMode.SelfSigned => "self-signed",
        ConnectionMode.OwnCertificate => "your own",
        _ => mode.ToString()
    };
}
