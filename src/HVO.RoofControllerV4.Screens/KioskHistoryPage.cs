using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The History page (#206, #208), offered only while someone has unlocked the kiosk (or signed in to the app): their
/// details as the controller knows them, read-only (<see cref="KioskPerson"/>), and their own most recent roof commands,
/// newest first, each with its time, command, the controller's answer, the roof after it and where it came from, in the
/// words every client uses (<see cref="RoofHistoryText"/>); then how many there are and how long the controller keeps
/// them, and where the person changes their names (never here). Its heading and Read again stay at the top; the rest is
/// bounded pages, paged only when they overflow.
/// </summary>
public sealed class KioskHistoryPage : UserControl
{
    private readonly KioskHistoryPanel _panel;
    private readonly KioskMetrics _metrics;
    private readonly KioskPager _document;
    private readonly Button _reload;
    private double _timeWidth;

    public KioskHistoryPage(KioskHistoryPanel panel, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(metrics);
        _panel = panel;
        _metrics = metrics;
        _document = new KioskPager(metrics, "history-pages");
        _reload = KioskTheme.TouchButton("Read again", "history-reload", metrics, () => _ = panel.LoadAsync());
        var title = KioskTheme.Label("Your roof commands", metrics.Large, KioskTheme.Accent, FontWeight.SemiBold);
        title.Name = "history-title";
        title.VerticalAlignment = VerticalAlignment.Center;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap * 2 };
        heading.Children.Add(title);
        Grid.SetColumn(_reload, 1);
        heading.Children.Add(_reload);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = metrics.Gap };
        page.Children.Add(heading);
        Grid.SetRow(_document, 1);
        page.Children.Add(_document);
        Content = page;
        panel.Changed += Update;
        Update();
    }

    /// <summary>Shows the panel as it is now.</summary>
    public void Update()
    {
        _reload.IsEnabled = !_panel.Reading;
        var items = new List<Control>();
        if (_panel.Reading || _panel.Problem is not null)
        {
            var line = KioskTheme.Label(
                _panel.Problem ?? "Reading your roof commands…",
                _metrics.Font,
                _panel.Problem is null ? KioskTheme.Muted : KioskTheme.Tone(KioskNoticeLevel.Warning));
            line.Name = "history-status";
            items.Add(line);
        }

        // The commands come first after who the person is; what only explains them is at the end.
        var person = _panel.Person is { } caller ? KioskPerson.Describe(caller) : null;
        if (person is not null)
        {
            var card = KioskTheme.Card(person.Content(_metrics, wide: true), _metrics);
            card.Name = "history-person";
            items.Add(card);
        }

        if (_panel.Page is { } history)
        {
            if (history.Entries.Count == 0)
            {
                var empty = KioskTheme.Label(RoofHistoryText.Empty, _metrics.Font, KioskTheme.Muted);
                empty.Name = "history-empty";
                items.Add(empty);
            }

            for (var i = 0; i < history.Entries.Count; i++)
            {
                items.Add(Entry(history.Entries[i], i));
            }

            var range = KioskTheme.Label(
                $"Newest first: {RoofHistoryText.DescribeRange(history)} {RoofHistoryText.DescribeRetention(history)}",
                _metrics.Small,
                KioskTheme.Muted);
            range.Name = "history-range";
            items.Add(range);
        }

        if (person?.Note is { } note)
        {
            var names = KioskTheme.Label(note, _metrics.Small, KioskTheme.Muted);
            names.Name = "history-names";
            items.Add(names);
        }

        _document.SetItems(items, reset: true);
    }

    // One command: its time in a column of its own, then the command, the answer (a refusal in the warning colour), the
    // roof after it and where it came from, wrapping onto a second line when they do not fit beside one another.
    private Border Entry(RoofCommandHistoryEntry entry, int index)
    {
        var time = KioskTheme.Label(RoofStatusText.Time(entry.TimestampUtc), _metrics.Small, KioskTheme.Muted);
        time.Name = $"history-time-{index}";
        time.Width = TimeWidth();
        var command = KioskTheme.Label(RoofHistoryText.DescribeCommand(entry), _metrics.Font, weight: FontWeight.SemiBold);
        command.Name = $"history-command-{index}";
        var result = KioskTheme.Label(
            RoofHistoryText.DescribeResult(entry),
            _metrics.Font,
            entry.Accepted ? KioskTheme.Brush(RoofUiPalette.Success) : KioskTheme.Tone(KioskNoticeLevel.Warning));
        result.Name = $"history-result-{index}";
        var after = KioskTheme.Label($"Roof after: {RoofHistoryText.DescribeOutcome(entry)}", _metrics.Small, KioskTheme.Muted);
        var source = KioskTheme.Label($"From {RoofHistoryText.DescribeSource(entry)}", _metrics.Small, KioskTheme.Muted);
        // The time lines up with the command's first line, as About's labels do with their values.
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, (_metrics.Font - _metrics.Small) / 2, 0, 0);
        var what = new WrapPanel { ItemSpacing = _metrics.Gap * 2, LineSpacing = _metrics.Gap / 2, Children = { command, result, after, source } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = _metrics.Gap * 2 };
        row.Children.Add(time);
        Grid.SetColumn(what, 1);
        row.Children.Add(what);
        return new Border
        {
            Name = $"history-entry-{index}",
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, _metrics.Gap / 2, 0, _metrics.Gap),
            Child = row
        };
    }

    // Wide enough for any time, so the times line up down the page.
    private double TimeWidth()
    {
        if (_timeWidth > 0)
        {
            return _timeWidth;
        }

        var sample = KioskTheme.Label(RoofStatusText.Time(new DateTimeOffset(2088, 12, 28, 20, 08, 58, TimeSpan.Zero)), _metrics.Small);
        sample.TextWrapping = TextWrapping.NoWrap;
        sample.Measure(Size.Infinity);
        _timeWidth = Math.Ceiling(sample.DesiredSize.Width) + 2;
        return _timeWidth;
    }
}
