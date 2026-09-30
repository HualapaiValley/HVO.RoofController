using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// HVO Dark on the kiosk: the web UI's colours (<see cref="RoofUiPalette"/>) as brushes, Fluent's controls recoloured to
/// them, and the helpers the kiosk's screens build their text, banners and buttons with.
/// </summary>
public static class KioskTheme
{
    public static IBrush Background { get; } = Brush(RoofUiPalette.Background);

    public static IBrush Text { get; } = Brush(RoofUiPalette.Text);

    public static IBrush Muted { get; } = Brush(RoofUiPalette.Muted);

    public static IBrush MutedWeak { get; } = Brush(RoofUiPalette.MutedWeak);

    public static IBrush Surface { get; } = Brush(RoofUiPalette.Surface);

    /// <summary>Frame lines.</summary>
    public static IBrush Frame { get; } = Brush(RoofUiPalette.Border);

    public static IBrush Badge { get; } = Brush(RoofUiPalette.Badge);

    public static IBrush Accent { get; } = Brush(RoofUiPalette.Accent);

    public static IBrush AccentStrong { get; } = Brush(RoofUiPalette.AccentStrong);

    public static IBrush FocusRing { get; } = Brush(RoofUiPalette.FocusRing);

    /// <summary>Solid black: the blank screen.</summary>
    public static IBrush Black { get; } = Brush("#000000");

