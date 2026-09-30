using HVO.RoofControllerV4.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>
/// The terminal programs (<c>hvo-roof ui</c> and the installer) in HVO Dark, the web UI's theme
/// (<see cref="RoofUiPalette"/>): text on the page's dark background, the focused control in the accent blue, Stop yellow,
/// Open green and Close red, and a stale status or an error in the theme's warning and danger colours. Terminal.Gui draws
/// the colours as the terminal allows: true colour, or the nearest of 256 or 16 colours. With <c>NO_COLOR</c> set
/// (https://no-color.org) the interface is drawn in the terminal's own colours instead (<see cref="NoColour"/>);
/// Terminal.Gui alone would still draw 16 colours.
/// </summary>
public sealed class RoofUiTheme
{
    private readonly bool _colour;
    private readonly Scheme _stopSent;
    private readonly Scheme _stopAcknowledged;
    private readonly Scheme _stopUnverified;
    private readonly Scheme _stopFailed;

    private RoofUiTheme(bool colour)
    {
        _colour = colour;
        Base = new(Of(RoofUiPalette.Text, RoofUiPalette.Background))
        {
            Focus = Of(RoofUiPalette.Text, RoofUiPalette.AccentStrong, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold),
            HotNormal = Of(RoofUiPalette.Accent, RoofUiPalette.Background, plain: TextStyle.Underline),
            HotFocus = Of(RoofUiPalette.Text, RoofUiPalette.AccentStrong, TextStyle.Bold | TextStyle.Underline, TextStyle.Reverse | TextStyle.Bold | TextStyle.Underline),
            Active = Of(RoofUiPalette.Text, RoofUiPalette.Surface, TextStyle.Bold, TextStyle.Bold),
            HotActive = Of(RoofUiPalette.Accent, RoofUiPalette.Surface, TextStyle.Bold, TextStyle.Bold | TextStyle.Underline),
            Highlight = Of(RoofUiPalette.Text, RoofUiPalette.Badge, plain: TextStyle.Bold),
            Editable = Of(RoofUiPalette.Text, RoofUiPalette.Surface, plain: TextStyle.Underline),
            ReadOnly = Of(RoofUiPalette.Muted, RoofUiPalette.Background),
            Disabled = Of(RoofUiPalette.MutedWeak, RoofUiPalette.Badge, TextStyle.Faint, TextStyle.Faint)
        };
        Frame = new(Base) { Normal = Of(RoofUiPalette.Border, RoofUiPalette.Background) };
        WindowFrame = new(Base) { Normal = Of(RoofUiPalette.Muted, RoofUiPalette.Background) };
        Header = new(Base) { Normal = Of(RoofUiPalette.Text, RoofUiPalette.Surface, TextStyle.Bold, TextStyle.Bold) };
        Key = new(Base) { Normal = Of(RoofUiPalette.Accent, RoofUiPalette.Badge, TextStyle.Bold, TextStyle.Bold) };
        KeyName = new(Base) { Normal = Of(RoofUiPalette.Muted, RoofUiPalette.Badge) };
        Stop = ButtonScheme(RoofUiPalette.StopButtonText, RoofUiPalette.StopButton, filled: true);
        Open = ButtonScheme(RoofUiPalette.OpenButtonText, RoofUiPalette.OpenButton, filled: false);
        Close = ButtonScheme(RoofUiPalette.CloseButtonText, RoofUiPalette.CloseButton, filled: false);
        Warning = new(Base) { Normal = Of(RoofUiPalette.WarningText, RoofUiPalette.WarningBackground, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
        Danger = new(Base) { Normal = Of(RoofUiPalette.DangerText, RoofUiPalette.DangerBackground, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
        Panel = new(Base)
        {
            Normal = Of(RoofUiPalette.Text, RoofUiPalette.Surface),
            HotNormal = Of(RoofUiPalette.Accent, RoofUiPalette.Surface, plain: TextStyle.Underline),
            ReadOnly = Of(RoofUiPalette.Muted, RoofUiPalette.Surface),
            Editable = Of(RoofUiPalette.Text, RoofUiPalette.Background, plain: TextStyle.Underline)
        };
        PanelFrame = new(Panel) { Normal = Of(RoofUiPalette.Accent, RoofUiPalette.Surface, plain: TextStyle.Bold) };
        PanelError = new(Panel) { Normal = Of(RoofUiPalette.Danger, RoofUiPalette.Surface, TextStyle.Bold, TextStyle.Bold) };
        _stopSent = new(Base) { Normal = Of(RoofUiPalette.InfoText, RoofUiPalette.Background) };
        _stopAcknowledged = new(Base) { Normal = Of(RoofUiPalette.SuccessText, RoofUiPalette.Background) };
        _stopUnverified = new(Base) { Normal = Of(RoofUiPalette.WarningText, RoofUiPalette.Background, TextStyle.Bold, TextStyle.Bold) };
        _stopFailed = new(Base) { Normal = Of(RoofUiPalette.DangerText, RoofUiPalette.Background, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
    }

    /// <summary>HVO Dark, in colour.</summary>
    public static RoofUiTheme HvoDark { get; } = new(colour: true);

    /// <summary>
    /// The interface without colour: the terminal's own foreground and background, with bold, underline and reverse
    /// video where HVO Dark has a colour. The focused control and Stop are in reverse video, an input field is
    /// underlined, a disabled control is faint, and a stale status or an error is bold in reverse video. Buttons have no
    /// shadow, which Terminal.Gui draws in black.
    /// </summary>
    public static RoofUiTheme NoColour { get; } = new(colour: false);

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

    /// <summary>The Stop button: yellow, as on the web UI, in every state; focus underlines it.</summary>
    public Scheme Stop { get; }

    /// <summary>The Open button.</summary>
    public Scheme Open { get; }

    /// <summary>The Close button.</summary>
    public Scheme Close { get; }

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
        => string.IsNullOrEmpty(environment("NO_COLOR")) ? HvoDark : NoColour;

    /// <summary>The Stop result, coloured as the web UI colours it.</summary>
    public Scheme ForStop(RoofStopOutcome outcome) => outcome switch
    {
        RoofStopOutcome.Sent => _stopSent,
        RoofStopOutcome.Acknowledged => _stopAcknowledged,
        RoofStopOutcome.RelayUnverified => _stopUnverified,
        RoofStopOutcome.Failed => _stopFailed,
        _ => Base
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
        }
    }

    /// <summary>A button in this theme: in <paramref name="scheme"/> if given, and with no shadow when there is no colour.</summary>
    public Button Styled(Button button, Scheme? scheme = null)
    {
        if (scheme is not null)
        {
            button.SetScheme(scheme);
        }

        if (!_colour)
        {
            button.ShadowStyle = ShadowStyles.None;
        }

        return button;
    }

    // A coloured button keeps its colour when focused, so Stop is always the yellow button: focus underlines the text.
    // Without colour, Stop is always in reverse video (filled) and the others are bold, in reverse video when focused.
    private Scheme ButtonScheme(string text, string background, bool filled)
    {
        var plain = filled ? TextStyle.Reverse | TextStyle.Bold : TextStyle.Bold;
        var plainFocus = TextStyle.Reverse | TextStyle.Bold | (filled ? TextStyle.Underline : TextStyle.None);
        return new(Base)
        {
            Normal = Of(text, background, TextStyle.Bold, plain),
            Focus = Of(text, background, TextStyle.Bold | TextStyle.Underline, plainFocus),
            HotNormal = Of(text, background, TextStyle.Bold, plain),
            HotFocus = Of(text, background, TextStyle.Bold | TextStyle.Underline, plainFocus),
            Highlight = Of(text, background, TextStyle.Bold, plain)
        };
    }

    // The colours and style, or without colour, the terminal's own colours and the plain style.
    private Attribute Of(string foreground, string background, TextStyle style = TextStyle.None, TextStyle plain = TextStyle.None)
        => _colour ? new(new Color(foreground), new Color(background), style) : new(Color.None, Color.None, plain);
}
