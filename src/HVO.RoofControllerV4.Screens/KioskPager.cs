using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Bounded touch pages for lists and explanatory text. Pages are measured with the configured text and target sizes;
/// they never scale the contents or scroll the whole screen. Only the current page participates in focus navigation.
/// </summary>
public sealed class KioskPager : UserControl
{
    private readonly KioskMetrics _metrics;
    private readonly Grid _root;
    private readonly StackPanel _body;
    private readonly TextBlock _position;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly List<Control> _items = [];
    private List<List<Control>> _pages = [];
    private Size _measured;
    private int _current;

    /// <summary>Creates a bounded, keyboard and touch accessible document.</summary>
    public KioskPager(KioskMetrics metrics, string name)
    {
        _metrics = metrics;
        Name = name;
        _body = new StackPanel { Name = $"{name}-body", Spacing = metrics.Gap };
        _previous = KioskTheme.TouchButton("Previous", $"{name}-previous", metrics, () => Move(-1));
        _next = KioskTheme.TouchButton("Next", $"{name}-next", metrics, () => Move(1));
        _position = KioskTheme.Label(string.Empty, metrics.Small, KioskTheme.Muted);
        _position.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = metrics.Gap };
        bar.Children.Add(_previous);
        Grid.SetColumn(_position, 1);
        bar.Children.Add(_position);
        Grid.SetColumn(_next, 2);
        bar.Children.Add(_next);
        _root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = metrics.Gap };
        _root.Children.Add(_body);
        Grid.SetRow(bar, 1);
        _root.Children.Add(bar);
        Content = _root;
    }

    /// <summary>The number of measured pages.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The current zero-based page.</summary>
    public int CurrentPage => _current;

    /// <summary>Replaces the document. Items retain their actions; the current page is preserved unless reset is requested.</summary>
    public void SetItems(IEnumerable<Control> items, bool reset = false)
    {
        _items.Clear();
        _items.AddRange(items);
        _measured = default;
        if (reset) _current = 0;
        InvalidateMeasure();
    }

    /// <summary>Moves to a measured page, keeping the navigation controls in place.</summary>
    public void Move(int delta)
    {
        _current = Math.Clamp(_current + delta, 0, Math.Max(0, _pages.Count - 1));
        Show();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height) && availableSize != _measured)
        {
            _measured = availableSize;
            var room = Math.Max(_metrics.Font * 2, availableSize.Height - _metrics.Touch - _metrics.Gap);
            _pages = [[]];
            var used = 0d;
            foreach (var item in ExpandText(_items, availableSize.Width, room))
            {
                item.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                var height = item.DesiredSize.Height;
                if (used > 0 && used + _metrics.Gap + height > room)
                {
                    _pages.Add([]);
                    used = 0;
                }
                _pages[^1].Add(item);
                used += height + _metrics.Gap;
            }
            _current = Math.Min(_current, _pages.Count - 1);
            Show();
        }
        return base.MeasureOverride(availableSize);
    }

    private IEnumerable<Control> ExpandText(IEnumerable<Control> items, double width, double height)
    {
        foreach (var item in items)
        {
            item.Measure(new Size(width, double.PositiveInfinity));
            if (item is not TextBlock text || item.DesiredSize.Height <= height)
            {
                yield return item;
                continue;
            }
            // Arbitrary server messages can exceed one page. Keep complete words, explicit continuation and all text.
            var words = (text.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var part = string.Empty;
            var index = 0;
            foreach (var word in words)
            {
                var candidate = part.Length == 0 ? word : $"{part} {word}";
                var probe = Piece(candidate);
                probe.Measure(new Size(width, double.PositiveInfinity));
                if (part.Length > 0 && probe.DesiredSize.Height > height - _metrics.Font * 1.5)
                {
                    yield return Piece(part + " (continued)");
                    index++;
                    part = word;
                }
                else part = candidate;
            }
            if (part.Length > 0) yield return Piece(part);

            TextBlock Piece(string value) => new()
            {
                Name = index == 0 ? text.Name : null,
                Text = value,
                FontSize = text.FontSize,
                FontWeight = text.FontWeight,
                Foreground = text.Foreground,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private void Show()
    {
        _body.Children.Clear();
        if (_pages.Count > 0)
            foreach (var item in _pages[_current]) _body.Children.Add(item);
        _position.Text = $"Page {_current + 1} / {Math.Max(1, _pages.Count)}";
        _previous.IsEnabled = _current > 0;
        _next.IsEnabled = _current + 1 < _pages.Count;
    }
}
