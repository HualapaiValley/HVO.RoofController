using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HVO.RoofControllerV4.Installer.Machine;

/// <summary>
/// A program to run and its arguments. The arguments are passed as they are, never through a shell. When
/// <see cref="Secret"/> is set, the log shows neither the input nor the output (see <see cref="InstallLog"/>).
/// </summary>
public sealed record CommandLine(string Program, IReadOnlyList<string> Arguments)
{
    public CommandLine(string program, params string[] arguments)
        : this(program, (IReadOnlyList<string>)arguments)
    {
    }

    /// <summary>Standard input, or null for none.</summary>
    public string? Input { get; init; }

    /// <summary>True when the input or the output may hold a secret: the log then records only the command and its exit code.</summary>
    public bool Secret { get; init; }

    /// <summary>
    /// The variables that a command which does not <see cref="InheritEnvironment"/> still gets from the installer's: where
    /// programs are, the person's home and terminal, and which Docker daemon the installer asked.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptEnvironment =
        ["PATH", "HOME", "USER", "LOGNAME", "LANG", "TERM", "TMPDIR", "DOCKER_HOST", "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY"];

    /// <summary>Variables added to the installer's environment for this command.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// False to give the command only <see cref="KeptEnvironment"/> and <see cref="Environment"/>: for a script that reads
    /// its settings from variables, so none the person happened to have set reaches it.
    /// </summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>How long the command may run before it is stopped (and fails); null waits as long as it takes.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Called with each line of output as it comes (for a long command's progress); never for a secret command.</summary>
    public Action<string>? OnOutputLine { get; init; }

    public override string ToString() => string.Join(' ', new[] { Program }.Concat(Arguments.Select(Quote)));

    private static string Quote(string argument)
        => argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@%+,{}".Contains(c))
            ? argument
            : $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}

/// <summary>How a command ended: its exit code (127 when the program is not there) and what it wrote.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public const int NotFound = 127;

    /// <summary>The program is there, but it could not be run (as a shell says, 126).</summary>
    public const int CannotRun = 126;

    public bool Succeeded => ExitCode == 0;

    /// <summary>The error output, or the output when there is none: what to show when the command failed.</summary>
    public string Reason => (string.IsNullOrWhiteSpace(Error) ? Output : Error).Trim();
}

/// <summary>Runs the programs the installer needs (docker, systemctl, stat…). Tests give a fake that answers from a script.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default);

    /// <summary>The full path of <paramref name="program"/> on the PATH, or null when it is not there.</summary>
    string? Find(string program);
}

/// <summary>Runs commands as processes on this machine.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private readonly Func<string, string?> _environment;

    public ProcessCommandRunner(Func<string, string?>? environment = null)
    {
        _environment = environment ?? System.Environment.GetEnvironmentVariable;
    }

    public string? Find(string program)
    {
        if (program.Contains('/', StringComparison.Ordinal))
        {
            return IsProgram(program) ? program : null;
        }

        foreach (var folder in (_environment("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, program);
            if (IsProgram(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsProgram(string path)
        => File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    public async Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var path = Find(command.Program);
        if (path is null)
        {
            return new CommandResult(CommandResult.NotFound, string.Empty, $"{command.Program}: not found");
        }

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!command.InheritEnvironment)
        {
            start.Environment.Clear();
            foreach (var name in CommandLine.KeptEnvironment)
            {
                if (_environment(name) is { } value)
                {
                    start.Environment[name] = value;
                }
            }
        }

        // Programs answer in English, so the installer can read what they say.
        start.Environment["LC_ALL"] = "C";
        if (command.Environment is not null)
        {
            foreach (var (name, value) in command.Environment)
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.Append(e.Data).Append('\n');
            }

            if (!command.Secret)
            {
                command.OnOutputLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (error)
            {
                error.Append(e.Data).Append('\n');
            }

            if (!command.Secret)
            {
                command.OnOutputLine?.Invoke(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception startError)
        {
            // There, but not a program this machine can run: one built for another processor, say.
            return new CommandResult(CommandResult.CannotRun, output.ToString(), $"{command.Program} could not be run: {startError.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (command.Input is not null)
            {
                await process.StandardInput.WriteAsync(command.Input.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program ended without reading its input; its exit code says how it went.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Timeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(124, output.ToString(), $"{command.Program} did not finish within {command.Timeout}.");
        }

        // The asynchronous reads end after the process does.
        process.WaitForExit();
        lock (output)
        {
            lock (error)
            {
                return new CommandResult(process.ExitCode, output.ToString(), error.ToString());
            }
        }
    }
}
