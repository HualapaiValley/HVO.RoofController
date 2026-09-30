using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's Avalonia application: HVO Dark, and the shell the host hands it. On the touchscreen the shell is the
/// single view; in a window (for development) it fills the window.
/// </summary>
public sealed class KioskApp : Application
{
    /// <summary>Builds the shell once Avalonia is up. Set by the host before the application starts.</summary>
    public static Func<KioskShell>? CreateShell { get; set; }

    /// <summary>The window's size when the kiosk runs in a window.</summary>
    public static Size WindowSize { get; set; } = new(1280, 720);

    /// <summary>The shell shown, once the application has started.</summary>
    public KioskShell? Shell { get; private set; }

    public override void Initialize() => KioskTheme.Apply(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (CreateShell is { } create)
        {
            Shell = create();
            switch (ApplicationLifetime)
            {
                case ISingleViewApplicationLifetime single:
                    single.MainView = Shell;
                    break;
                case IClassicDesktopStyleApplicationLifetime desktop:
                    desktop.MainWindow = new Window
                    {
                        Title = "HVO roof kiosk",
                        Width = WindowSize.Width,
                        Height = WindowSize.Height,
                        Background = KioskTheme.Background,
                        Content = Shell
                    };
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
