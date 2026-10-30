using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Controls side by side, in equal columns, as one item of a bounded page (<see cref="KioskPager"/>). When the row is
/// taller than a whole page (a person's card with the longest names, two a row), the pager shows its controls one under
/// another instead, each the page's width, so nothing is drawn over the page controls; at another size it is a row again.
/// </summary>
public sealed class KioskPagerRow : UniformGrid
{
    private readonly Control[] _cells;

    public KioskPagerRow(IEnumerable<Control> cells, double spacing)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        Columns = Math.Max(1, _cells.Length);
        ColumnSpacing = spacing;
        Join();
    }

    /// <summary>The controls, in order, whether side by side or one under another.</summary>
    public IReadOnlyList<Control> Cells => _cells;

    /// <summary>Puts the controls side by side again, from wherever they are.</summary>
    internal void Join()
    {
        if (Children.Count == _cells.Length)
        {
            return;
        }

        foreach (var cell in _cells)
        {
            if (cell.Parent is Panel parent)
            {
                parent.Children.Remove(cell);
            }
        }

        Children.Clear();
        Children.AddRange(_cells);
    }

    /// <summary>Takes the controls out of the row, for the pager to show one under another.</summary>
    internal IReadOnlyList<Control> Split()
    {
        Children.Clear();
        return _cells;
    }
}
