using Avalonia;
using Avalonia.Controls;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The header's layout: the title (its first child) on the left and the badges (its second) on the right. The title has
/// its whole width while the badges fit beside it on one line, and at least half the header when they do not; the badges
/// take what is left and wrap onto more lines in it. A title wider than its share is cut short by the title itself.
/// </summary>
internal sealed class KioskHeaderPanel : Panel
{
    private double _titleWidth;

    /// <summary>The room between the title and the badges.</summary>
    public double Spacing { get; init; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (title, badges) = (Children[0], Children[1]);
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        title.Measure(unbounded);
        badges.Measure(unbounded);
        var width = availableSize.Width;
        if (!double.IsFinite(width))
        {
            _titleWidth = title.DesiredSize.Width;
            return new Size(_titleWidth + Spacing + badges.DesiredSize.Width, Math.Max(title.DesiredSize.Height, badges.DesiredSize.Height));
        }

        _titleWidth = Math.Min(title.DesiredSize.Width, Math.Max(width / 2, width - Spacing - badges.DesiredSize.Width));
        title.Measure(new Size(_titleWidth, availableSize.Height));
        badges.Measure(new Size(Math.Max(0, width - _titleWidth - Spacing), availableSize.Height));
        return new Size(width, Math.Max(title.DesiredSize.Height, badges.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (title, badges) = (Children[0], Children[1]);
        var titleWidth = Math.Min(_titleWidth, finalSize.Width);
        title.Arrange(new Rect(0, 0, titleWidth, finalSize.Height));
        var left = Math.Min(finalSize.Width, titleWidth + Spacing);
        badges.Arrange(new Rect(left, 0, finalSize.Width - left, finalSize.Height));
        return finalSize;
    }
}
