using System.Drawing;
using Region = Terminal.Gui.Drawing.Region;
using Terminal.Gui.ViewBase;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>Bounds explicit cell drawing to the containing work area, including scrolled forms.</summary>
internal static class TerminalDrawing
{
    public static Region Clip(View view, Rectangle frame)
    {
        for (var parent = view.SuperView; parent is not null; parent = parent.SuperView)
            frame = Rectangle.Intersect(frame, parent.ViewportToScreen());
        return new(frame);
    }
}
