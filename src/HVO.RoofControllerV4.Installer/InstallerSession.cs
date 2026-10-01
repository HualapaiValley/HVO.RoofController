using System.Globalization;
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
/// One run of the installer: the machine as the survey found it, the answers so far, and the log. The wizard and an
/// answers file drive the same session, so both check, plan and install the same way.
/// </summary>
public sealed class InstallerSession
{
    /// <summary>The file the wizard offers to save the answers to, in the folder the installer was started in.</summary>
    public const string AnswersFileName = "hvo-roof-answers.json";

    private InstallerSession(InstallerMachine machine, InstallLog log, string version, MachineSurvey survey, TimeProvider time, ReleaseSource release)
    {
        Machine = machine;
        Log = log;
        Version = version;
        Survey = survey;
        Time = time;
        Release = release;
        Answers = RecordedAnswers(survey, includeSystem: survey.IsRoot) ?? new InstallAnswers();
    }

    /// <summary>
    /// Looks at the machine (every command logged) and starts a session there, with the answers of what was installed
    /// before (<see cref="RecordedAnswers"/>) as the starting point. The release comes from <paramref name="release"/>,
    /// GitHub unless it says otherwise.
    /// </summary>
    public static async Task<InstallerSession> StartAsync(
        InstallerMachine machine,
        InstallLog log,
        string version,
        TimeProvider time,
        ReleaseSource? release = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        var logged = machine.WithCommands(new LoggingCommandRunner(machine.Commands, log));
        log.Write($"hvo-roof-install {version} on {machine.HostName} ({machine.RuntimeIdentifier}), as {machine.UserName}.");
        var survey = await MachineSurveyor.SurveyAsync(logged, cancellationToken).ConfigureAwait(false);
        foreach (var line in DescribeSurvey(survey, time.GetUtcNow()))
        {
            log.Write(line);
        }

        return new InstallerSession(logged, log, RoofVersion(version), survey, time, release ?? ReleaseSource.GitHub());
    }

    public InstallerMachine Machine { get; }

    public InstallLog Log { get; }

    /// <summary>The release this installer installs (its version without the commit); <c>rollback</c> sets the one it goes back to.</summary>
    public string Version { get; internal set; }

    public MachineSurvey Survey { get; }

    public TimeProvider Time { get; }

    /// <summary>Where the release's release.json comes from.</summary>
    public ReleaseSource Release { get; }

    /// <summary>The answers so far: the roles and choices.</summary>
    public InstallAnswers Answers { get; set; }

    /// <summary>The passwords and PINs given so far (<see cref="GiveSecret"/>): never saved, logged or shown.</summary>
    public InstallSecrets Secrets { get; } = new();

    /// <summary>
    /// Keeps a password or PIN for this run, and keeps it out of the log. Throws <see cref="InstallerUsageException"/>
    /// when it is not one the step takes.
    /// </summary>
    public void GiveSecret(InstallSecret secret, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (InstallSecrets.Problem(secret, value) is { } problem)
        {
            throw new InstallerUsageException(problem);
        }

        Log.AddSecret(value);
        Secrets.Set(secret, value);
    }

    /// <summary>The secrets <paramref name="plan"/> needs that were not given yet.</summary>
    public IReadOnlyList<InstallSecret> MissingSecrets(CheckedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Asked for in one order, the admin's before the camera's, whatever the order of the steps that need them.
        return [.. plan.NeededSecrets().Where(secret => !Secrets.Has(secret)).Distinct().Order()];
    }

    public IReadOnlyList<RoleOption> Options => RoleGuards.Options(Survey);