    /// <summary>Makes an application HVO Dark: Fluent's dark variant with the palette's colours.</summary>
    public static void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.RequestedThemeVariant = ThemeVariant.Dark;
        application.Styles.Add(new FluentTheme());
        var resources = application.Resources;
        SetButton(resources, RoofUiPalette.Badge, RoofUiPalette.Text, RoofUiPalette.Border);
        resources["TextControlBackground"] = Surface;
        resources["TextControlForeground"] = Text;
        resources["TextControlBorderBrush"] = Frame;
        resources["SystemControlFocusVisualPrimaryBrush"] = FocusRing;
        resources["ScrollBarThumbFill"] = Frame;
    }

    /// <summary>An immutable brush of a <c>#rrggbb</c> colour.</summary>
    public static IBrush Brush(string colour) => new ImmutableSolidColorBrush(Color.Parse(colour));

    /// <summary>The text colour and background of a notice, a banner or a message at <paramref name="level"/>.</summary>
    public static (IBrush Foreground, IBrush Background, IBrush Edge) Colours(KioskNoticeLevel level) => level switch
    {
        KioskNoticeLevel.Danger => (Brush(RoofUiPalette.DangerText), Brush(RoofUiPalette.DangerBackground), Brush(RoofUiPalette.Danger)),
        KioskNoticeLevel.Warning => (Brush(RoofUiPalette.WarningText), Brush(RoofUiPalette.WarningBackground), Brush(RoofUiPalette.Warning)),
        _ => (Brush(RoofUiPalette.InfoText), Brush(RoofUiPalette.InfoBackground), Brush(RoofUiPalette.Info))
    };

    /// <summary>The colours of a success badge: a live status.</summary>
    public static (IBrush Foreground, IBrush Background, IBrush Edge) SuccessColours { get; }
        = (Brush(RoofUiPalette.SuccessText), Brush(RoofUiPalette.SuccessBackground), Brush(RoofUiPalette.Success));

    /// <summary>Colours a button in every state: the colour it has stays while it is pressed, a bit lighter.</summary>
    public static void Colour(Button button, string background, string foreground, string? border = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        SetButton(button.Resources, background, foreground, border ?? background);
    }

    /// <summary>Wrapped text in the kiosk's text colour.</summary>
    public static TextBlock Label(string text, double fontSize, IBrush? foreground = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = weight,
        Foreground = foreground ?? Text,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A banner: wrapped text on a coloured panel with a stronger edge on the left.</summary>
    public static Border Banner(string text, KioskNoticeLevel level, KioskMetrics metrics, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var (foreground, background, edge) = Colours(level);
        return new Border
        {
            Name = name,
            Background = background,
            BorderBrush = edge,
            BorderThickness = new Thickness(metrics.Gap / 2, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(metrics.Gap * 1.5, metrics.Gap),
            Child = Label(text, metrics.Font, foreground)
        };
    }

    /// <summary>
    /// The class of a text cut short with an ellipsis on purpose, because its whole text is shown elsewhere (or, for a
    /// value being typed, because its end is what matters). Any other text cut short is a fault.
    /// </summary>
    public const string Abbreviated = "abbreviated";

    /// <summary>
    /// A badge: short text on a coloured pill. On a pill no wider than <paramref name="maxWidth"/>, a text too long for
    /// it ends in an ellipsis before <paramref name="kept"/>, which is always shown whole; the caller shows the whole
    /// text elsewhere.
    /// </summary>
    public static Border Pill(
        string text,
        (IBrush Foreground, IBrush Background, IBrush Edge) colours,
        KioskMetrics metrics,
        string? name = null,
        double maxWidth = double.PositiveInfinity,
        string? kept = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        TextBlock Piece(string piece) => new() { Text = piece, FontSize = metrics.Small, Foreground = colours.Foreground, FontWeight = FontWeight.SemiBold };
        var label = Piece(text);
        Control child = label;
        if (double.IsFinite(maxWidth))
        {
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Classes.Add(Abbreviated);
        }

        if (kept is not null)
        {
            var end = Piece(kept);
            var line = new DockPanel();
            DockPanel.SetDock(end, Dock.Right);
            line.Children.Add(end);
            line.Children.Add(label);
            child = line;
        }

        return new Border
        {
            Name = name,
            Background = colours.Background,
            BorderBrush = colours.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(metrics.Font),
            Padding = new Thickness(metrics.Gap * 1.5, metrics.Gap / 2),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = maxWidth,
            Child = child
        };
    }

    /// <summary>A panel with a frame: a card of the page.</summary>
    public static Border Card(Control child, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new Border
        {
            Background = Surface,
            BorderBrush = Frame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(metrics.Gap * 2),
            Child = child
        };
    }

    /// <summary>
    /// A button at least the kiosk's touch target in both directions, with wrapped text. <paramref name="name"/> is how
    /// the tests find it.
    /// </summary>
    public static Button TouchButton(string text, string name, KioskMetrics metrics, Action? click = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var button = new Button
        {
            Name = name,
            MinWidth = metrics.Touch,
            MinHeight = metrics.Touch,
            Padding = new Thickness(metrics.Gap * 2, metrics.Gap),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = metrics.Font,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }
        };
        if (click is not null)
        {
            button.Click += (_, _) => click();
        }

        return button;
    }

    /// <summary>The text on a button made by <see cref="TouchButton"/>.</summary>
    public static string GetText(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        return button.Content is TextBlock { Text: { } text } ? text : button.Content?.ToString() ?? string.Empty;
    }

    /// <summary>Changes the text on a button made by <see cref="TouchButton"/>.</summary>
    public static void SetText(Button button, string text)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (button.Content is TextBlock block)
        {
            block.Text = text;
        }
        else
        {
            button.Content = text;
        }
    }

    private static void SetButton(IResourceDictionary resources, string background, string foreground, string border)
    {
        var normal = Color.Parse(background);
        var lighter = Blend(normal, Colors.White, 0.18);
        var darker = Blend(normal, Colors.Black, 0.25);
        var disabled = Blend(normal, Color.Parse(RoofUiPalette.Background), 0.6);
        var text = Color.Parse(foreground);
        var edge = Color.Parse(border);
        resources["ButtonBackground"] = new ImmutableSolidColorBrush(normal);
        resources["ButtonBackgroundPointerOver"] = new ImmutableSolidColorBrush(lighter);
        resources["ButtonBackgroundPressed"] = new ImmutableSolidColorBrush(darker);
        resources["ButtonBackgroundDisabled"] = new ImmutableSolidColorBrush(disabled);
        resources["ButtonForeground"] = new ImmutableSolidColorBrush(text);
        resources["ButtonForegroundPointerOver"] = new ImmutableSolidColorBrush(text);
        resources["ButtonForegroundPressed"] = new ImmutableSolidColorBrush(text);
        resources["ButtonForegroundDisabled"] = new ImmutableSolidColorBrush(Blend(text, Color.Parse(RoofUiPalette.Background), 0.5));
        resources["ButtonBorderBrush"] = new ImmutableSolidColorBrush(edge);
        resources["ButtonBorderBrushPointerOver"] = new ImmutableSolidColorBrush(Blend(edge, Colors.White, 0.18));
        resources["ButtonBorderBrushPressed"] = new ImmutableSolidColorBrush(Blend(edge, Colors.Black, 0.25));
        resources["ButtonBorderBrushDisabled"] = new ImmutableSolidColorBrush(Blend(edge, Color.Parse(RoofUiPalette.Background), 0.6));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * amount));
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}
