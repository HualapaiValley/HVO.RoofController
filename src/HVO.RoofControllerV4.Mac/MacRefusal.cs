using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// What the Mac app shows when its settings are wrong, instead of the roof: why, and where the settings are. An app
/// opened from the Finder has nowhere else to say it.
/// </summary>
public static class MacRefusal
{
    /// <summary>The window's size.</summary>
    public static readonly Size WindowSize = new(760, 460);

    /// <summary>The page: the reason, the settings file, and Quit.</summary>
    /// <param name="reason">Why the app did not start.</param>
    /// <param name="settingsFile">The settings file.</param>
    /// <param name="quit">Closes the app.</param>
    public static Control Content(string reason, string settingsFile, Action quit)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(settingsFile);
        ArgumentNullException.ThrowIfNull(quit);
        var metrics = KioskMetrics.Desk;
        var file = new SelectableTextBlock
        {
            Name = "refusal-file",
            Text = settingsFile,
            FontSize = metrics.Font,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"),
            Foreground = KioskTheme.Text,
            TextWrapping = TextWrapping.Wrap
        };
        var quitButton = KioskTheme.TouchButton("Quit", "refusal-quit", metrics, quit);
        quitButton.HorizontalAlignment = HorizontalAlignment.Left;
        quitButton.MinWidth = metrics.Touch * 2;
        quitButton.IsDefault = true;
        quitButton.IsCancel = true;
        var reasonText = KioskTheme.Banner(reason, KioskNoticeLevel.Danger, metrics, "refusal-reason");
        return new ScrollViewer
        {
            Background = KioskTheme.Background,
            Content = new StackPanel
            {
                Margin = new Thickness(metrics.Gap * 4),
                Spacing = metrics.Gap * 2,
                Children =
                {
                    KioskTheme.Label("HVO Roof did not start", metrics.Large, weight: FontWeight.SemiBold),
                    reasonText,
                    KioskTheme.Label(File.Exists(settingsFile) ? "Its settings file is:" : "Its settings file, which does not exist yet, is:", metrics.Font, KioskTheme.Muted),
                    file,
                    KioskTheme.Label(
                        "The Mac app's page in the roof controller's docs (docs/mac.md) says what goes in it, and how to make the device key. Change it, then open the app again.",
                        metrics.Font,
                        KioskTheme.Muted),
                    quitButton
                }
            }
        };
    }

    /// <summary>Shows the page in a window until it is closed.</summary>
    public static void Show(string reason, string settingsFile)
    {
        RefusalApp.Reason = reason;
        RefusalApp.SettingsFile = settingsFile;
        AppBuilder.Configure<RefusalApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime([]);
    }

    private sealed class RefusalApp : Application
    {
        public static string Reason { get; set; } = string.Empty;

        public static string SettingsFile { get; set; } = string.Empty;

        public override void Initialize()
        {
            Name = Program.Title;
            KioskTheme.Apply(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new Window
                {
                    Title = Program.Title,
                    Width = WindowSize.Width,
                    Height = WindowSize.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = KioskTheme.Background
                };
                window.Content = Content(Reason, SettingsFile, window.Close);
                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
