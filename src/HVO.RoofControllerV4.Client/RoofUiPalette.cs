namespace HVO.RoofControllerV4.Client;

/// <summary>
/// HVO Dark semantic colours, mirrored from final SkyMonitor bf5ef211 and the source-pinned
/// docs/ui-redesign/tokens.json contract. Web owns wwwroot/css/hvo-dark.css; native and terminal
/// adapters consume these constants. Notice text belongs on its matching background. Alpha notice
/// backgrounds are already composited over Background. Compatibility aliases keep staged clients building.
/// </summary>
public static class RoofUiPalette
{
    /// <summary>The page background: <c>--hvo-body-bg</c>.</summary>
    public const string Background = "#05070d";

    /// <summary>Text: <c>--hvo-body-color</c>.</summary>
    public const string Text = "#f7f9fc";

    /// <summary>Secondary text: <c>--hvo-muted</c>.</summary>
    public const string Muted = "#98a7bb";

    /// <summary>Compatibility alias for secondary text, including disabled controls.</summary>
    public const string MutedWeak = Muted;

    /// <summary>Panels and header: <c>--hvo-surface</c>.</summary>
    public const string Surface = "#0d1420";

    /// <summary>Raised actions and inputs: <c>--hvo-surface-elevated</c>.</summary>
    public const string SurfaceRaised = "#111c2a";

    /// <summary>Frame lines: <c>--hvo-border-strong</c>.</summary>
    public const string Border = "#3a4a60";

    /// <summary>Compatibility alias: raised navigation keys.</summary>
    public const string Badge = SurfaceRaised;

    /// <summary>Links, key names and selection: <c>--hvo-accent</c>.</summary>
    public const string Accent = "#58a6ff";

    /// <summary>The focused control: <c>--hvo-accent-strong</c>.</summary>
    public const string AccentStrong = "#2563eb";

    /// <summary>Compatibility alias: a high contrast accent keyboard outline.</summary>
    public const string FocusRing = Accent;

    /// <summary><c>--hvo-success-strong</c>.</summary>
    public const string Success = "#3fb950";

    /// <summary><c>--hvo-success-fg</c>, on <see cref="SuccessBackground"/>.</summary>
    public const string SuccessText = "#bbf7d0";

    /// <summary><c>--hvo-success-bg</c>.</summary>
    public const string SuccessBackground = "#0c3721";

    /// <summary><c>--hvo-warning-strong</c>.</summary>
    public const string Warning = "#d29922";

    /// <summary><c>--hvo-warning-fg</c>, on <see cref="WarningBackground"/>: a stale status, for one.</summary>
    public const string WarningText = "#fef9c3";

    /// <summary><c>--hvo-warning-bg</c>.</summary>
    public const string WarningBackground = "#42380f";

    /// <summary><c>--hvo-danger-strong</c>.</summary>
    public const string Danger = "#f85149";

    /// <summary><c>--hvo-danger-fg</c>, on <see cref="DangerBackground"/>: an error.</summary>
    public const string DangerText = "#fecaca";

    /// <summary><c>--hvo-danger-bg</c>.</summary>
    public const string DangerBackground = "#422226";

    /// <summary><c>--hvo-info-strong</c>.</summary>
    public const string Info = "#58a6ff";

    /// <summary><c>--hvo-info-fg</c>, on <see cref="InfoBackground"/>.</summary>
    public const string InfoText = "#58a6ff";

    /// <summary><c>--hvo-info-bg</c>.</summary>
    public const string InfoBackground = "#123548";

    /// <summary>
    /// Permanent red Stop action, idle (nothing to stop): the dim danger pair, retained while focused. Stop is never
    /// disabled; only its colour follows <see cref="RoofActionStates.IsStopActive"/>.
    /// </summary>
    public const string StopButton = DangerBackground;

    /// <summary>The Stop action's text, paired with StopButton.</summary>
    public const string StopButtonText = DangerText;

    /// <summary>Stop's edge while idle.</summary>
    public const string StopButtonBorder = Danger;

    /// <summary>Stop while the roof is moving, or last known to be: the bright danger tone, so the action that acts stands out.</summary>
    public const string StopButtonActive = Danger;

    /// <summary>Active Stop's text: the page background on bright danger (about 6:1).</summary>
    public const string StopButtonActiveText = Background;

    /// <summary>Active Stop's edge.</summary>
    public const string StopButtonActiveBorder = DangerText;

    /// <summary>Open or Close offered (<see cref="RoofActionState.Available"/>): the success notice pair.</summary>
    public const string MotionAvailable = SuccessBackground;

    /// <summary>An offered Open or Close's text.</summary>
    public const string MotionAvailableText = SuccessText;

    /// <summary>An offered Open or Close's edge.</summary>
    public const string MotionAvailableBorder = Success;

    /// <summary>Open or Close while the roof moves its way (<see cref="RoofActionState.Running"/>): bright success.</summary>
    public const string MotionRunning = Success;

    /// <summary>A running Open or Close's text: the page background on bright success (about 8:1).</summary>
    public const string MotionRunningText = Background;

    /// <summary>A running Open or Close's edge.</summary>
    public const string MotionRunningBorder = SuccessText;

    /// <summary>
    /// Open or Close not offered (<see cref="RoofActionState.Disabled"/>): neutral, not green, so it reads as disabled; its
    /// label stays readable and its reason is shown beside it.
    /// </summary>
    public const string MotionDisabled = Surface;

    /// <summary>A disabled Open or Close's text.</summary>
    public const string MotionDisabledText = Muted;

    /// <summary>A disabled Open or Close's edge.</summary>
    public const string MotionDisabledBorder = Border;

    /// <summary>Compatibility alias: an offered Open (<see cref="MotionAvailable"/>).</summary>
    public const string OpenButton = MotionAvailable;

    /// <summary>The Open action's text.</summary>
    public const string OpenButtonText = MotionAvailableText;

    /// <summary>Compatibility alias: an offered Close (<see cref="MotionAvailable"/>); Stop owns the red emphasis.</summary>
    public const string CloseButton = MotionAvailable;

    /// <summary>The Close action's text.</summary>
    public const string CloseButtonText = MotionAvailableText;
}
