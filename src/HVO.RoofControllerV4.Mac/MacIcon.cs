using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// The app's icon, drawn in code in HVO Dark: a roll-off observatory under the stars, its roof rolled aside and the
/// telescope up. The bundle's AppIcon.icns holds it at each size macOS uses (MacIconTests draws them, and
/// bundle/make-icns.py packs them), so changing it here and running those again changes the icon.
/// </summary>
public sealed class MacIcon : Control
{
    /// <summary>The square the drawing is made in, scaled to the control.</summary>
    public const double Canvas = 1024;

    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(RoofUiPalette.Surface), 0), new GradientStop(Color.Parse(RoofUiPalette.Background), 1) }
    }.ToImmutable();

    private static readonly IPen Edge = new ImmutablePen(Color.Parse(RoofUiPalette.Border).ToUInt32(), 10);
    private static readonly IPen Rail = new ImmutablePen(Color.Parse(RoofUiPalette.Muted).ToUInt32(), 14, lineCap: PenLineCap.Round);

    // Stars: centre and radius on the canvas.
    private static readonly (double X, double Y, double R)[] Stars =
    [
        (250, 250, 14), (380, 190, 9), (540, 270, 12), (700, 205, 16), (790, 330, 9), (300, 380, 8), (620, 380, 7)
    ];

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / Canvas;
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));

        // The body, on macOS's icon grid: 824 of 1024, with rounded corners.
        context.DrawRectangle(Sky, Edge, new RoundedRect(new Rect(100, 100, 824, 824), 185));
        foreach (var (x, y, r) in Stars)
        {
            context.DrawEllipse(KioskTheme.Text, null, new Point(x, y), r, r);
        }

        // The telescope on its pier, pointing up through the open roof.
        var tube = new StreamGeometry();
        using (var draw = tube.Open())
        {
            draw.BeginFigure(new Point(345, 610), true);
            draw.LineTo(new Point(520, 400));
            draw.LineTo(new Point(575, 446));
            draw.LineTo(new Point(400, 656));
            draw.EndFigure(true);
        }

        context.DrawGeometry(KioskTheme.Text, null, tube);
        context.DrawRectangle(KioskTheme.Muted, null, new Rect(380, 620, 36, 110));

        // The building, its roof rolled off to the right along the rails.
        context.DrawRectangle(KioskTheme.Badge, Edge, new Rect(220, 700, 400, 150));
        context.DrawLine(Rail, new Point(200, 700), new Point(860, 700));
        var roof = new StreamGeometry();
        using (var draw = roof.Open())
        {
            draw.BeginFigure(new Point(560, 700), true);
            draw.LineTo(new Point(600, 600));
            draw.LineTo(new Point(820, 600));
            draw.LineTo(new Point(860, 700));
            draw.EndFigure(true);
        }

        context.DrawGeometry(KioskTheme.Accent, null, roof);
    }
}
