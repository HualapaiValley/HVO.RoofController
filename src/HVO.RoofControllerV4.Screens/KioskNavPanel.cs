using Avalonia;
using Avalonia.Controls;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The bottom navigation's layout: its visible buttons left to right, each at least <see cref="WideWidth"/> (the
/// comfortable width, <see cref="KioskMetrics.NavWidth"/>) while they all fit on one row. When they would not (the Alpaca
/// page offered beside History, Change PIN and Terminal, #220), each takes only the width its words need, never less than
/// the touch target its own minimum width keeps, so the bar stays one row and the pages keep their height. Only when even
/// that does not fit do they wrap onto another row, as a wrap panel's do.
/// </summary>
internal sealed class KioskNavPanel : Panel
{
    private readonly List<(Control Child, double Width)> _laid = [];

    /// <summary>The room between buttons, and between rows.</summary>
    public double Spacing { get; init; }

    /// <summary>The width every button has while they all fit on one row at it.</summary>
    public double WideWidth { get; init; }

    /// <summary>True while the buttons are narrowed to their words to fit on one row.</summary>
    public bool IsCompact { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(child => child.IsVisible).ToList();
        foreach (var child in visible)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        }

        var wide = visible.Select(child => Math.Max(child.DesiredSize.Width, WideWidth)).ToList();
        IsCompact = double.IsFinite(availableSize.Width) && Row(wide) > availableSize.Width;
        _laid.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            _laid.Add((visible[i], IsCompact ? visible[i].DesiredSize.Width : wide[i]));
        }

        var (width, height) = (0d, 0d);
        foreach (var line in Lines(availableSize.Width))
        {
            width = Math.Max(width, Row(line.Select(item => item.Width)));
            height += (height > 0 ? Spacing : 0) + line.Max(item => item.Child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var line in Lines(finalSize.Width))
        {
            var height = line.Max(item => item.Child.DesiredSize.Height);
            var x = 0d;
            foreach (var (child, width) in line)
            {
                child.Arrange(new Rect(x, y, width, height));
                x += width + Spacing;
            }

            y += height + Spacing;
        }

        return finalSize;
    }

    private double Row(IEnumerable<double> widths)
    {
        var all = widths.ToList();
        return all.Sum() + (Spacing * Math.Max(0, all.Count - 1));
    }

    // The buttons in rows, each row as many as fit in the width (at least one).
    private IEnumerable<List<(Control Child, double Width)>> Lines(double room)
    {
        var line = new List<(Control Child, double Width)>();
        var used = 0d;
        foreach (var item in _laid)
        {
            var needed = line.Count == 0 ? item.Width : used + Spacing + item.Width;
            if (line.Count > 0 && double.IsFinite(room) && needed > room + 0.5)
            {
                yield return line;
                line = [];
                needed = item.Width;
            }

            line.Add(item);
            used = needed;
        }

        if (line.Count > 0)
        {
            yield return line;
        }
    }
}