    /// <summary>
    /// The controller's choices when nothing is recorded: the person's own certificate when one is in place that this
    /// machine's CA did not issue (so it is never replaced unasked, even when an installer's CA elsewhere issued it),
    /// otherwise a private CA (in place of a self-signed one, or one that cannot be opened). No domain is listed unasked:
    /// each one lets the CA sign for any name in it, so the wizard only suggests those this machine seems to be in. A
    /// controller already running keeps its telemetry, a rig's emulator its pace, and a rig published on every address stays
    /// open to the network.
    /// </summary>
    public ControllerSettings DefaultController => new()
    {
        Connection = Survey.Certificate is { Problem: null, IsTheirs: true } ? ConnectionMode.OwnCertificate : ConnectionMode.PrivateCa,
        TelemetryEndpoint = Survey.Controller?.TelemetryEndpoint is { Length: > 0 } endpoint ? endpoint : null,
        Rig = new RigSettings
        {
            TimeScale = Number(Survey.HatEmulator?.EmulatorTimeScale) ?? RigSettings.DefaultTimeScale,
            CameraFramesPerSecond = Number(Survey.HatEmulator?.EmulatorCameraFramesPerSecond) ?? RigSettings.DefaultCameraFramesPerSecond,
            OpenToLan = Survey.Controller is { HatEmulator: not null, PublishAddress: null, PublishedPorts.Count: > 0 }
        }
    };

    private static double? Number(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>
    /// Why the installer refuses the answers here; empty when it may go ahead. Without <paramref name="clientChosen"/>,
    /// the client's controller is not checked (<see cref="RoleGuards.Check"/>).
    /// </summary>
    public IReadOnlyList<string> Problems(bool planOnly = false, bool clientChosen = true)
        => [.. VersionProblems(), .. RoleGuards.Check(Survey, Answers, planOnly, clientChosen)];

    /// <summary>The record of this run's roles (the machine's as root, else the person's): what an upgrade starts from.</summary>
    public InstallRecord? Record => Survey.IsRoot ? Survey.SystemRecord : Survey.UserRecord;

    /// <summary>
    /// Why this installer will not install over what the record says: a newer release is installed. Going back is
    /// <c>rollback</c>'s, which knows what the release before left; an older installer does not.
    /// </summary>
    public IReadOnlyList<string> VersionProblems()
    {
        if (Record is not { } record || !RoofSemVer.IsValid(record.Version) || !RoofSemVer.IsValid(Version) || !RoofSemVer.IsOlder(Version, record.Version))
        {
            return [];
        }

        var sudo = Survey.IsRoot ? "sudo " : string.Empty;
        return [record.UninstalledAt is null
            ? $"{record.Version} is installed here, newer than this installer ({Version}). To go back to the release before it: {sudo}hvo-roof-install rollback"
            : $"{record.Version} was uninstalled here, keeping its data, which is newer than this installer ({Version}). Install {record.Version} or newer, or remove the data first: {sudo}hvo-roof-install uninstall --purge"];
    }

    /// <summary>"Upgrading from 4.0.0 to 4.0.1." when the record has an older release installed; null otherwise.</summary>
    public string? UpgradeLine()
        => Record is { UninstalledAt: null } record && RoofSemVer.IsValid(record.Version) && RoofSemVer.IsValid(Version) && RoofSemVer.IsOlder(record.Version, Version)
            ? $"Upgrading from {record.Version} to {Version}."
            : null;

    public InstallContext Context => ContextFor(null);

    /// <summary>A context whose long steps (a deploy) say how they are getting on through <paramref name="progress"/>.</summary>
    public InstallContext ContextFor(Action<string>? progress) => new()
    {
        Machine = Machine,
        Log = Log,
        Survey = Survey,
        Answers = Answers.Normalised(),
        Version = Version,
        Time = Time,
        Release = Release,
        Secrets = Secrets,
        Progress = progress
    };

    /// <summary>What the answers make and change here: it only looks.</summary>
    public Task<CheckedPlan> CheckAsync(CancellationToken cancellationToken = default)
        => PlanBuilder.Build(Machine, Survey, Answers).CheckAsync(Context, cancellationToken);

    /// <summary>Installs <paramref name="plan"/>: each step checked again as it comes, and logged.</summary>
    public async Task ApplyAsync(CheckedPlan plan, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = Problems();
        if (problems.Count > 0)
        {
            throw new InstallerRefusedException(string.Join(Environment.NewLine, problems));
        }

        Log.Write($"Installing {InstallRoles.Describe(Answers.Roles)}: {PlanText.Summary(plan)}");
        await plan.ApplyAsync(ContextFor(progress), progress, cancellationToken).ConfigureAwait(false);
        Log.Write($"Installed {InstallRoles.Describe(Answers.Roles)}.");
    }

    /// <summary>Where the wizard offers to save the answers: the folder the installer was started in.</summary>
    public string DefaultAnswersPath => Path.Join(Machine.CurrentDirectory, AnswersFileName);

    /// <summary>
    /// Saves the answers (never a secret, nor the rig's confirmation) to <paramref name="path"/>, relative to the folder the
    /// installer was started in, and returns the full path. <c>hvo-roof-install --answers</c> installs from it.
    /// </summary>
    public string SaveAnswers(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InstallerUsageException("Give a file to save the answers to.");
        }

        var full = Path.GetFullPath(InstallPaths.Expand(Machine, path.Trim()), Machine.CurrentDirectory);
        var folder = Path.GetDirectoryName(full)!;
        if (!Machine.DirectoryExists(folder))
        {
            throw new InstallerUsageException($"There is no folder {folder}.");
        }

        if (Machine.DirectoryExists(full))
        {
            throw new InstallerUsageException($"{full} is a folder: give a file name.");
        }

        Machine.WriteAtomically(full, Answers.ToJson(), Modes.File);
        Log.Write($"Saved the answers to {full}.");
        return full;
    }

