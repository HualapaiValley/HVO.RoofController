using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The Avalonia application of the kiosk and the Mac app: HVO Dark, and the shell the host hands it. On the touchscreen
/// the shell is the single view; in a window (the Mac app, or the kiosk for development) it fills the window.
/// </summary>
public sealed class KioskApp : Application
{
    /// <summary>Builds the shell once Avalonia is up. Set by the host before the application starts.</summary>
    public static Func<KioskShell>? CreateShell { get; set; }

    /// <summary>The window's size when the shell is shown in a window.</summary>
    public static Size WindowSize { get; set; } = new(1280, 720);

    /// <summary>The smallest the window may be made, so that Stop and the pages still fit. None: any size.</summary>
    public static Size? MinimumWindowSize { get; set; }

    /// <summary>The window's title, and the application's name (macOS shows it in the menu bar).</summary>
    public static string Title { get; set; } = "HVO roof kiosk";

    /// <summary>The shell shown, once the application has started.</summary>
    public KioskShell? Shell { get; private set; }

    public override void Initialize()
    {
        Name = Title;
        KioskTheme.Apply(this);
    }

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
                        Title = Title,
                        Width = WindowSize.Width,
                        Height = WindowSize.Height,
                        MinWidth = MinimumWindowSize?.Width ?? 0,
                        MinHeight = MinimumWindowSize?.Height ?? 0,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Background = KioskTheme.Background,
                        Content = Shell
                    };
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
