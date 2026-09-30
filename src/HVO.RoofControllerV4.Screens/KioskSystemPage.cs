using Avalonia.Controls;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's system page: the controller's health checks and readiness, and for an admin its version, host and
/// resource use, and Restart. Rebuilt when the panel changes, which only a touch here or its answer does.
/// </summary>
public sealed class KioskSystemPage : UserControl
{
    private readonly KioskSystemPanel _panel;
    private readonly KioskConsole _console;
    private readonly KioskMetrics _metrics;

    public KioskSystemPage(KioskSystemPanel panel, KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _panel = panel;
        _console = console;
        _metrics = metrics;
        panel.Changed += Update;
        Update();
    }

    /// <summary>Shows the panel as it is now.</summary>
    public void Update()
    {
        var page = new StackPanel { Spacing = _metrics.Gap * 2 };
        page.Children.Add(KioskTheme.Label("System", _metrics.Large, weight: FontWeight.SemiBold));
        foreach (var status in KioskPageParts.Status(_panel.Busy, _panel.Message, _metrics))
        {
            page.Children.Add(status);
        }

        if (_panel.Question is { } question)
        {
            page.Children.Add(KioskPageParts.Question(question, _metrics, _panel.Dismiss));
            Content = page;
            return;
        }

        var actions = new List<Control>
        {
            KioskTheme.TouchButton("Read again", "system-reload", _metrics, () => _ = _panel.LoadAsync()),
            KioskTheme.TouchButton("Readiness", "system-readiness", _metrics, () => _ = _panel.CheckReadinessAsync())
        };
        if (_console.View.IsAdmin)
        {
            var restart = KioskTheme.TouchButton("Restart", "system-restart", _metrics, () => _ = _panel.AskRestartAsync());
            KioskTheme.Colour(restart, RoofUiPalette.CloseButton, RoofUiPalette.CloseButtonText);
            actions.Add(restart);
        }

        page.Children.Add(KioskPageParts.Row(_metrics, [.. actions]));

        if (_panel.Information.Count > 0)
        {
            page.Children.Add(KioskTheme.Card(Rows(_panel.Information), _metrics));
        }
        else if (_panel.Health is not null && !_console.View.IsAdmin)
        {
            page.Children.Add(KioskTheme.Label(KioskSystemPanel.InformationNeedsAdmin, _metrics.Font, KioskTheme.MutedWeak));
        }

        if (_panel.Health is { } health)
        {
            var checks = new StackPanel { Spacing = _metrics.Gap };
            checks.Children.Add(KioskTheme.Label($"Health: {health}", _metrics.Font, weight: FontWeight.SemiBold));
            checks.Children.Add(Rows(_panel.Checks.Select(check => (check.Name, string.IsNullOrEmpty(check.Description) ? check.Status : $"{check.Status}: {check.Description}"))));
            page.Children.Add(KioskTheme.Card(checks, _metrics));
        }

        Content = page;
    }

    private Grid Rows(IEnumerable<(string Label, string Value)> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = _metrics.Gap * 3, RowSpacing = _metrics.Gap / 2 };
        var row = 0;
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = KioskTheme.Label(label, _metrics.Small, KioskTheme.Muted);
            var text = KioskTheme.Label(value, _metrics.Small);
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            row++;
        }

        return grid;
    }
}