    /// <summary>What the Done page and an answers-file install end with: what was installed, where it is reached, and what next.</summary>
    public IReadOnlyList<string> DoneLines()
    {
        var answers = Answers.Normalised();
        var lines = new List<string> { $"Installed {InstallRoles.Describe(answers.Roles)} ({Version}) on {Survey.HostName}." };
        if (answers.Controller is { } controller)
        {
            var rig = answers.Roles.Contains(InstallRole.Rig);
            var scheme = controller.UsesHttps ? "https" : "http";
            var host = rig ? "localhost" : CertificateNames.LocalName(Survey.HostName);
            lines.Add(string.Empty);
            lines.Add($"The controller's API:  {scheme}://{host}:{controller.ApiPort}/");
            lines.Add($"The web UI:            {scheme}://{host}:{controller.WebPort}/");
            lines.Add($"Its log:               docker logs {MachineSurveyor.ControllerContainer}");
            lines.Add(rig
                ? "This is a test rig: the controller drives the HAT emulator, and nothing here moves a roof."
                : "The roof has not moved. Confirm the installation assumptions in commissioning.md on site before the first move.");
            if (!rig)
            {
                var layout = ControllerLayout.For(Machine);
                lines.Add($"Back up {layout.Configuration} and {layout.Data} now, and after each change: they hold its keys, certificates, "
                    + "people and settings. sudo hvo-roof-install backup makes one archive of them (install.md, \"Backing up\").");
            }

            var trust = TrustLines(controller, $"{scheme}://{host}:{controller.ApiPort}/ca.crt").ToArray();
            if (trust.Length > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(trust);
            }
        }

        if (answers.Roles.Contains(InstallRole.Kiosk))
        {
            lines.Add(string.Empty);
            lines.Add($"The kiosk runs on the touchscreen, and starts when the Pi does ({MachineSurveyor.KioskUnit}). Its log: journalctl -u {MachineSurveyor.KioskUnit}");
            if (answers.Kiosk?.HideCursor == true && !KioskCursorStep.Hidden(Machine))
            {
                lines.Add("Reboot to hide the console's cursor behind the kiosk: sudo reboot");
            }
        }

        if (answers.Cli is { } cli)
        {
            var folder = InstallPaths.Expand(Machine, cli.Folder);
            lines.Add(string.Empty);
            lines.Add($"hvo-roof: {Path.Join(folder, ClientSteps.CliProgramName)}, connected to {answers.Client?.Controller} ({ClientSteps.CredentialsFile(Machine)}).");
            lines.Add("Next, sign in as yourself: hvo-roof login NAME");
            if (!(Machine.Environment("PATH") ?? string.Empty).Split(':').Contains(folder))
            {
                lines.Add($"{folder} is not on your PATH: add it (export PATH=\"{folder}:$PATH\" in your shell's profile), or run hvo-roof by its full path.");
            }
        }

        if (answers.MacApp is { } macApp)
        {
            lines.Add(string.Empty);
            lines.Add($"The Mac app: {Path.Join(InstallPaths.Expand(Machine, macApp.Folder), MachineSurveyor.MacAppBundle)}, connected to {answers.Client?.Controller}.");
            lines.Add($"It shows the roof with its own device key (a viewer's, {MacDeviceKeyStep.KeyName(Machine)}); sign in there with your name and password to open or close it.");
            lines.Add($"Its settings and key: {ClientSteps.MacSettingsFolder(Machine)}");
        }

        if (answers.Client is { CaSha256: not null } client && (answers.Roles.Contains(InstallRole.Cli) || answers.Roles.Contains(InstallRole.MacApp)))
        {
            lines.Add(string.Empty);
            lines.AddRange(BrowserLines(answers, client));
        }

        lines.Add(string.Empty);
        foreach (var scope in answers.Roles.Select(role => InstallRoles.ScopeOf(role, Machine.Os)).Distinct().Order())
        {
            lines.Add($"The install record: {InstallPaths.RecordFor(scope, Machine)}");
        }

        if (Log.Path is { } log)
        {
            lines.Add($"The log: {log}");
        }

        return lines;
    }

