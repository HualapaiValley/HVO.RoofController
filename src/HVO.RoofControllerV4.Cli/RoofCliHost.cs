using HVO.RoofControllerV4.Client;
using Terminal.Gui.App;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// What <c>hvo-roof</c> needs from the machine it runs on: its output, the environment, a way to ask for a secret, and
/// the connection to the controller. <see cref="System"/> is the real one; tests give their own, pointing the
/// connection at an in-process controller.
/// </summary>
public sealed class RoofCliHost
{
    /// <summary>Normal output: results, and JSON with <c>--json</c>.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Errors and prompts, so <see cref="Out"/> stays clean for scripts.</summary>
    public required TextWriter Error { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Reads one line of input for <paramref name="prompt"/>, without echoing it when <paramref name="secret"/> is true.
    /// Returns null at the end of the input. The prompt goes to <see cref="Error"/>; with input redirected, no prompt is
    /// shown and the line is read as it is.
    /// </summary>
    public required Func<string, bool, string?> ReadLine { get; init; }

    /// <summary>True when a person can answer a prompt (the input is a terminal).</summary>
    public bool IsInteractive { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>For tests: the HTTP handler of every connection to the controller.</summary>
    public Func<HttpMessageHandler>? CreateHandler { get; init; }

    /// <summary>For tests: how the status feed opens its WebSocket.</summary>
    public RoofWebSocketFactory? WebSocketFactory { get; init; }

    /// <summary>For tests: the status feed's reconnect and staleness timings.</summary>
    public RoofStatusFeedOptions? StatusFeed { get; init; }

    /// <summary>
    /// Creates and initialises the Terminal.Gui application for <c>hvo-roof ui</c>. Tests give one with a virtual clock.
    /// </summary>
    public Func<IApplication> CreateApplication { get; init; } = () =>
    {
        var app = Application.Create();
        app.Init();
        return app;
    };

    /// <summary>
    /// Runs the interface until it closes. The real host calls <see cref="IApplication.Run(IRunnable, Func{Exception, bool})"/>;
    /// tests replace this and drive the window themselves.
    /// </summary>
    public Action<IApplication, IRunnable> RunApplication { get; init; } = (app, window) => app.Run(window);

    /// <summary>The console of this process.</summary>
    public static RoofCliHost System() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        IsInteractive = !Console.IsInputRedirected,
        ReadLine = ReadConsoleLine
    };

    private static string? ReadConsoleLine(string prompt, bool secret)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write(prompt);
        if (!secret)
        {
            return Console.ReadLine();
        }

        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                Console.Error.WriteLine();
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
