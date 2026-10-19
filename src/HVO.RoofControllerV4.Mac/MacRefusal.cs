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
        var document = new ScrollViewer
        {
            Name = "refusal-document",
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = KioskTheme.Card(new StackPanel
            {
                Spacing = metrics.Gap * 2,
                Children =
                {
                    reasonText,
                    KioskTheme.Label(File.Exists(settingsFile) ? "Settings file" : "Create this settings file", metrics.Font, KioskTheme.Accent, FontWeight.SemiBold),
                    file,
                    KioskTheme.Label("Correct the connection, device key or certificate setting shown above, then open the app again. The Mac app guide (docs/mac.md) describes each setting and recovery step.", metrics.Font, KioskTheme.Muted)
                }
            }, metrics)
        };
        var header = new StackPanel { Spacing = metrics.Gap, Children =
        {
            KioskTheme.Label("HVO Roof did not start", metrics.Large, weight: FontWeight.SemiBold),
            KioskTheme.Label("Connection setup needs attention", metrics.Font, KioskTheme.Muted)
        }};
        var page = new Grid { Background = KioskTheme.Background, Margin = new Thickness(metrics.Gap * 4), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = metrics.Gap * 2 };
        page.Children.Add(header);
        Grid.SetRow(document, 1);
        page.Children.Add(document);
        Grid.SetRow(quitButton, 2);
        page.Children.Add(quitButton);
        return page;
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
