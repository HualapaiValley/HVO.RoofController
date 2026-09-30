using System.CommandLine;
using HVO.RoofControllerV4.Installer.Answers;
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
        var version = new Option<bool>("--version")
        {
            Description = "Print the installer's version (the release it installs) and change nothing."
        };
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
        root.Options.Add(version);
        root.SetAction((parseResult, token) =>
        {
            if (parseResult.GetValue(version))
            {
                host.Out.WriteLine(host.Version);
                return Task.FromResult((int)InstallerExitCode.Success);
            }

            return RunAsync(host, parseResult.GetValue(answers), parseResult.GetValue(plan), token);
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

    private static async Task<int> RunAsync(InstallerHost host, string? answersFile, bool planOnly, CancellationToken cancellationToken)
    {
        try
        {
            if (planOnly)
            {
                return await PlanAsync(host, answersFile, cancellationToken).ConfigureAwait(false);
            }

            if (answersFile is not null)
            {
                return await InstallAsync(host, answersFile, cancellationToken).ConfigureAwait(false);
            }

            if (!host.IsInteractive)
            {
                throw new InstallerUsageException($"The wizard needs a terminal. Without one, install from an answers file: {CommandName} --answers FILE.");
            }

            return await WizardAsync(host, cancellationToken).ConfigureAwait(false);
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
    }

    /// <summary><c>--plan</c>: what the answers (or what is installed) would make and change here. Nothing is logged or changed.</summary>
    private static async Task<int> PlanAsync(InstallerHost host, string? answersFile, CancellationToken cancellationToken)
    {
        var session = await InstallerSession.StartAsync(host.Machine, InstallLog.None, host.Version, host.Time, cancellationToken).ConfigureAwait(false);
        session.Answers = answersFile is not null
            ? ReadAnswers(host, answersFile)
            : InstallerSession.RecordedAnswers(session.Survey, includeSystem: true)
                ?? throw new InstallerUsageException($"Nothing is recorded as installed here: give the answers to plan, {CommandName} --plan --answers FILE.");

        var problems = session.Problems(planOnly: true);
        if (problems.Count > 0)
        {
            WriteRefusal(host, problems);
            return (int)InstallerExitCode.Refused;
        }

        var checkedPlan = await session.CheckAsync(cancellationToken).ConfigureAwait(false);
        host.Out.WriteLine($"The plan for {InstallRoles.Describe(session.Answers.Roles)} on {session.Survey.HostName}, {session.Version}:");
        host.Out.WriteLine();
        foreach (var line in PlanText.Lines(checkedPlan))
        {
            host.Out.WriteLine(line);
        }

        var system = session.Answers.Roles.Where(role => InstallRoles.ScopeOf(role, session.Machine.Os) == InstallScope.System).ToArray();
        if (!session.Survey.IsRoot && system.Length > 0 && checkedPlan.HasChanges)
        {
            host.Out.WriteLine($"Installing {InstallRoles.Describe(system)} needs root: run the installer with sudo.");
        }

        return checkedPlan.IsBlocked ? (int)InstallerExitCode.Refused : (int)InstallerExitCode.Success;
    }

    /// <summary><c>--answers FILE</c>: installs from the answers, asking nothing.</summary>
    private static async Task<int> InstallAsync(InstallerHost host, string answersFile, CancellationToken cancellationToken)
    {
        var answers = ReadAnswers(host, answersFile);
        var log = InstallLog.Open(host.Machine, InstallPaths.Log(host.Machine), host.Time);
        var session = await InstallerSession.StartAsync(host.Machine, log, host.Version, host.Time, cancellationToken).ConfigureAwait(false);
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

    private static async Task<int> WizardAsync(InstallerHost host, CancellationToken cancellationToken)
    {
        host.Error.WriteLine("Looking at this machine…");
        var log = InstallLog.Open(host.Machine, InstallPaths.Log(host.Machine), host.Time);
        var session = await InstallerSession.StartAsync(host.Machine, log, host.Version, host.Time, cancellationToken).ConfigureAwait(false);
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

    private static InstallAnswers ReadAnswers(InstallerHost host, string answersFile)
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

    private static void WriteRefusal(InstallerHost host, IReadOnlyList<string> problems)
    {
        host.Error.WriteLine("The installer cannot go ahead:");
        foreach (var problem in problems)
        {
            host.Error.WriteLine($"  - {problem}");
        }

        host.Error.WriteLine("Nothing was changed.");
    }
}
