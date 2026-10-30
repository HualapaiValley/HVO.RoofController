using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>The shared Sky semantic theme, exact ANSI16 fallback, terminal defaults and ASCII controls.</summary>
public sealed class RoofUiTheme
{
    private readonly Scheme _stopSent;
    private readonly Scheme _stopAcknowledged;
    private readonly Scheme _stopUnverified;
    private readonly Scheme _stopFailed;

    private RoofUiTheme(TerminalCapabilities capabilities)
    {
        Capabilities = capabilities;
        Base = new(Of(Role.Text, Role.Background))
        {
            Focus = Of(Role.Text, Role.AccentStrong, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold),
            HotNormal = Of(Role.Accent, Role.Background, plain: TextStyle.Underline),
            HotFocus = Of(Role.Text, Role.AccentStrong, TextStyle.Bold | TextStyle.Underline, TextStyle.Reverse | TextStyle.Bold | TextStyle.Underline),
            Active = Of(Role.Text, Role.Surface, TextStyle.Bold, TextStyle.Bold),
            HotActive = Of(Role.Accent, Role.Surface, TextStyle.Bold, TextStyle.Bold | TextStyle.Underline),
            Highlight = Of(Role.Text, Role.Badge, plain: TextStyle.Bold),
            Editable = Of(Role.Text, Role.Surface, plain: TextStyle.Underline),
            ReadOnly = Of(Role.Muted, Role.Background),
            Disabled = Of(Role.MutedWeak, Role.Badge, TextStyle.Faint, TextStyle.Faint)
        };
        Frame = new(Base) { Normal = Of(Role.Border, Role.Background) };
        WindowFrame = new(Base) { Normal = Of(Role.Muted, Role.Background) };
        Header = new(Base) { Normal = Of(Role.Text, Role.Surface, TextStyle.Bold, TextStyle.Bold) };
        Key = new(Base) { Normal = Of(Role.Accent, Role.Badge, TextStyle.Bold, TextStyle.Bold) };
        KeyName = new(Base) { Normal = Of(Role.Muted, Role.Badge) };
        Stop = ButtonScheme(Role.DangerText, Role.DangerBackground, filled: true);
        StopMoving = ButtonScheme(Role.Background, Role.Danger, filled: true);
        Open = ButtonScheme(Role.SuccessText, Role.SuccessBackground, filled: false);
        Close = ButtonScheme(Role.SuccessText, Role.SuccessBackground, filled: false);
        // A running button is not pressable (the roof is moving), so it keeps its bright colours while disabled; a
        // disabled one is neutral, never a faded green.
        MotionRunning = ButtonScheme(Role.Background, Role.Success, filled: true, keepWhenDisabled: true);
        MotionDisabled = new(Base) { Disabled = Of(Role.Muted, Role.Surface, plain: TextStyle.Faint) };
        Outcome = ButtonScheme(Role.Text, Role.Badge, filled: false);
        Warning = new(Base) { Normal = Of(Role.WarningText, Role.WarningBackground, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
        Danger = new(Base) { Normal = Of(Role.DangerText, Role.DangerBackground, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
        Panel = new(Base)
        {
            Normal = Of(Role.Text, Role.Surface),
            HotNormal = Of(Role.Accent, Role.Surface, plain: TextStyle.Underline),
            ReadOnly = Of(Role.Muted, Role.Surface),
            Editable = Of(Role.Text, Role.Background, plain: TextStyle.Underline)
        };
        PanelFrame = new(Panel) { Normal = Of(Role.Accent, Role.Surface, plain: TextStyle.Bold) };
        PanelError = new(Panel) { Normal = Of(Role.Danger, Role.Surface, TextStyle.Bold, TextStyle.Bold) };
        _stopSent = new(Base) { Normal = Of(Role.Info, Role.Background) };
        _stopAcknowledged = new(Base) { Normal = Of(Role.Success, Role.Background) };
        _stopUnverified = new(Base) { Normal = Of(Role.Warning, Role.Background, TextStyle.Bold, TextStyle.Bold) };
        _stopFailed = new(Base) { Normal = Of(Role.Danger, Role.Background, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
    }

    /// <summary>HVO Dark, in colour.</summary>
    public static RoofUiTheme HvoDark { get; } = new(new(TerminalColours.TrueColour, false));

    /// <summary>
    /// The interface without colour: the terminal's own foreground and background, with bold, underline and reverse
    /// video where HVO Dark has a colour. The focused control and Stop are in reverse video, an input field is
    /// underlined, a disabled control is faint, and a stale status or an error is bold in reverse video. Buttons have no
    /// shadow, which Terminal.Gui draws in black.
    /// </summary>
    public static RoofUiTheme NoColour { get; } = new(new(TerminalColours.None, false));

    /// <summary>The window and everything in it without a scheme of its own.</summary>
    public Scheme Base { get; }

    /// <summary>Frame lines, dimmer than the text, as the web UI's panel borders are.</summary>
    public Scheme Frame { get; }

    /// <summary>
    /// The window's frame, in the secondary text colour: Terminal.Gui draws a window's title in its frame's colour, and
    /// the title is the controller address, which must be readable.
    /// </summary>
    public Scheme WindowFrame { get; }

    /// <summary>Who is signed in and whether the status is live: the web UI's navigation bar.</summary>
    public Scheme Header { get; }

    /// <summary>A key in the key bar.</summary>
    public Scheme Key { get; }

    /// <summary>What a key in the key bar does.</summary>
    public Scheme KeyName { get; }

    /// <summary>The Stop button: red, as on the web UI, in every state; focus underlines it.</summary>
    public Scheme Stop { get; }

    /// <summary>
    /// The Stop button while the roof moves, or was last seen moving (<see cref="RoofActionStates.IsStopActive"/>): bright
    /// red. <see cref="Stop"/> is the idle, dimmer red. Both are always enabled.
    /// </summary>
    public Scheme StopMoving { get; }

    /// <summary>The Open button while offered: the success pair.</summary>
    public Scheme Open { get; }

    /// <summary>The Close button while offered: the success pair.</summary>
    public Scheme Close { get; }

    /// <summary>Open or Close while the roof moves its way: bright green, kept while the button is not pressable.</summary>
    public Scheme MotionRunning { get; }

    /// <summary>Open or Close while not offered: neutral and faint without colour; the reason is shown under the buttons.</summary>
    public Scheme MotionDisabled { get; }

    /// <summary>A finished task's headline (the installer's "The install finished."): bold text on the raised surface.</summary>
    public Scheme Outcome { get; }

    /// <summary>A stale status, and anything else the operator should notice before acting.</summary>
    public Scheme Warning { get; }

    /// <summary>An error or a refusal.</summary>
    public Scheme Danger { get; }

    /// <summary>A prompt over the page (a sign-in, a confirmation), on the raised surface of the web UI's dialogs.</summary>
    public Scheme Panel { get; }

    /// <summary>The frame of a prompt, in the accent colour so the prompt stands out from the page.</summary>
    public Scheme PanelFrame { get; }

    /// <summary>An error inside a prompt: the colour only, on the prompt.</summary>
    public Scheme PanelError { get; }

    /// <summary><see cref="NoColour"/> when <c>NO_COLOR</c> is set and not empty, as no-color.org asks; otherwise <see cref="HvoDark"/>.</summary>
    public static RoofUiTheme For(Func<string, string?> environment)
    {
        var capabilities = TerminalCapabilities.Detect(environment);
        return capabilities == HvoDark.Capabilities ? HvoDark : capabilities == NoColour.Capabilities ? NoColour : new(capabilities);
    }

    /// <summary>The Stop result, coloured as the web UI colours it.</summary>
    public Scheme ForStop(RoofStopOutcome outcome) => outcome switch
    {
        RoofStopOutcome.Sent => _stopSent,
        RoofStopOutcome.Acknowledged => _stopAcknowledged,
        RoofStopOutcome.RelayUnverified => _stopUnverified,
        RoofStopOutcome.Failed => _stopFailed,
        _ => Base
    };

    /// <summary>The Stop button's scheme: <see cref="StopMoving"/> while <paramref name="active"/>, else <see cref="Stop"/>.</summary>
    public Scheme StopButtonFor(bool active) => active ? StopMoving : Stop;

    /// <summary>The scheme of Open (<see cref="RoofMotionDirection.Opening"/>) or Close in <paramref name="state"/> (<see cref="RoofActionStates.ForMotion"/>).</summary>
    public Scheme ForMotion(RoofActionState state, RoofMotionDirection direction) => state switch
    {
        RoofActionState.Running => MotionRunning,
        RoofActionState.Disabled => MotionDisabled,
        _ => direction == RoofMotionDirection.Opening ? Open : Close
    };

    /// <summary>The key bar at the foot of a window: each key in the accent colour, and what it does, on the navigation bar's grey.</summary>
    public View CreateKeyBar(IEnumerable<(string Key, string Name)> keys)
    {
        var bar = new View { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        bar.SetScheme(KeyName);
        View? previous = null;
        foreach (var (key, name) in keys)
        {
            var keyLabel = new Label { Text = key, X = previous is null ? 0 : Pos.Right(previous) + 2, Y = 0 };
            keyLabel.SetScheme(Key);
            var nameLabel = new Label { Text = name, X = Pos.Right(keyLabel) + 1, Y = 0 };
            bar.Add(keyLabel, nameLabel);
            previous = nameLabel;
        }

        return bar;
    }

    /// <summary>Draws the frame of <paramref name="view"/> in <paramref name="scheme"/> (<see cref="Frame"/> by default).</summary>
    public void SetFrame(View view, Scheme? scheme = null)
    {
        if (view.Border?.View is { } border)
        {
            border.SetScheme(scheme ?? Frame);
            if (Capabilities.Ascii && !_frames.TryGetValue(view, out _))
            {
                _frames.Add(view, new object());
                view.BorderStyle = LineStyle.None;
                view.Border!.Thickness = new Thickness(1);
                var top = new Label { X = 0, Y = 0, Height = 1 };
                var bottom = new Label { X = 0, Height = 1 };
                var left = new Label { X = 0, Y = 1, Width = 1 };
                var right = new Label { Y = 1, Width = 1 };
                foreach (var line in new[] { top, bottom, left, right }) { line.SetScheme(scheme ?? Frame); border.Add(line); }
                border.SubViewLayout += (_, _) =>
                {
                    var frame = view.Border!.FrameToScreen();
                    var width = frame.Width;
                    var height = frame.Height;
                    if (width < 2 || height < 2) return;
                    var title = "+-" + TerminalCells.Take(view.Title, Math.Max(0, width - 4));
                    top.Text = title + new string('-', Math.Max(0, width - TerminalCells.Width(title) - 1)) + "+";
                    bottom.Text = "+" + new string('-', width - 2) + "+";
                    top.Width = bottom.Width = width;
                    bottom.Y = height - 1;
                    left.Text = right.Text = string.Join('\n', Enumerable.Repeat("|", height - 2));
                    left.Height = right.Height = height - 2;
                    right.X = width - 1;
                };
            }
        }
    }

    /// <summary>A button in this theme: in <paramref name="scheme"/> if given, and with no shadow when there is no colour.</summary>
    public Button Styled(Button button, Scheme? scheme = null)
    {
        if (scheme is not null)
        {
            button.SetScheme(scheme);
        }

        button.ShadowStyle = ShadowStyles.None;
        button.NoDecorations = button is TerminalButton;
        if (button is TerminalButton ordinary)
        {
            ordinary.Ascii = Capabilities.Ascii;
            ordinary.Width = Dim.Func(_ => TerminalCells.Width(ordinary.Text) + (ordinary.IsDefault ? 8 : 4));
        }

        return button;
    }

    // A coloured button keeps its colour when focused, so Stop is always the red button: focus underlines the text.
    // Without colour, Stop is always in reverse video (filled) and the others are bold, in reverse video when focused.
    private Scheme ButtonScheme(Role text, Role background, bool filled, bool keepWhenDisabled = false)
    {
        var plain = filled ? TextStyle.Reverse | TextStyle.Bold : TextStyle.Bold;
        var plainFocus = TextStyle.Reverse | TextStyle.Bold | (filled ? TextStyle.Underline : TextStyle.None);
        var scheme = new Scheme(Base)
        {
            Normal = Of(text, background, TextStyle.Bold, plain),
            Focus = Of(text, background, TextStyle.Bold | TextStyle.Underline, plainFocus),
            HotNormal = Of(text, background, TextStyle.Bold, plain),
            HotFocus = Of(text, background, TextStyle.Bold | TextStyle.Underline, plainFocus),
            Highlight = Of(text, background, TextStyle.Bold, plain)
        };
        return keepWhenDisabled ? new Scheme(scheme) { Disabled = scheme.Normal } : scheme;
    }

    // The colours and style, or without colour, the terminal's own colours and the plain style.
    private Attribute Of(Role foreground, Role background, TextStyle style = TextStyle.None, TextStyle plain = TextStyle.None)
        => Capabilities.Colours switch
        {
            TerminalColours.TrueColour => new(new Color(Hex(foreground)), new Color(Hex(background)), style),
            TerminalColours.Ansi16 => new(new Color(Ansi(foreground)), new Color(Ansi(background)), style),
            _ => new(Color.None, Color.None, plain)
        };

    /// <summary>The detected protocol and character repertoire.</summary>
    public TerminalCapabilities Capabilities { get; }

    /// <summary>Configures exact ANSI16 emission and the toolkit's glyphs before either app creates controls.</summary>
    public void Prepare(IApplication app)
    {
        if (app.Driver is { } driver) driver.Force16Colors = Capabilities.Colours == TerminalColours.Ansi16;
        // Glyphs is a read-only facade in Terminal.Gui 2.5. Set its immutable settings record;
        // reflecting writable facade properties silently changes nothing, including password masks.
        GlyphSettings.Current = Capabilities.Ascii ? AsciiGlyphs : UnicodeGlyphs;
    }

    private static readonly GlyphSettings UnicodeGlyphs = GlyphSettings.Default;
    private static readonly GlyphSettings AsciiGlyphs = CreateAsciiGlyphs();

    private static GlyphSettings CreateAsciiGlyphs()
    {
        var settings = UnicodeGlyphs with { };
        foreach (var property in typeof(GlyphSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.PropertyType == typeof(Rune) && property.CanWrite))
        {
            var original = (Rune)property.GetValue(settings)!;
            if (original.Value <= 127) continue;
            var name = property.Name;
            var glyph = original.Value is >= 0x2500 and <= 0x257f
                ? name.StartsWith("HLine") ? '-' : name.StartsWith("VLine") ? '|' : '+'
                : name == "Stipple" ? '.'
                : name.Contains("MeterSegment") ? '#'
                : name.StartsWith("CheckState") || name is "Selected" or "UnSelected" ? ' '
                : name.Contains("Left") ? '<' : name.Contains("Right") ? '>'
                : name.Contains("Up") ? '^' : name.Contains("Down") ? 'v' : '*';
            property.SetValue(settings, new Rune(glyph));
        }
        return settings;
    }

    /// <summary>Styles an editable or read-only field, with a visible focus cue in every protocol.</summary>
    public void StyleField(TextField field)
    {
        var ordinary = new Scheme(Panel) { Normal = Of(Role.Text, Role.Badge, Capabilities.Colours == TerminalColours.TrueColour ? TextStyle.None : TextStyle.Underline, TextStyle.Underline) };
        var focused = new Scheme(ordinary) { Normal = Base.Focus };
        if (field.ContextMenu is { } menu)
        {
            menu.SetScheme(Panel);
            SetFrame(menu, PanelFrame);
            if (menu.Root is { } root)
            {
                root.SetScheme(Panel);
                SetFrame(root, PanelFrame);
                foreach (var child in root.SubViews) child.SetScheme(Panel);
            }
        }
        field.SetScheme(field.HasFocus ? focused : ordinary);
        field.HasFocusChanged += (_, _) => field.SetScheme(field.HasFocus ? focused : ordinary);
    }

    /// <summary>Applies shared field/list/control styling to ordinary views, including dynamically added forms.</summary>
    private readonly ConditionalWeakTable<CheckBox, object> _checks = new();
    private readonly ConditionalWeakTable<View, object> _frames = new();
    private readonly ConditionalWeakTable<View, object> _focusStyled = new();

    public void Style(View view)
    {
        if (Capabilities.Ascii && view is CheckBox check && !_checks.TryGetValue(check, out _))
        {
            _checks.Add(check, new object());
            check.DrawingContent += (_, args) =>
            {
                var mark = check.Value == CheckState.Checked ? (check.RadioStyle ? "(*)" : "[x]") : (check.RadioStyle ? "( )" : "[ ]");
                check.SetAttributeForRole(!check.Enabled ? VisualRole.Disabled : check.HasFocus ? VisualRole.Focus : VisualRole.Normal);
                check.AddStr(0, 0, TerminalCells.PadRight(TerminalCells.Take($"{mark} {check.Text}", check.Viewport.Width), check.Viewport.Width));
                args.Cancel = true;
            };
            check.Width = Dim.Func(_ => TerminalCells.Width(check.Text) + 4);
        }
        if (view is TextField field && !_focusStyled.TryGetValue(field, out _))
        {
            _focusStyled.Add(field, new object());
            StyleField(field);
        }
        if (view is ListView list) list.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        foreach (var child in view.SubViews) Style(child);
    }

    private enum Role { Background, Surface, Border, Text, Muted, Accent, AccentStrong, Success, Warning, Danger, Info, SuccessBackground, SuccessText, WarningBackground, WarningText, DangerBackground, DangerText, InfoBackground, InfoText, Badge, MutedWeak }
    private static string Hex(Role role) => role switch
    {
        Role.Background => RoofUiPalette.Background,
        Role.Surface => RoofUiPalette.Surface,
        Role.Border => RoofUiPalette.Border,
        Role.Text => RoofUiPalette.Text,
        Role.Muted => RoofUiPalette.Muted,
        Role.Accent => RoofUiPalette.Accent,
        Role.AccentStrong => RoofUiPalette.AccentStrong,
        Role.Success => RoofUiPalette.Success,
        Role.Warning => RoofUiPalette.Warning,
        Role.Danger => RoofUiPalette.Danger,
        Role.Info => RoofUiPalette.Info,
        Role.SuccessBackground => RoofUiPalette.SuccessBackground,
        Role.SuccessText => RoofUiPalette.SuccessText,
        Role.WarningBackground => RoofUiPalette.WarningBackground,
        Role.WarningText => RoofUiPalette.WarningText,
        Role.DangerBackground => RoofUiPalette.DangerBackground,
        Role.DangerText => RoofUiPalette.DangerText,
        Role.InfoBackground => RoofUiPalette.InfoBackground,
        Role.InfoText => RoofUiPalette.InfoText,
        Role.Badge => RoofUiPalette.Badge,
        Role.MutedWeak => RoofUiPalette.MutedWeak,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static ColorName16 Ansi(Role role) => role switch
    {
        Role.Background => ColorName16.Black,
        Role.Surface => ColorName16.Black,
        Role.Border => ColorName16.DarkGray,
        Role.Text => ColorName16.White,
        Role.Muted => ColorName16.Gray,
        Role.Accent => ColorName16.BrightCyan,
        Role.AccentStrong => ColorName16.Blue,
        Role.Success => ColorName16.BrightGreen,
        Role.Warning => ColorName16.BrightYellow,
        Role.Danger => ColorName16.BrightRed,
        Role.Info => ColorName16.BrightCyan,
        Role.SuccessBackground => ColorName16.Green,
        Role.SuccessText => ColorName16.Black,
        Role.WarningBackground => ColorName16.Yellow,
        Role.WarningText => ColorName16.Black,
        Role.DangerBackground => ColorName16.Red,
        Role.DangerText => ColorName16.White,
        Role.InfoBackground => ColorName16.Cyan,
        Role.InfoText => ColorName16.Black,
        Role.Badge => ColorName16.Black,
        Role.MutedWeak => ColorName16.Gray,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
