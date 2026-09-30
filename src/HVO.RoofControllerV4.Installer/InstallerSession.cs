using HVO.RoofControllerV4.Installer.Answers;
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

    private InstallerSession(InstallerMachine machine, InstallLog log, string version, MachineSurvey survey, TimeProvider time)
    {
        Machine = machine;
        Log = log;
        Version = version;
        Survey = survey;
        Time = time;
        Answers = RecordedAnswers(survey, includeSystem: survey.IsRoot) ?? new InstallAnswers();
    }

    /// <summary>
    /// Looks at the machine (every command logged) and starts a session there, with the answers of what was installed
    /// before (<see cref="RecordedAnswers"/>) as the starting point.
    /// </summary>
    public static async Task<InstallerSession> StartAsync(InstallerMachine machine, InstallLog log, string version, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(log);
        var logged = machine.WithCommands(new LoggingCommandRunner(machine.Commands, log));
        log.Write($"hvo-roof-install {version} on {machine.HostName} ({machine.RuntimeIdentifier}), as {machine.UserName}.");
        var survey = await MachineSurveyor.SurveyAsync(logged, cancellationToken).ConfigureAwait(false);
        foreach (var line in DescribeSurvey(survey))
        {
            log.Write(line);
        }

        return new InstallerSession(logged, log, RoofVersion(version), survey, time);
    }

    public InstallerMachine Machine { get; }

    public InstallLog Log { get; }

    /// <summary>The release this installer installs (its version without the commit).</summary>
    public string Version { get; }

    public MachineSurvey Survey { get; }

    public TimeProvider Time { get; }

    /// <summary>The answers so far: the roles and choices.</summary>
    public InstallAnswers Answers { get; set; }

    public IReadOnlyList<RoleOption> Options => RoleGuards.Options(Survey);

    /// <summary>Why the installer refuses the answers here; empty when it may go ahead.</summary>
    public IReadOnlyList<string> Problems(bool planOnly = false) => RoleGuards.Check(Survey, Answers, planOnly);

    public InstallContext Context => new()
    {
        Machine = Machine,
        Log = Log,
        Survey = Survey,
        Answers = Answers.Normalised(),
        Version = Version,
        Time = Time
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
        await plan.ApplyAsync(Context, progress, cancellationToken).ConfigureAwait(false);
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
            var host = rig ? "localhost" : $"{Survey.HostName}.local";
            lines.Add(string.Empty);
            lines.Add($"The controller's API:  {scheme}://{host}:{controller.ApiPort}/");
            lines.Add($"The web UI:            {scheme}://{host}:{controller.WebPort}/");
            lines.Add(rig
                ? "This is a test rig: the controller drives the HAT emulator, and nothing here moves a roof."
                : "The roof has not moved. Confirm the installation assumptions in commissioning.md on site before the first move.");
        }

        if (answers.Roles.Contains(InstallRole.Kiosk))
        {
            lines.Add(string.Empty);
            lines.Add($"The kiosk starts on the touchscreen when the Pi starts ({MachineSurveyor.KioskUnit}).");
        }

        if (answers.Cli is { } cli)
        {
            var folder = InstallPaths.Expand(Machine, cli.Folder);
            lines.Add(string.Empty);
            lines.Add($"hvo-roof: {Path.Join(folder, "hvo-roof")}. Next, connect it to the controller: hvo-roof setup");
            if (!(Machine.Environment("PATH") ?? string.Empty).Split(':').Contains(folder))
            {
                lines.Add($"{folder} is not on your PATH: add it (export PATH=\"{folder}:$PATH\" in your shell's profile), or run hvo-roof by its full path.");
            }
        }

        if (answers.MacApp is { } macApp)
        {
            lines.Add(string.Empty);
            lines.Add($"The Mac app: {Path.Join(InstallPaths.Expand(Machine, macApp.Folder), MachineSurveyor.MacAppBundle)}. Open it and sign in.");
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

    /// <summary>What the survey found, a line each: the wizard's first page, and the log.</summary>
    public static IReadOnlyList<string> DescribeSurvey(MachineSurvey survey)
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

        return lines;
    }

    /// <summary>The release a version names: its version without the commit.</summary>
    public static string RoofVersion(string informationalVersion)
        => Common.RoofProductVersion.WithoutCommit(informationalVersion);

    private static string DescribeRecord(InstallRecord? record, string whose)
        => record is null ? $"nothing in {whose} record" : $"{InstallRoles.Describe(record.Roles)} {record.Version} in {whose} record";
}
