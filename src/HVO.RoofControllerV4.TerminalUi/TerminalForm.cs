using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>A bounded ordinary form: Tab reveals the focused control; Page Up/Down scroll explanations and fields.</summary>
public class TerminalForm : View
{
    /// <summary>Creates a form with a visible overflow indicator and keyboard scrolling.</summary>
    public TerminalForm()
    {
        CanFocus = true;
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        AddCommand(Command.PageUp, () => Scroll(-Math.Max(1, Viewport.Height - 1)));
        AddCommand(Command.PageDown, () => Scroll(Math.Max(1, Viewport.Height - 1)));
        KeyBindings.Add(Key.PageUp, Command.PageUp);
        KeyBindings.Add(Key.PageDown, Command.PageDown);
    }

    /// <inheritdoc/>
    protected override void OnSubViewAdded(View view)
    {
        base.OnSubViewAdded(view);
        view.HasFocusChanged += (_, _) =>
        {
            if (view.HasFocus && view.Frame.Height < Viewport.Height)
            {
                var y = Viewport.Y;
                if (view.Frame.Y < y) y = view.Frame.Y;
                if (view.Frame.Bottom > y + Viewport.Height) y = view.Frame.Bottom - Viewport.Height;
                Viewport = Viewport with { Y = Math.Max(0, y) };
            }
        };
    }

    /// <inheritdoc/>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        // A page with pinned footer fields (review/save) lays those out against the viewport, not a previous content size.
        if (SubViews.Any(view => view.Y is PosAnchorEnd)) SetContentSize(null);
        base.OnSubViewLayout(args);
    }

    /// <inheritdoc/>
    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);
        if (SubViews.Any(view => view.Y is PosAnchorEnd)) return;
        var bottom = SubViews.Where(view => view.Visible).Select(view => view.Frame.Bottom).DefaultIfEmpty(0).Max();
        var size = new Size(Viewport.Width, Math.Max(Viewport.Height, bottom));
        if (GetContentSize() != size) SetContentSize(size);
    }

    private bool Scroll(int amount)
    {
        Viewport = Viewport with { Y = Math.Clamp(Viewport.Y + amount, 0, Math.Max(0, GetContentSize().Height - Viewport.Height)) };
        return true;
    }
}
