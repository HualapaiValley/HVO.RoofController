namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The colours of HVO Dark, the web UI's theme (<c>hvo-dark.css</c> in HVO.WebSite.Themes, from HVO.SkyMonitor), as
/// <c>#rrggbb</c> values for the interfaces that are not web pages: the terminal interface of <c>hvo-roof</c>, the kiosk
/// and the Mac app. Each one names the stylesheet's token; a token given with transparency is here as it looks over the
/// page background. Stop, Open and Close have the colours of the web UI's buttons (Bootstrap's warning, success and
/// danger buttons), so each looks the same in every interface.
/// </summary>
public static class RoofUiPalette
{
    /// <summary>The page background: <c>--hvo-body-bg</c>.</summary>
    public const string Background = "#05070d";

    /// <summary>Text: <c>--hvo-body-color</c>.</summary>
    public const string Text = "#f8fafc";

    /// <summary>Secondary text: <c>--hvo-muted</c>.</summary>
    public const string Muted = "#abb0b7";

    /// <summary>Hints and disabled controls: <c>--hvo-muted-weak</c>.</summary>
    public const string MutedWeak = "#aeb9c8";

    /// <summary>Panels, the header and input fields: <c>--hvo-surface</c>.</summary>
    public const string Surface = "#0e1628";

    /// <summary>Frame lines: <c>--hvo-border-stronger</c>.</summary>
    public const string Border = "#2d333d";

    /// <summary>The navigation bar's keys: <c>--hvo-nav-badge-bg</c>.</summary>
    public const string Badge = "#1c2028";

    /// <summary>Links, key names and selection: <c>--hvo-accent</c>.</summary>
    public const string Accent = "#3b82f6";

    /// <summary>The focused control: <c>--hvo-accent-strong</c>.</summary>
    public const string AccentStrong = "#2563eb";

    /// <summary>The keyboard focus outline: the stylesheet's <c>.nav-badge:focus-visible</c> outline, which has no token.</summary>
    public const string FocusRing = "#60a5fa";

    /// <summary><c>--hvo-success-strong</c>.</summary>
    public const string Success = "#34d399";

    /// <summary><c>--hvo-success-fg</c>, on <see cref="SuccessBackground"/>.</summary>
    public const string SuccessText = "#bbf7d0";

    /// <summary><c>--hvo-success-bg</c>.</summary>
    public const string SuccessBackground = "#0c3721";

    /// <summary><c>--hvo-warning-strong</c>.</summary>
    public const string Warning = "#facc15";

    /// <summary><c>--hvo-warning-fg</c>, on <see cref="WarningBackground"/>: a stale status, for one.</summary>
    public const string WarningText = "#fef9c3";

    /// <summary><c>--hvo-warning-bg</c>.</summary>
    public const string WarningBackground = "#42380f";

    /// <summary><c>--hvo-danger-strong</c>.</summary>
    public const string Danger = "#f87171";

    /// <summary><c>--hvo-danger-fg</c>, on <see cref="DangerBackground"/>: an error.</summary>
    public const string DangerText = "#fecaca";

    /// <summary><c>--hvo-danger-bg</c>.</summary>
    public const string DangerBackground = "#422226";

    /// <summary><c>--hvo-info-strong</c>.</summary>
    public const string Info = "#0ea5e9";

    /// <summary><c>--hvo-info-fg</c>, on <see cref="InfoBackground"/>.</summary>
    public const string InfoText = "#e0f2fe";

    /// <summary><c>--hvo-info-bg</c>.</summary>
    public const string InfoBackground = "#123548";

    /// <summary>The Stop button (the web UI's <c>btn-warning</c>).</summary>
    public const string StopButton = "#ffc107";

    /// <summary>The Stop button's text.</summary>
    public const string StopButtonText = "#000000";

    /// <summary>The Open button (the web UI's <c>btn-success</c>).</summary>
    public const string OpenButton = "#198754";

    /// <summary>The Open button's text.</summary>
    public const string OpenButtonText = "#ffffff";

    /// <summary>The Close button (the web UI's <c>btn-danger</c>).</summary>
    public const string CloseButton = "#dc3545";

    /// <summary>The Close button's text.</summary>
    public const string CloseButtonText = "#ffffff";
}