    // How a browser here comes to trust the controller's CA: the keychain (Safari and Chrome on a Mac), and Firefox's own
    // list. The CA is the Mac app's copy when there is one, else the controller's own.
    private IEnumerable<string> BrowserLines(InstallAnswers answers, ClientSettings client)
    {
        var ca = answers.Roles.Contains(InstallRole.MacApp)
            ? Path.Join(ClientSteps.MacSettingsFolder(Machine), ClientSteps.MacCaFile)
            : $"{client.Controller.TrimEnd('/')}/ca.crt";
        if (Machine.Os != InstallerOs.MacOS)
        {
            yield return $"For the web UI in your browser, trust the controller's CA ({ca}): install.md, \"Trusting the controller in a browser\".";
        }
        else if (client.TrustInKeychain)
        {
            yield return "Safari and Chrome trust the controller's CA: it is in your login keychain.";
        }
        else
        {
            yield return $"For the web UI in Safari and Chrome, trust the controller's CA ({ca}) in your login keychain: "
                + "run the installer again and choose the keychain, or see install.md, \"Trusting the controller in a browser\".";
        }

        yield return $"Firefox may use its own list: if it warns, import the CA ({ca}) in Settings → Privacy & Security → View Certificates → Authorities, "
            + "and tick 'Trust this CA to identify websites'.";
    }

    // What clients trust, and the fingerprint a person checks when one asks: the CA's, or the self-signed certificate's.
    private IEnumerable<string> TrustLines(ControllerSettings controller, string caUrl)
    {
        if (controller.Connection is not (ConnectionMode.PrivateCa or ConnectionMode.SelfSigned))
        {
            yield break;
        }

        var (certificate, authority) = MachineSurveyor.SurveyCertificates(Machine, controller);
        if (controller.Connection == ConnectionMode.PrivateCa && authority is { Fingerprint: { } caFingerprint })
        {
            yield return $"Clients trust its CA:  {authority.Path}, or {caUrl}";
            yield return "Its SHA-256 fingerprint, to check when a client asks you to trust it:";
            yield return caFingerprint;
        }
        else if (controller.Connection == ConnectionMode.SelfSigned && certificate is { Fingerprint: { } fingerprint })
        {
            yield return "Each client pins its self-signed certificate. Its SHA-256 fingerprint, to check when one asks:";
            yield return fingerprint;
        }
    }

