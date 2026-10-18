using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>
/// The deploy script (src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh), which the installer carries whole and
/// deploys the controller through: its pre-flight check of the new image, its verified Stop before a running controller
/// is replaced, its checks of the new one, and its restore of the old one when they fail. Settings go to it in its
/// environment, and a key only as a file's path.
/// </summary>
public static class DeployScript
{
    public const string FileName = "deploy-roofcontroller-rpi.sh";

    /// <summary>The script's text.</summary>
    public static string Text()
    {
        using var stream = typeof(DeployScript).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InstallerException($"The installer was built without {FileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A folder of the installer's own, which only its user can open, for the script and the files it is given; deleted
    /// when the run ends, whatever happens. It is made, new, only in a temporary folder that no one but root (or the
    /// installer's user) can change, by that folder's physical path (<see cref="InstallerMachine.TrustedFolder"/>), so what
    /// the installer writes and checks in it (an archive's copy that Docker loads, the deploy script that bash runs) is
    /// what is read back from it; and it is deleted only while it is still the folder made.
    /// </summary>
    public sealed class WorkFolder : IDisposable
    {
        private readonly InstallerMachine _machine;

        public WorkFolder(InstallerMachine machine)
        {
            ArgumentNullException.ThrowIfNull(machine);
            _machine = machine;
            Path = System.IO.Path.Join(machine.TrustedFolder(machine.TemporaryDirectory), $"hvo-roof-install-{Guid.NewGuid():N}");
            machine.CreatePrivateDirectory(Path);
        }

        /// <summary>The folder, by its physical path: no link in it.</summary>
        public string Path { get; }

        public void Dispose() => _machine.DeletePrivateDirectory(Path);
    }

    /// <summary>
    /// Runs the script from <paramref name="folder"/> with <paramref name="environment"/> and its
    /// <paramref name="arguments"/> (<c>--rollback</c>, <c>--stop</c>; none deploys), passing on each line it prints to the
    /// context's progress. Throws <see cref="InstallerException"/> with the script's last words when it fails.
    /// </summary>
    public static async Task RunAsync(
        InstallContext context,
        WorkFolder folder,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        params IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(folder);
        var script = Path.Join(folder.Path, FileName);
        context.Machine.WriteAtomically(script, Text(), Modes.PrivateFolder);
        // Its settings are paths and choices, never a key, so the log may show them.
        context.Log.Write($"Running the deploy script{string.Concat(arguments.Select(argument => $" {argument}"))} with {string.Join(' ', environment.Select(setting => $"{setting.Key}={Quote(setting.Value)}"))}.");
        var result = await context.Machine.Commands.RunAsync(
            new CommandLine("bash", [script, .. arguments])
            {
                // Only the installer's settings: a variable the script reads that the person happened to have set (IMAGE_TAG,
                // READY_TIMEOUT_SECONDS, a proxy its checks would go through) never reaches it.
                Environment = environment,
                InheritEnvironment = false,

                // The script has its own limits: the pull, the pre-flight check, the Stop, the new controller's start.
                Timeout = null,
                OnOutputLine = line =>
                {
                    var redacted = context.Log.Redact(line);
                    // The command log keeps only its tail. Preserve deployment warnings even when later progress
                    // would push them out, including when no progress display is attached.
                    if (redacted.Contains("WARNING:", StringComparison.Ordinal))
                    {
                        context.Log.Write(redacted);
                    }

                    context.Progress?.Invoke(redacted);
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var said = LastWords(result);
            var words = said is null ? string.Empty : $": {context.Log.Redact(said)}";

            // 129, 130 and 143: a hangup, a Ctrl-C or a TERM ended it, through its EXIT trap.
            if (result.ExitCode is 129 or 130 or 143)
            {
                throw new InstallerException(
                    $"The deploy script was stopped (exit {result.ExitCode}){words}. When it is stopped while it replaces the controller, it "
                    + "puts the old one back. The install log has the script's output.",
                    InstallerExitCode.Cancelled);
            }

            throw new InstallerException(
                $"The deploy script stopped (exit {result.ExitCode}){words}. "
                + "When it fails after stopping a running controller, it puts that one back. The install log has the script's output.");
        }
    }

    private static string? LastWords(CommandResult result)
        => result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
            ?? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static string Quote(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"'{value}'" : value;
}
