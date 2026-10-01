using System.Text;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Installer.Machine;
using Terminal.Gui.App;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// What the installer needs from the process it runs in: its output, the machine, the clock and the terminal.
/// <see cref="System"/> is the real one; tests give a fake machine and drive the wizard themselves.
/// </summary>
public sealed class InstallerHost
{
    /// <summary>The plan, progress and the Done text.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Why the installer refused or failed.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>The machine to install on.</summary>
    public required InstallerMachine Machine { get; init; }

    /// <summary>True when a person is at a terminal: the wizard can run.</summary>
    public bool IsInteractive { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The installer's version, with its commit: 4.0.0+0123abcd….</summary>
    public string Version { get; init; } = RoofProductVersion.Of(typeof(InstallerHost).Assembly);

    /// <summary>Creates and initialises the Terminal.Gui application for the wizard. Tests give one with a virtual clock.</summary>
    public Func<IApplication> CreateApplication { get; init; } = () =>
    {
        var app = Application.Create();
        app.Init();
        return app;
    };

    /// <summary>Runs the wizard until it closes. Tests replace this and drive the window themselves.</summary>
    public Action<IApplication, IRunnable> RunApplication { get; init; } = (app, window) => app.Run(window);

    /// <summary>
    /// Asks the person at the terminal for a secret (a certificate's password), named by its argument, without showing
    /// what they type. Null when no one can be asked: the input is not a terminal.
    /// </summary>
    public Func<string, string?> ReadSecret { get; init; } = _ => null;

    /// <summary>
    /// Asks the person at the terminal a yes-or-no question, its argument: true for yes, false for anything else. Null
    /// when no one can be asked: the input is not a terminal.
    /// </summary>
    public Func<string, bool?> Confirm { get; init; } = _ => null;

    /// <summary>
    /// Asks the person at the terminal for a line, its argument the question (the machine's name, which
    /// <c>uninstall --purge</c> asks to be typed): what they typed, or null when no one can be asked.
    /// </summary>
    public Func<string, string?> Ask { get; init; } = _ => null;

    /// <summary>This process, on the machine it runs on.</summary>
    public static InstallerHost System() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        Machine = InstallerMachine.Current(),
        IsInteractive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
        ReadSecret = ReadSecretFromTerminal,
        Confirm = ConfirmAtTerminal,
        Ask = AskAtTerminal
    };

    private static string? AskAtTerminal(string question)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write($"{question} ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    // y or yes goes ahead; anything else, Enter alone, or the end of the input does not.
    private static bool? ConfirmAtTerminal(string question)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write($"{question} [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    // Each key read without echo; Backspace takes one back, Ctrl+C stops the installer before anything changes.
    private static string? ReadSecretFromTerminal(string what)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write($"Type {what} (it is not shown), then Enter: ");
        var controlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        var secret = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    return secret.ToString();
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new InstallerException("Stopped: nothing was changed.", InstallerExitCode.Cancelled);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    secret.Length = Math.Max(0, secret.Length - 1);
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    secret.Append(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = controlC;
            Console.Error.WriteLine();
        }
    }
}
