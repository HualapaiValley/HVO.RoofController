using System.CommandLine;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// <c>hvo-roof-install cert</c>: the controller's HTTPS certificate on its own. With no subcommand it checks the
/// certificate and makes or renews what needs it (the CA, the file's password, the certificate), as the installer does;
/// <c>cert show</c> says what is in place; <c>cert import FILE</c> puts a person's own certificate in place. None of
/// them restarts the controller or moves the roof: it serves a new certificate once it is deployed again.
/// </summary>
internal static class CertificateCommands
{
    private const string Redeploy = "The controller serves it once it is deployed again: when the roof is idle, run the deploy script again (docs/deployment.md).";

    public static Command Create(InstallerHost host)
    {
        var plan = new Option<bool>("--plan") { Description = "Print what it would make or change, and change nothing." };
        var renew = new Option<bool>("--renew") { Description = "Issue the certificate again even though it is still good." };
        var newCa = new Option<bool>("--new-ca")
        {
            Description = "Make a new certificate authority and issue the certificate from it. Every client must then trust the new CA."
        };
        var cert = new Command(
            "cert",
            "Checks the controller's HTTPS certificate and makes or renews what needs it: the CA, the certificate file's password "
            + "and the certificate. It changes only what differs, and never restarts the controller or moves the roof.");
        cert.Options.Add(plan);
        cert.Options.Add(renew);
        cert.Options.Add(newCa);
        cert.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => RenewAsync(host, parseResult.GetValue(plan), parseResult.GetValue(renew), parseResult.GetValue(newCa), token),
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
        var import = new Command(
            "import",
            "Puts your own certificate in place for the controller, with its chain, and records that the controller serves your own. "
            + "It refuses one that has expired, is not yet valid, is a CA's, or is not for a server.");
        import.Arguments.Add(file);
        import.Options.Add(key);
        import.Options.Add(passwordFile);
        import.Options.Add(importPlan);
        import.SetAction((parseResult, token) => Installer.GuardAsync(
            host,
            () => ImportAsync(host, parseResult.GetValue(file)!, parseResult.GetValue(key), parseResult.GetValue(passwordFile), parseResult.GetValue(importPlan), token),
            token));
        cert.Subcommands.Add(import);
        return cert;
    }

    /// <summary>
    /// <c>cert</c>: the controller's certificate, checked and made or renewed: as recorded, or with the defaults when nothing
    /// is recorded yet (a controller the deploy script runs, or one not yet installed).
    /// </summary>
    private static async Task<int> RenewAsync(InstallerHost host, bool planOnly, bool renew, bool newCa, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        var log = planOnly || NeedsRoot(machine) ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time);
        var session = await InstallerSession.StartAsync(machine, log, host.Version, host.Time, cancellationToken).ConfigureAwait(false);
        Installer.WriteWarnings(host, session);
        var record = Recorded(machine, session.Survey)?.Record;
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
        var checkedPlan = await PlanBuilder.BuildCertificate(machine, settings, replacing, renew).CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        var exit = await RunPlanAsync(host, session, checkedPlan, planOnly, "the controller's certificate", settings, cancellationToken).ConfigureAwait(false);
        if (exit == (int)InstallerExitCode.Success && !planOnly && record is null)
        {
            host.Out.WriteLine(
                $"Nothing here is recorded as running the controller yet: when you install it ({Installer.CommandName}), choose {(settings.Connection == ConnectionMode.OwnCertificate ? "own-certificate" : "private-ca")} to keep this certificate.");
        }

        return exit;
    }

    /// <summary><c>cert show</c>: the certificate and CA in place, and what needs doing about them. It only reads.</summary>
    private static int Show(InstallerHost host)
    {
        var machine = host.Machine;
        var recorded = new[] { (InstallScope.System, InstallPaths.SystemRecord), (InstallScope.User, InstallPaths.UserRecord(machine)) }
            .Select(entry => InstallRecord.Load(machine, entry.Item2).Record)
            .FirstOrDefault(record => record is { Controller: not null } && InstallRoles.RunsController(record.Roles));
        var settings = recorded?.Controller;
        var now = host.Time.GetUtcNow();
        var (certificate, authority) = MachineSurveyor.SurveyCertificates(machine, settings);
        foreach (var line in Describe(machine, certificate, authority, recorded, now))
        {
            host.Out.WriteLine(line);
        }

        foreach (var warning in InstallerSession.CertificateWarnings(certificate, authority, now))
        {
            host.Error.WriteLine($"Warning: {warning}");
        }

        return (int)InstallerExitCode.Success;
    }

    /// <summary><c>cert import FILE</c>: the person's own certificate, checked and put in place.</summary>
    private static async Task<int> ImportAsync(InstallerHost host, string file, string? keyFile, string? passwordFile, bool planOnly, CancellationToken cancellationToken)
    {
        var machine = host.Machine;
        var log = planOnly || NeedsRoot(machine) ? InstallLog.None : InstallLog.Open(machine, InstallPaths.Log(machine), host.Time);
        var session = await InstallerSession.StartAsync(machine, log, host.Version, host.Time, cancellationToken).ConfigureAwait(false);
        RefuseWithoutRoot(machine, planOnly, "cert import FILE");

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

        var recorded = Recorded(machine, session.Survey);
        var settings = recorded?.Record.Controller ?? session.DefaultController;
        foreach (var warning in imported.Warnings(CertificateNames.For(machine, settings.Normalised())))
        {
            host.Error.WriteLine($"Warning: {warning}");
        }

        session.Answers = recorded?.Record.ToAnswers() ?? new InstallAnswers();
        log.Write($"Importing {path}: {ImportCertificateStep.Describe(imported)}, SHA-256 {ControllerCertificates.Fingerprint(imported.Certificate)}.");
        var checkedPlan = await PlanBuilder.BuildImport(machine, imported, path, recorded is var (scope, recordPath, _) ? (scope, recordPath) : null)
            .CheckAsync(session.Context, cancellationToken)
            .ConfigureAwait(false);
        var exit = await RunPlanAsync(host, session, checkedPlan, planOnly, "your certificate", settings with { Connection = ConnectionMode.OwnCertificate }, cancellationToken).ConfigureAwait(false);
        if (exit == (int)InstallerExitCode.Success && !planOnly && recorded is null)
        {
            host.Out.WriteLine($"Nothing here is recorded as running the controller yet: when you install it ({Installer.CommandName}), choose own-certificate to serve this one.");
        }

        return exit;
    }

    // The plan's lines; then, unless only planning, the changes and what is in place after them.
    private static async Task<int> RunPlanAsync(InstallerHost host, InstallerSession session, CheckedPlan checkedPlan, bool planOnly, string what, ControllerSettings settings, CancellationToken cancellationToken)
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
            await checkedPlan.ApplyAsync(session.Context, host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException error) when (error is not InstallerRefusedException)
        {
            throw new InstallerException($"It stopped: {error.Message}{Environment.NewLine}Nothing after that step was changed; the log is {session.Log.Path}. Run it again to carry on.", error.ExitCode);
        }

        session.Log.Write($"Put {what} in place.");
        host.Out.WriteLine();
        var (certificate, authority) = MachineSurveyor.SurveyCertificates(session.Machine, settings);
        var record = Recorded(session.Machine, session.Survey)?.Record;
        foreach (var line in Describe(session.Machine, certificate, authority, record, session.Time.GetUtcNow()))
        {
            host.Out.WriteLine(line);
        }

        host.Out.WriteLine();
        host.Out.WriteLine(Redeploy);
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
