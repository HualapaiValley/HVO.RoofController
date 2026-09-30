using System.CommandLine;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.Installer.Wizard;

namespace HVO.RoofControllerV4.Installer;

/// <summary>The <c>hvo-roof-install</c> process.</summary>
public static class InstallerProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var interrupt = new CancellationTokenSource();

        // Ctrl+C outside the wizard (which reads its own keys) stops the install between steps.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            interrupt.Cancel();
        };
        return await Installer.RunAsync(args, InstallerHost.System(), interrupt.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>hvo-roof-install</c>: installs the roof controller's pieces on this machine (the controller, a test rig, the kiosk,
/// hvo-roof, the Mac app) and records what it installed. With no options it opens the wizard; <c>--answers</c> installs
/// from a saved answers file without asking anything; <c>--plan</c> says what an install would do and changes nothing.
/// It never moves the roof.
/// </summary>
public static class Installer
{
    public const string CommandName = "hvo-roof-install";

    public static async Task<int> RunAsync(string[] args, InstallerHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        var answers = new Option<string?>("--answers")
        {
            Description = "Install from an answers file (the wizard saves one on its review page) without asking anything.",
            HelpName = "FILE"
        };
        var plan = new Option<bool>("--plan")
        {
            Description = "Print every folder, file, container, service and port the install would make or change, and change nothing. Uses --answers, or what is installed."
        };
        var release = new Option<string?>("--release")
        {
            Description = $"Read the release (its {ReleaseManifest.FileName}) from this folder, not from GitHub: for a machine that cannot reach GitHub.",
            HelpName = "DIR"
        };
        var version = new Option<bool>("--version")
        {
            Description = "Print the installer's version (the release it installs) and change nothing."
        };
        var secretFiles = Enum.GetValues<InstallSecret>().ToDictionary(
            secret => secret,
            secret => new Option<string?>(InstallSecrets.OptionName(secret))
            {
                Description = $"Read {InstallSecrets.Describe(secret)} from the first line of this file, which only you may read, in place of typing it.",
                HelpName = "FILE"
            });
        var root = new RootCommand(
            "Installs the roof controller, a test rig, the kiosk, hvo-roof or the Mac app on this machine, and records what it installed. "
            + "With no options it opens the wizard. It never moves the roof.");

        // The built-in --version names the process's entry assembly; this one names the installer's release.
        foreach (var builtIn in root.Options.OfType<VersionOption>().ToArray())
        {
            root.Options.Remove(builtIn);
        }

        root.Options.Add(answers);
        root.Options.Add(plan);
        root.Options.Add(release);
        root.Options.Add(version);
        foreach (var option in secretFiles.Values)
        {
            root.Options.Add(option);
        }

        root.Subcommands.Add(CertificateCommands.Create(host));
        root.SetAction((parseResult, token) =>
        {
            if (parseResult.GetValue(version))
            {
                host.Out.WriteLine(host.Version);
                return Task.FromResult((int)InstallerExitCode.Success);
            }

            var options = new RunOptions(parseResult.GetValue(answers), parseResult.GetValue(plan), parseResult.GetValue(release))
            {
                SecretFiles = secretFiles
                    .Select(option => (option.Key, File: parseResult.GetValue(option.Value)))
                    .Where(option => option.File is not null)
                    .ToDictionary(option => option.Key, option => option.File!)
            };
            return RunAsync(host, options, token);
        });

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors)
            {
                host.Error.WriteLine(error.Message);
            }

            host.Error.WriteLine($"Run '{CommandName} --help' for usage.");
            return (int)InstallerExitCode.Usage;
        }

        return await parsed.InvokeAsync(
            new InvocationConfiguration
            {
                Output = host.Out,
                Error = host.Error,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the command line asks for.</summary>
    /// <param name="AnswersFile"><c>--answers</c>: install from this file.</param>
    /// <param name="PlanOnly"><c>--plan</c>: say what an install would do.</param>
    /// <param name="ReleaseFolder"><c>--release</c>: the folder with the release's release.json, in place of GitHub.</param>
    internal sealed record RunOptions(string? AnswersFile, bool PlanOnly, string? ReleaseFolder)
    {
        /// <summary><c>--admin-password-file</c> and the others: the files that hold the passwords and PINs.</summary>
        public IReadOnlyDictionary<InstallSecret, string> SecretFiles { get; init; } = new Dictionary<InstallSecret, string>();

        public ReleaseSource Release(InstallerHost host)
            => ReleaseFolder is { } folder ? ReleaseSource.Folder(Path.GetFullPath(folder, host.Machine.CurrentDirectory)) : ReleaseSource.GitHub();
    }

    private static Task<int> RunAsync(InstallerHost host, RunOptions options, CancellationToken cancellationToken)
        => GuardAsync(
            host,
            () =>
            {
                if (options.ReleaseFolder is { } folder && !host.Machine.DirectoryExists(Path.GetFullPath(folder, host.Machine.CurrentDirectory)))
                {
                    throw new InstallerUsageException($"There is no folder {folder} for --release.");
                }

                if (options.PlanOnly)
                {
                    return PlanAsync(host, options, cancellationToken);
                }

                if (options.AnswersFile is { } answersFile)
                {
                    return InstallAsync(host, answersFile, options, cancellationToken);
                }

                if (!host.IsInteractive)
                {
                    throw new InstallerUsageException($"The wizard needs a terminal. Without one, install from an answers file: {CommandName} --answers FILE.");
                }

                return WizardAsync(host, options, cancellationToken);
            },
            cancellationToken);

    /// <summary>Runs one of the installer's commands, turning what stops it into a message and an exit code.</summary>
    internal static async Task<int> GuardAsync(InstallerHost host, Func<Task<int>> run, CancellationToken cancellationToken)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (InstallerException error)
        {
            host.Error.WriteLine(error.Message);
            return (int)error.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            host.Error.WriteLine("Stopped. Run the installer again to carry on: it changes only what is left.");
            return (int)InstallerExitCode.Cancelled;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file the installer could not read or write, outside a step (which says which step it was).
            host.Error.WriteLine($"The installer stopped: {error.Message}");
            return (int)InstallerExitCode.Failed;
        }
    }

    /// <summary><c>--plan</c>: what the answers (or what is installed) would make and change here. Nothing is logged or changed.</summary>
    private static async Task<int> PlanAsync(InstallerHost host, RunOptions options, CancellationToken cancellationToken)
    {
        var session = await InstallerSession.StartAsync(host.Machine, InstallLog.None, host.Version, host.Time, options.Release(host), cancellationToken).ConfigureAwait(false);
        WriteWarnings(host, session);
        session.Answers = options.AnswersFile is { } answersFile
            ? ReadAnswers(host, answersFile)
            : InstallerSession.RecordedAnswers(session.Survey, includeSystem: true)
                ?? throw new InstallerUsageException($"Nothing is recorded as installed here: give the answers to plan, {CommandName} --plan --answers FILE.");

        var problems = session.Problems(planOnly: true);
        if (problems.Count > 0)
        {
            WriteRefusal(host, problems);
            return (int)InstallerExitCode.Refused;
        }

        GiveSecretFiles(host, session, options);
        var checkedPlan = await session.CheckAsync(cancellationToken).ConfigureAwait(false);
        host.Out.WriteLine($"The plan for {InstallRoles.Describe(session.Answers.Roles)} on {session.Survey.HostName}, {session.Version}:");
        host.Out.WriteLine();
        foreach (var line in PlanText.Lines(checkedPlan))
        {
            host.Out.WriteLine(line);
        }

        var missing = session.MissingSecrets(checkedPlan);
        if (missing.Count > 0)
        {
            host.Out.WriteLine();
            foreach (var secret in missing)
            {
                host.Out.WriteLine($"The install asks for {InstallSecrets.Describe(secret)} (or give it with {InstallSecrets.OptionName(secret)} FILE).");
            }
        }

        var system = session.Answers.Roles.Where(role => InstallRoles.ScopeOf(role, session.Machine.Os) == InstallScope.System).ToArray();
        if (!session.Survey.IsRoot && system.Length > 0 && checkedPlan.HasChanges)
        {
            host.Out.WriteLine($"Installing {InstallRoles.Describe(system)} needs root: run the installer with sudo.");
        }

        return checkedPlan.IsBlocked ? (int)InstallerExitCode.Refused : (int)InstallerExitCode.Success;
    }

    /// <summary><c>--answers FILE</c>: installs from the answers, asking nothing.</summary>
    private static async Task<int> InstallAsync(InstallerHost host, string answersFile, RunOptions options, CancellationToken cancellationToken)
    {
        var answers = ReadAnswers(host, answersFile);
        var log = InstallLog.Open(host.Machine, InstallPaths.Log(host.Machine), host.Time);
        var session = await InstallerSession.StartAsync(host.Machine, log, host.Version, host.Time, options.Release(host), cancellationToken).ConfigureAwait(false);
        WriteWarnings(host, session);
        session.Answers = answers;
        log.Write($"Installing from {Path.GetFullPath(answersFile, host.Machine.CurrentDirectory)}: {InstallRoles.Describe(answers.Roles)}.");

        var problems = session.Problems();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                log.Write($"Refused: {problem}");
            }

            WriteRefusal(host, problems);
            return (int)InstallerExitCode.Refused;
        }

        GiveSecretFiles(host, session, options);
        var checkedPlan = await session.CheckAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in PlanText.Lines(checkedPlan))
        {
            host.Out.WriteLine(line);
        }

        if (checkedPlan.IsBlocked)
        {
            log.Write($"Refused: {PlanText.Summary(checkedPlan)}");
            return (int)InstallerExitCode.Refused;
        }

        try
        {
            AskForSecrets(host, session, checkedPlan);
        }
        catch (InstallerException error)
        {
            log.Write($"Refused: {error.Message}");
            throw;
        }

        host.Out.WriteLine();
        try
        {
            await session.ApplyAsync(checkedPlan, host.Out.WriteLine, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerRefusedException error)
        {
            // Refused before the first change: nothing to carry on from.
            log.Write($"Refused: {error.Message}");
            throw;
        }
        catch (InstallerException error)
        {
            throw new InstallerException($"The install stopped: {error.Message}{Environment.NewLine}Nothing after that step was changed; the log is {log.Path}. Run the installer again to carry on.", error.ExitCode);
        }

        host.Out.WriteLine();
        foreach (var line in session.DoneLines())
        {
            host.Out.WriteLine(line);
        }

        return (int)InstallerExitCode.Success;
    }

    private static async Task<int> WizardAsync(InstallerHost host, RunOptions options, CancellationToken cancellationToken)
    {
        host.Error.WriteLine("Looking at this machine…");
        var log = InstallLog.Open(host.Machine, InstallPaths.Log(host.Machine), host.Time);
        var session = await InstallerSession.StartAsync(host.Machine, log, host.Version, host.Time, options.Release(host), cancellationToken).ConfigureAwait(false);
        GiveSecretFiles(host, session, options);
        var app = host.CreateApplication();
        InstallerWizard? wizard = null;
        try
        {
            wizard = new InstallerWizard(app, session, host.Machine.Environment);
            using (cancellationToken.Register(() => wizard.Post(wizard.Quit)))
            {
                host.RunApplication(app, wizard.Window);
            }
        }
        finally
        {
            wizard?.Dispose();
            app.Dispose();
        }

        // After the terminal is back: the Done text stays on the screen.
        if (wizard.Result == InstallerExitCode.Success)
        {
            foreach (var line in session.DoneLines())
            {
                host.Out.WriteLine(line);
            }
        }
        else if (wizard.Failure is { } failure)
        {
            host.Error.WriteLine(failure);
        }

        return (int)wizard.Result;
    }

    internal static InstallAnswers ReadAnswers(InstallerHost host, string answersFile)
    {
        var path = Path.GetFullPath(answersFile, host.Machine.CurrentDirectory);
        string? json;
        try
        {
            json = host.Machine.ReadText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InstallerUsageException($"The answers file {path} could not be read: {error.Message}");
        }

        return InstallAnswers.Parse(json ?? throw new InstallerUsageException($"There is no answers file {path}."));
    }

    // The passwords and PINs given in files (--admin-password-file and the others), each checked before anything is planned.
    private static void GiveSecretFiles(InstallerHost host, InstallerSession session, RunOptions options)
    {
        foreach (var (secret, file) in options.SecretFiles.OrderBy(pair => pair.Key))
        {
            session.GiveSecret(secret, InstallSecrets.ReadFile(host.Machine, secret, Path.GetFullPath(file, host.Machine.CurrentDirectory)));
        }
    }

    /// <summary>
    /// The passwords and PINs <paramref name="plan"/> needs that no file gave: each typed twice at the terminal, and never
    /// shown. Without a terminal, the install is refused before anything changes.
    /// </summary>
    internal static void AskForSecrets(InstallerHost host, InstallerSession session, CheckedPlan plan)
    {
        var missing = session.MissingSecrets(plan);
        foreach (var secret in missing)
        {
            if (secret == InstallSecret.CameraPassword)
            {
                host.Error.WriteLine(CameraCredentialReminder);
            }

            session.GiveSecret(secret, AskFor(host, secret, missing));
        }
    }

    /// <summary>
    /// Said before the camera's password is asked for: the Blue Iris credential the source once held is in the public git
    /// history (docs/security.md).
    /// </summary>
    public const string CameraCredentialReminder =
        "The Blue Iris credential that earlier versions held in their source is still in the public git history: give the controller a "
        + "view-only Blue Iris user of its own, and change the old user's password if you have not (docs/security.md, \"Rotate the Blue Iris credential\").";

    private const int Tries = 3;

    private static string AskFor(InstallerHost host, InstallSecret secret, IReadOnlyList<InstallSecret> missing)
    {
        var what = InstallSecrets.Describe(secret);
        for (var attempt = 1; ; attempt++)
        {
            var typed = host.ReadSecret(what) ?? throw NoTerminal(missing);
            var problem = InstallSecrets.Problem(secret, typed);
            if (problem is null)
            {
                var again = host.ReadSecret($"{what} again") ?? throw NoTerminal(missing);
                if (string.Equals(typed, again, StringComparison.Ordinal))
                {
                    return typed;
                }

                problem = secret == InstallSecret.AdminPin ? "The two PINs differ." : "The two passwords differ.";
            }

            host.Error.WriteLine(problem);
            if (attempt == Tries)
            {
                throw new InstallerUsageException($"{char.ToUpperInvariant(what[0])}{what[1..]} was not given in {Tries} tries, so nothing was changed.");
            }
        }
    }

    private static InstallerUsageException NoTerminal(IReadOnlyList<InstallSecret> missing)
    {
        var them = missing.Count == 1 ? "it" : "them";
        return new InstallerUsageException(
            $"The install needs {string.Join(" and ", missing.Select(InstallSecrets.Describe))}, and there is no terminal to type {them} at: "
            + $"give {them} with {string.Join(" and ", missing.Select(secret => $"{InstallSecrets.OptionName(secret)} FILE"))}. Nothing was changed.");
    }

    // What needs doing about the certificate: the wizard shows it on its first page, and every other run says it too.
    internal static void WriteWarnings(InstallerHost host, InstallerSession session)
    {
        foreach (var warning in InstallerSession.CertificateWarnings(session.Survey, host.Time.GetUtcNow()))
        {
            host.Error.WriteLine($"Warning: {warning}");
        }
    }

    internal static void WriteRefusal(InstallerHost host, IReadOnlyList<string> problems)
    {
        host.Error.WriteLine("The installer cannot go ahead:");
        foreach (var problem in problems)
        {
            host.Error.WriteLine($"  - {problem}");
        }

        host.Error.WriteLine("Nothing was changed.");
    }
}
