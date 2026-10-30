using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Build identification and the controller's summary (#209), available even while locked or disconnected: the summary is
/// read with the kiosk's own key (or the app's session), and grants no controller permissions. Labelled cards for this
/// device, the controller and the controller's host (<see cref="KioskAboutInfo"/>), each value saying why when it is not
/// known. On the kiosk the cards are bounded pages, paged only when they overflow.
/// </summary>
public sealed class KioskAboutPage : UserControl
{
    private readonly KioskConsole _console;
    private readonly KioskSystemPanel _system;
    private readonly KioskMetrics _metrics;
    private readonly KioskPager _document;
    private string? _shown;

    public KioskAboutPage(KioskConsole console, KioskSystemPanel system, KioskMetrics metrics)
    {
        _console = console;
        _system = system;
        _metrics = metrics;
        _document = new KioskPager(metrics, "about-pages");
        Info = KioskAboutInfo.Describe(console.View, console.Wording, system.Self, system.Summary, system.SummaryProblem);
        system.Changed += () => Update(console.View);
        Update(console.View);
    }

    /// <summary>What About shows now.</summary>
    public KioskAboutInfo Info { get; private set; }

    public void Update(KioskView view)
    {
        Info = KioskAboutInfo.Describe(view, _console.Wording, _system.Self, _system.Summary, _system.SummaryProblem);
        // Rebuilt only when something shown changed, so a status heartbeat keeps the reader's page.
        var shown = string.Join('\n', new[] { Info.Device, Info.Controller, Info.Host }
            .SelectMany(section => section.Rows.Select(row => $"{row.Name}={row.Value}").Append(section.Note)));
        if (shown == _shown) return;
        _shown = shown;

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = _metrics.Gap * 2 };
        cards.Children.Add(Card(Info.Device));
        var controller = Card(Info.Controller);
        Grid.SetColumn(controller, 1);
        cards.Children.Add(controller);
        // The navigation names the page; the cards have the titles.
        var host = Card(Info.Host, tiles: true);
        if (_console.Wording.SignIn == KioskSignIn.Pin)
        {
            _document.SetItems([cards, host]);
            Content = _document;
        }
        else Content = new StackPanel { Spacing = _metrics.Gap * 2, Children = { cards, host } };
    }

    // A titled card: label and value rows (or, for tiles, a label over each value, three a row), then its note.
    private Border Card(KioskAboutSection section, bool tiles = false)
    {
        var panel = new StackPanel { Spacing = _metrics.Gap * 1.5 };
        panel.Children.Add(KioskTheme.Label(section.Title, _metrics.Font, KioskTheme.Accent, FontWeight.SemiBold));
        if (tiles)
        {
            var grid = new UniformGrid { Columns = Math.Min(3, section.Rows.Count), ColumnSpacing = _metrics.Gap * 3, RowSpacing = _metrics.Gap * 1.5 };
            foreach (var row in section.Rows)
                grid.Children.Add(new StackPanel { Spacing = _metrics.Gap / 2, Children = { Label(row), Value(row) } });
            panel.Children.Add(grid);
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = _metrics.Gap * 2, RowSpacing = _metrics.Gap };
            for (var i = 0; i < section.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = Label(section.Rows[i]);
                label.VerticalAlignment = VerticalAlignment.Top;
                label.Margin = new Thickness(0, (_metrics.Font - _metrics.Small) / 2, 0, 0);
                Grid.SetRow(label, i);
                grid.Children.Add(label);
                var value = Value(section.Rows[i]);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }
            panel.Children.Add(grid);
        }

        if (section.Note is { } note)
        {
            var text = KioskTheme.Label(note, _metrics.Small, KioskTheme.Muted);
            text.Name = $"{section.Name}-note";
            panel.Children.Add(text);
        }

        var card = KioskTheme.Card(panel, _metrics);
        card.Name = section.Name;
        return card;
    }

    private TextBlock Label(KioskAboutRow row) => KioskTheme.Label(row.Label, _metrics.Small, KioskTheme.Muted);

    private TextBlock Value(KioskAboutRow row)
    {
        var value = KioskTheme.Label(row.Value, _metrics.Font, row.IsKnown ? KioskTheme.Text : KioskTheme.Muted, row.IsKnown ? FontWeight.SemiBold : FontWeight.Normal);
        value.Name = row.Name;
        return value;
    }
}
