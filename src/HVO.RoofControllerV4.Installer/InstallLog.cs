using System.Globalization;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// The installer's log: what it found, each command it ran and how it ended, and each change it made, with the time.
/// It never holds a secret: a secret the installer handles is registered (<see cref="AddSecret"/>) and written as
/// [secret] wherever it would appear, and a secret command (<see cref="CommandLine.Secret"/>) is logged without its
/// input or output. <c>--plan</c> writes no log (<see cref="None"/>): it changes nothing.
/// </summary>
public sealed class InstallLog
{
    private const int MaxOutputLines = 40;

    private readonly InstallerMachine? _machine;
    private readonly TimeProvider _time;
    private readonly List<string> _secrets = [];
    private readonly object _gate = new();
    private bool _failed;

    private InstallLog(InstallerMachine? machine, string? path, TimeProvider time)
    {
        _machine = machine;
        Path = path;
        _time = time;
    }

    /// <summary>A log that writes nothing.</summary>
    public static InstallLog None { get; } = new(null, null, TimeProvider.System);

    /// <summary>
    /// Opens the log at <paramref name="path"/>, making its folder when needed. As root the file is 0640, owned by root;
    /// for a person, 0600.
    /// </summary>
    public static InstallLog Open(InstallerMachine machine, string path, TimeProvider time)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        if (!machine.DirectoryExists(folder))
        {
            machine.CreateDirectory(folder, machine.IsRoot ? Modes.Folder : Modes.PrivateFolder);
        }

        return new InstallLog(machine, path, time);
    }

    /// <summary>The log's path on the machine, or null when nothing is logged.</summary>
    public string? Path { get; }

    /// <summary>Everything written from now on shows <paramref name="secret"/> as [secret].</summary>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(secret))
            {
                _secrets.Add(secret);

                // The longest first, so a secret that contains another is replaced whole.
                _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
            }
        }
    }

    public void Write(string message)
    {
        if (_machine is null || Path is null)
        {
            return;
        }

        var stamp = _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            var text = Redact(message);
            try
            {
                foreach (var line in text.Split('\n'))
                {
                    _machine.AppendLine(Path, $"{stamp} {line.TrimEnd('\r')}", _machine.IsRoot ? Modes.GroupFile : Modes.PrivateFile);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must not stop the install; the person sees what happens on screen.
                _failed = true;
            }
        }
    }

    /// <summary>Logs a command and how it ended: its output too, unless it is secret.</summary>
    public void Command(CommandLine command, CommandResult result)
    {
        Write($"$ {command}");
        if (!command.Secret)
        {
            foreach (var line in Tail(result.Output).Concat(Tail(result.Error)))
            {
                Write($"  {line}");
            }
        }

        Write($"  exit {result.ExitCode}{(command.Secret ? " (secret: output not logged)" : string.Empty)}");
    }

    /// <summary><paramref name="text"/> with every registered secret replaced.</summary>
    public string Redact(string text)
    {
        lock (_gate)
        {
            foreach (var secret in _secrets)
            {
                // A secret of digits only (the admin PIN) is replaced only where it stands alone: inside a longer number,
                // or a digest, [secret] would show where the PIN's digits are, and so the PIN.
                text = secret.All(char.IsAsciiDigit)
                    ? Regex.Replace(text, $"(?<![0-9A-Za-z]){secret}(?![0-9A-Za-z])", "[secret]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    : text.Replace(secret, "[secret]", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static IEnumerable<string> Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaxOutputLines)
        {
            yield return $"… {lines.Length - MaxOutputLines} lines before these not logged";
        }

        foreach (var line in lines.Skip(Math.Max(0, lines.Length - MaxOutputLines)))
        {
            yield return line.TrimEnd('\r');
        }
    }
}

/// <summary>Runs commands through another runner and logs each one.</summary>
public sealed class LoggingCommandRunner(ICommandRunner inner, InstallLog log) : ICommandRunner
{
    public string? Find(string program) => inner.Find(program);

    public async Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
    {
        var result = await inner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        log.Command(command, result);
        return result;
    }
}
