namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// How the kiosk (or the Mac app) signs people in, locks and blanks. The kiosk's own settings; the screen timeout itself is
/// the controller's.
/// </summary>
public sealed record KioskConsoleOptions
{
    /// <summary>The longest idle lock the kiosk accepts.</summary>
    public static readonly TimeSpan MaximumIdleLock = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the kiosk stays unlocked without a touch. It also locks when the controller would end the PIN session
    /// (its idle timeout), and never while it renews the operator lease of a motion it started. Default 2 minutes.
    /// </summary>
    public TimeSpan IdleLock { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How a person signs in, and the words for it. Default the kiosk's (a PIN); the Mac app's is
    /// <see cref="KioskWording.Desktop"/> (a name and password).
    /// </summary>
    public KioskWording Wording { get; init; } = KioskWording.Kiosk;

    /// <summary>
    /// True (the kiosk): the screen blanks after the controller's kiosk screen timeout. False (the Mac app, a window among
    /// others): it never blanks, and the timeout is not read.
    /// </summary>
    public bool Blanking { get; init; } = true;

    /// <summary>How often the screen timeout is read from the controller's settings. Default 5 minutes.</summary>
    public TimeSpan ScreenTimeoutRefresh { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the idle lock and the screen timeout are checked. Default 1 second.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The screen timeout used until the controller's is read. Default 5 minutes.</summary>
    public TimeSpan DefaultScreenTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long locking waits for the controller to end the PIN session. Default 5 seconds.</summary>
    public TimeSpan SignOutTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most time between two requests that keep the PIN session open while the person touches the screen. A shorter
    /// session idle timeout is kept open four times within it. Default 30 seconds.
    /// </summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}
