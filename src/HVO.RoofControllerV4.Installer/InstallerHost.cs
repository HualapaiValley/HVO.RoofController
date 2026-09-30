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

    /// <summary>This process, on the machine it runs on.</summary>
    public static InstallerHost System() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        Machine = InstallerMachine.Current(),
        IsInteractive = !Console.IsInputRedirected && !Console.IsOutputRedirected
    };
}
