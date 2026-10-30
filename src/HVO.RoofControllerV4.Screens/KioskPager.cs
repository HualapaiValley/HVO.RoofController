using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Bounded touch pages for lists and explanatory text. Pages are measured with the configured text and target sizes;
/// they never scale the contents or scroll the whole screen. Only the current page participates in focus navigation.
/// Previous, Next and the page count are shown only when the contents overflow one page; contents that fit have the
/// whole height and no page controls.
/// </summary>
public sealed class KioskPager : UserControl
{
    private readonly KioskMetrics _metrics;
    private readonly Grid _root;
    private readonly StackPanel _body;
    private readonly TextBlock _position;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Grid _bar;
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
        _bar = new Grid { Name = $"{name}-bar", ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = metrics.Gap, IsVisible = false };
        _bar.Children.Add(_previous);
        Grid.SetColumn(_position, 1);
        _bar.Children.Add(_position);
        Grid.SetColumn(_next, 2);
        _bar.Children.Add(_next);
        _root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        _root.Children.Add(_body);
        Grid.SetRow(_bar, 1);
        _root.Children.Add(_bar);
        Content = _root;
    }

    /// <summary>The number of measured pages.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The current zero-based page.</summary>
    public int CurrentPage => _current;

    /// <summary>True when the contents overflow one page, so Previous, Next and the page count are shown.</summary>
    public bool IsPaged => _pages.Count > 1;

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
            // Contents that fit on one page have the whole height; only overflowing contents give up the page controls' room.
            _pages = Paginate(availableSize.Width, Math.Max(_metrics.Font * 2, availableSize.Height));
            if (_pages.Count > 1)
                _pages = Paginate(availableSize.Width, Math.Max(_metrics.Font * 2, availableSize.Height - _metrics.Touch - _metrics.Gap));
            _bar.IsVisible = _pages.Count > 1;
            _root.RowSpacing = _bar.IsVisible ? _metrics.Gap : 0;
            _current = Math.Min(_current, _pages.Count - 1);
            Show();
        }
        return base.MeasureOverride(availableSize);
    }

    private List<List<Control>> Paginate(double width, double room)
    {
        // Every item is measured as it is drawn: attached, so its styles and a button's template apply. Detached, a
        // button has no template and measures as only its minimum size, however much its content needs.
        _body.Children.Clear();
        _body.Children.AddRange(_items);
        List<List<Control>> pages = [[]];
        var used = 0d;
        foreach (var item in ExpandText(_items, width, room))
        {
            item.Measure(new Size(width, double.PositiveInfinity));
            var height = item.DesiredSize.Height;
            if (used > 0 && used + _metrics.Gap + height > room)
            {
                pages.Add([]);
                used = 0;
            }
            pages[^1].Add(item);
            used += height + _metrics.Gap;
        }
        return pages;
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
            // Measure complete graphemes, including the continuation cue. Prefer a nearby whitespace boundary for
            // normal prose, but a single server token/newline-only/CJK paragraph must also have reachable pages.
            // Keep the source whitespace verbatim so concatenating the fragments recovers the complete message.
            var source = text.Text ?? string.Empty;
            var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToArray();
            var start = 0;
            while (start < boundaries.Length - 1)
            {
                var offset = boundaries[start];
                var rest = Piece(source[offset..]);
                if (Fits(rest))
                {
                    yield return rest;
                    break;
                }
                var low = start + 1;
                var high = boundaries.Length - 2;
                var end = start;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    if (Fits(Piece(source[offset..boundaries[middle]] + " (continued)")))
                    {
                        end = middle;
                        low = middle + 1;
                    }
                    else high = middle - 1;
                }
                // At supported geometry at least one full-size grapheme and cue fit. If a temporarily tiny viewport
                // cannot hold the cue, the page counter still supplies continuation; never split the grapheme.
                var cue = " (continued)";
                if (end == start)
                {
                    end = start + 1;
                    cue = string.Empty;
                }
                else
                {
                    for (var wordEnd = end; wordEnd > start + (end - start) / 2; wordEnd--)
                    {
                        if (!char.IsWhiteSpace(source[boundaries[wordEnd - 1]])) continue;
                        if (!Fits(Piece(source[offset..boundaries[wordEnd]] + cue))) continue;
                        end = wordEnd;
                        break;
                    }
                }
                yield return Piece(source[offset..boundaries[end]] + cue);
                start = end;
            }

            bool Fits(TextBlock candidate)
            {
                candidate.Measure(new Size(width, double.PositiveInfinity));
                return candidate.DesiredSize.Height <= height;
            }

            TextBlock Piece(string value) => new()
            {
                Name = start == 0 ? text.Name : null,
                Text = value,
                FontFamily = text.FontFamily,
                FontSize = text.FontSize,
                FontStyle = text.FontStyle,
                FontWeight = text.FontWeight,
                FontStretch = text.FontStretch,
                LineHeight = text.LineHeight,
                LetterSpacing = text.LetterSpacing,
                TextAlignment = text.TextAlignment,
                FlowDirection = text.FlowDirection,
                Padding = text.Padding,
                Foreground = text.Foreground,
                VerticalAlignment = text.VerticalAlignment,
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
