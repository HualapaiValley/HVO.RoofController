using Terminal.Gui.Drawing;
using Terminal.Gui.App;
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
    protected override void OnInitializingShadowStyle(ValueChangingEventArgs<ShadowStyles?> args)
    {
        // Suppress allocation in Button's base constructor: setting ShadowStyle=None later leaves its
        // right/bottom margin allocated, which clips the final bracket and consumes a second row.
        args.Handled = true;
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
    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);
        // Button's undecorated Text content is narrower than our bracketed caption. Keep its draw clip at
        // the actual viewport width without replacing the toolkit's occlusion clip during painting.
        if (GetContentSize() != Viewport.Size) SetContentSize(Viewport.Size);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingText(DrawContext? context) => true;

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (GetDriver() is not { } driver) return true;
        var frame = FrameToScreen();
        // Use the existing clip: replacing it lets an underlying page paint over a prompt. Driver cells avoid
        // Button's text formatter clipping the trailing bracket to the undecorated Text width.
        driver.SetAttribute(GetAttributeForRole(!Enabled ? VisualRole.Disabled : HasFocus ? VisualRole.Focus : VisualRole.Normal));
        driver.Move(frame.X, frame.Y);
        driver.AddStr($"[ {Caption} ]");
        return true;
    }
}