    /// <summary>
    /// The answers of what is installed here, from the install records: the machine's (as root, or with
    /// <paramref name="includeSystem"/>) and the person's. Null when nothing is recorded.
    /// </summary>
    public static InstallAnswers? RecordedAnswers(MachineSurvey survey, bool includeSystem)
    {
        ArgumentNullException.ThrowIfNull(survey);
        var record = survey.IsRoot ? survey.SystemRecord : survey.UserRecord ?? (includeSystem ? survey.SystemRecord : null);
        return record?.ToAnswers();
    }

    /// <summary>What the survey found, a line each, with the certificate's warnings at <paramref name="now"/>: the wizard's first page, and the log.</summary>
    public static IReadOnlyList<string> DescribeSurvey(MachineSurvey survey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(survey);
        var lines = new List<string>
        {
            $"Machine:     {survey.HostName} ({survey.RuntimeIdentifier}{(survey.OsName is null ? string.Empty : $", {survey.OsName}")})",
            $"Running as:  {survey.UserName}{(survey.IsRoot && survey.UserName != "root" ? " (root)" : string.Empty)}"
        };
        if (survey.PiModel is { } model)
        {
            lines.Add($"Pi:          {model}");
        }

        var devices = new[] { (Name: HatDevices.I2c, Present: survey.HasI2c), (Name: HatDevices.GpioMemory, Present: survey.HasGpioMemory), (Name: "the thermal sensor", Present: survey.HasThermalSensor) };
        var present = devices.Where(device => device.Present).Select(device => device.Name).ToArray();
        var missing = devices.Where(device => !device.Present).Select(device => device.Name).ToArray();
        lines.Add($"HAT devices: {string.Join("; ", new[] { present.Length > 0 ? $"{string.Join(", ", present)} present" : null, missing.Length > 0 ? $"{string.Join(", ", missing)} missing" : null }.OfType<string>())}");
        lines.Add(survey.Docker.IsUsable
            ? $"Docker:      {survey.Docker.Version}{(survey.Docker.ComposeVersion is null ? ", no Compose v2" : $", Compose {survey.Docker.ComposeVersion}")}"
            : $"Docker:      {survey.Docker.Problem}");
        lines.Add($"Recorded:    {DescribeRecord(survey.SystemRecord, "the machine's")}; {DescribeRecord(survey.UserRecord, "your")}");
        foreach (var problem in survey.RecordProblems)
        {
            lines.Add($"             {problem}");
        }

        if (survey.HasSystemConfiguration && survey.SystemRecord is null)
        {
            lines.Add("Found:       /etc/hvo-roof, set up without the installer");
        }

        foreach (var container in new[] { survey.Controller, survey.HatEmulator })
        {
            if (container is not null)
            {
                var origin = container.Origin == ContainerOrigin.Compose ? $"Docker Compose project {container.ComposeProject}" : "the deploy script";
                var hat = container.Name == MachineSurveyor.ControllerContainer ? container.HatEmulator is null ? ", the real HAT" : $", the HAT emulator at {container.HatEmulator}" : string.Empty;
                lines.Add($"Container:   {container.Name}: {container.State}{(container.Version is null ? string.Empty : $", {container.Version}")}{hat} (made by {origin})");
            }
        }

        if (survey.Kiosk is { } kiosk)
        {
            lines.Add($"Kiosk:       {kiosk.Unit}: {(kiosk.Enabled ? "enabled" : "disabled")}, {(kiosk.Active ? "running" : "stopped")}");
        }

        if (survey.Cli is { } cli)
        {
            lines.Add($"hvo-roof:    {cli.Path}{(cli.Version is null ? string.Empty : $" ({cli.Version})")}");
        }

        if (survey.MacApp is { } macApp)
        {
            lines.Add($"Mac app:     {macApp.Path}{(macApp.Version is null ? string.Empty : $" ({macApp.Version})")}");
        }

        if (survey.Certificate is { } certificate)
        {
            lines.Add(certificate.Problem is { } problem
                ? $"Certificate: {certificate.Path}: {problem}"
                : $"Certificate: {certificate.Subject}, until {Date(certificate.NotAfter)}, {(certificate.Issuer is null ? "self-signed" : $"issued by {certificate.Issuer}")}");
        }

        if (survey.Authority is { } authority)
        {
            lines.Add(authority.Problem is { } problem
                ? $"CA:          {authority.Path}: {problem}"
                : $"CA:          {authority.Subject}, until {Date(authority.NotAfter)}");
        }

        lines.AddRange(CertificateWarnings(survey, now).Select(warning => $"             {warning}"));
        return lines;
    }

    /// <summary>
    /// What needs doing about the controller's certificate or its CA at <paramref name="now"/>, a sentence each: it has
    /// expired or soon will, it is not for a name clients use, or its password does not open it. Every run says them.
    /// </summary>
    public static IReadOnlyList<string> CertificateWarnings(MachineSurvey survey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(survey);
        return CertificateWarnings(survey.Certificate, survey.Authority, now, survey.RecordedController?.Connection);
    }

    /// <summary>
    /// What needs doing about <paramref name="certificate"/> and <paramref name="authority"/> at <paramref name="now"/>, a
    /// sentence each. <paramref name="recorded"/> is how the record says the controller serves HTTPS, when it says; when
    /// it says plain HTTP there are none, since the controller serves no certificate (one left in place is not used).
    /// </summary>
    public static IReadOnlyList<string> CertificateWarnings(CertificateSurvey? certificate, AuthoritySurvey? authority, DateTimeOffset now, ConnectionMode? recorded = null)
    {
        var warnings = new List<string>();
        if (recorded == ConnectionMode.Http)
        {
            return warnings;
        }

        if (certificate is not null)
        {
            // Your own certificate is renewed by whoever issued it; the installer renews the ones it makes. The record says
            // which it is; with nothing recorded, one this machine's CA did not issue is taken to be yours.
            var own = recorded is { } connection ? connection == ConnectionMode.OwnCertificate : certificate.IsTheirs;
            var renew = own ? "Put a new one in place with: sudo hvo-roof-install cert import FILE" : "Renew it with: sudo hvo-roof-install cert";
            if (certificate.Problem is { } problem)
            {
                // Without root nothing can be said; a password that does not open it is a certificate the controller cannot serve.
                if (!certificate.NeedsRoot)
                {
                    warnings.Add($"The controller cannot serve its certificate: {problem}.");
                }
            }
            else if (certificate.NotAfter is { } notAfter)
            {
                var left = notAfter - now;
                if (left <= TimeSpan.Zero)
                {
                    warnings.Add($"The certificate expired on {Date(notAfter)}: clients refuse it. {renew}");
                }
                else if (left < ControllerCertificates.RenewWithin)
                {
                    warnings.Add($"The certificate expires on {Date(notAfter)}, in {(int)Math.Ceiling(left.TotalDays)} days. {renew}");
                }

                if (certificate.Uncovered.Count > 0)
                {
                    warnings.Add($"The certificate is not for {string.Join(", ", certificate.Uncovered)}: a client that uses {(certificate.Uncovered.Count == 1 ? "it" : "one")} refuses it.");
                }
            }
        }

        if (authority is { NotAfter: { } authorityExpires } && authorityExpires - now < ControllerCertificates.RenewAuthorityWithin)
        {
            warnings.Add(authorityExpires <= now
                ? $"The CA expired on {Date(authorityExpires)}: the installer makes a new one, which every client must then trust."
                : $"The CA expires on {Date(authorityExpires)}: the installer makes a new one, which every client must then trust.");
        }

        return warnings;
    }

    /// <summary>The release a version names: its version without the commit.</summary>
    public static string RoofVersion(string informationalVersion)
        => Common.RoofProductVersion.WithoutCommit(informationalVersion);

    internal static string Date(DateTimeOffset? date) => date is { } value ? AuthorityAssessment.Date(value.UtcDateTime) : "?";

    private static string DescribeRecord(InstallRecord? record, string whose)
        => record is null ? $"nothing in {whose} record" : $"{InstallRoles.Describe(record.Roles)} {record.Version} in {whose} record";
}
