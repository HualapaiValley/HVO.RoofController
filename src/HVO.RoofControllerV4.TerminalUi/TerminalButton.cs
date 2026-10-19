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
        DrawComplete += (_, _) => DrawButton();
    }

    /// <summary>Whether default-action indicators must also be ASCII.</summary>
    public bool Ascii { get; set; }

    private string Caption => IsDefault ? $"{(Ascii ? ">" : "►")} {Text} {(Ascii ? "<" : "◄")}" : Text;

    /// <inheritdoc/>
    protected override void OnTextChanged()
    {
        base.OnTextChanged();
        Width = Dim.Func(_ => TerminalCells.Width(Caption) + 4);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingText(DrawContext? context) => true;

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context) => true;

    private void DrawButton()
    {
        if (GetDriver() is not { } driver) return;
        var saved = driver.Clip;
        var frame = FrameToScreen();
        try
        {
            driver.Clip = TerminalDrawing.Clip(this, frame);
            driver.SetAttribute(GetAttributeForRole(!Enabled ? VisualRole.Disabled : HasFocus ? VisualRole.Focus : VisualRole.Normal));
            driver.Move(frame.X, frame.Y);
            driver.AddStr($"[ {Caption} ]");
        }
        finally { driver.Clip = saved; }
    }
}
