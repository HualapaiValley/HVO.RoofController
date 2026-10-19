using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>A bracketed ordinary button, with the same keyboard/command behavior in every character mode.</summary>
public sealed class TerminalButton : Button
{
    /// <summary>Creates a shadow-free button whose label keeps its accessible action text.</summary>
    public TerminalButton()
    {
        NoDecorations = true;
        NoPadding = true;
        ShadowStyle = ShadowStyles.None;
        Width = Dim.Func(_ => TerminalCells.Width(Text) + 4);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingText(DrawContext? context)
    {
        SetAttributeForRole(!Enabled ? VisualRole.Disabled : HasFocus ? VisualRole.Focus : VisualRole.Normal);
        Move(0, 0);
        AddStr($"[ {Text} ]");
        return true;
    }
}
