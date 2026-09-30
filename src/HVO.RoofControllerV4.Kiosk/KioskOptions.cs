using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.Kiosk;

/// <summary>
/// The kiosk's settings: the <c>Kiosk</c> section, from appsettings.Local.json next to the program, the environment
/// (Kiosk__*) and the command line (--Kiosk:Name=value), later ones winning.
/// </summary>
public sealed class KioskOptions
{
    public const string SectionName = "Kiosk";

    /// <summary>The Pi Touch Display 2 in landscape: 1280 pixels across about 155 mm.</summary>
    public const double DefaultPixelsPerMillimetre = 8.2;

    /// <summary>The controller's API. Default http://localhost:8080: the kiosk runs on the controller's Pi.</summary>
    public Uri ControllerUrl { get; set; } = new("http://localhost:8080");

    /// <summary>
    /// A file holding the kiosk's device key (an API key the controller marks <c>Kiosk: true</c>) on one line. Only the
    /// kiosk's user may read it. Required.
    /// </summary>
    public string? DeviceKeyFile { get; set; }

    /// <summary>For an https:// <see cref="ControllerUrl"/> with a self-signed certificate: its SHA-256, in hex.</summary>
    public string? ServerCertificateSha256 { get; set; }

    /// <summary>How long, in seconds, the kiosk stays unlocked without a touch. Default 120, at most 3600.</summary>
    public int IdleLockSeconds { get; set; } = 120;

    /// <summary>
    /// The screen's pixels per millimetre, which sets the size of everything so that a touch target is at least 12 mm.
    /// Default 8.2 (the Pi Touch Display 2); the original 7-inch Pi touchscreen is about 5.2.
    /// </summary>
    public double PixelsPerMillimetre { get; set; } = DefaultPixelsPerMillimetre;

    /// <summary>
    /// The display's DRM device, such as /dev/dri/card1. None: the first card with a connected display
    /// (<see cref="KioskDisplay.FindCard"/>).
    /// </summary>
    public string? Card { get; set; }

    /// <summary>
    /// How far the picture is turned, clockwise: 0, 90, 180 or 270. Default 0. The Touch Display 2's panel is portrait
    /// (720x1280), so landscape takes 90 or 270, whichever is the right way up in its mount.
    /// </summary>
    public int Rotation { get; set; }

    /// <summary>
    /// The touchscreen's input devices, such as /dev/input/by-path/…-event. None: every input device libinput finds on
    /// the seat.
    /// </summary>
    public string[] InputDevices { get; set; } = [];

    /// <summary>
    /// A backlight control the kiosk writes when it blanks and wakes the screen, such as
    /// /sys/class/backlight/10-0045/bl_power. None: a blank screen is drawn black with the backlight left on.
    /// </summary>
    public string? BacklightFile { get; set; }

    /// <summary>What <see cref="BacklightFile"/> is given to turn the backlight on. Default 0 (bl_power: on).</summary>
    public string BacklightOn { get; set; } = "0";

    /// <summary>What <see cref="BacklightFile"/> is given to turn the backlight off. Default 4 (bl_power: powered down).</summary>
    public string BacklightOff { get; set; } = "4";

    /// <summary>Runs the kiosk in a desktop window instead of on the display (for development). Also --window.</summary>
    public bool Window { get; set; }

    /// <summary>The window's size with <see cref="Window"/>, as WIDTHxHEIGHT. Default 1280x720.</summary>
    public string WindowSize { get; set; } = "1280x720";

    /// <summary>The problems with these settings, empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!ControllerUrl.IsAbsoluteUri || ControllerUrl.Scheme is not ("http" or "https"))
        {
            problems.Add($"{SectionName}:ControllerUrl must be an absolute http or https URL, such as http://localhost:8080.");
        }

        if (!string.IsNullOrWhiteSpace(ServerCertificateSha256) && !RoofCertificatePin.IsValid(ServerCertificateSha256))
        {
            problems.Add($"{SectionName}:ServerCertificateSha256 must be the certificate's SHA-256: 64 hex digits, colons allowed.");
        }

        if (string.IsNullOrWhiteSpace(DeviceKeyFile))
        {
            problems.Add($"{SectionName}:DeviceKeyFile must name the file that holds the kiosk's device key.");
        }

        if (IdleLockSeconds < 10 || TimeSpan.FromSeconds(IdleLockSeconds) > KioskConsoleOptions.MaximumIdleLock)
        {
            problems.Add($"{SectionName}:IdleLockSeconds must be between 10 and {KioskConsoleOptions.MaximumIdleLock.TotalSeconds:0}, got {IdleLockSeconds}.");
        }

        if (!double.IsFinite(PixelsPerMillimetre) || PixelsPerMillimetre is < 2 or > 40)
        {
            problems.Add($"{SectionName}:PixelsPerMillimetre must be between 2 and 40, got {PixelsPerMillimetre}.");
        }

        if (Rotation is not (0 or 90 or 180 or 270))
        {
            problems.Add($"{SectionName}:Rotation must be 0, 90, 180 or 270, got {Rotation}.");
        }

        if (!string.IsNullOrWhiteSpace(Card) && !Card.StartsWith("/dev/dri/", StringComparison.Ordinal))
        {
            problems.Add($"{SectionName}:Card must be a DRM device under /dev/dri/, such as /dev/dri/card1, got {Card}.");
        }

        foreach (var device in InputDevices ?? [])
        {
            if (!device.StartsWith("/dev/input/", StringComparison.Ordinal))
            {
                problems.Add($"{SectionName}:InputDevices has {device}, which is not an input device under /dev/input/.");
            }
        }

        if (!string.IsNullOrWhiteSpace(BacklightFile) && (string.IsNullOrWhiteSpace(BacklightOn) || string.IsNullOrWhiteSpace(BacklightOff)))
        {
            problems.Add($"{SectionName}:BacklightOn and BacklightOff must both be set when BacklightFile is.");
        }

        if (Window && !TryParseSize(WindowSize, out _, out _))
        {
            problems.Add($"{SectionName}:WindowSize must be WIDTHxHEIGHT, such as 1280x720, got {WindowSize}.");
        }

        return problems;
    }

    /// <summary>Reads WIDTHxHEIGHT, each from 200 to 8000 pixels.</summary>
    public static bool TryParseSize(string? text, out int width, out int height)
    {
        width = height = 0;
        var parts = (text ?? string.Empty).Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out height)
            && width is >= 200 and <= 8000
            && height is >= 200 and <= 8000;
    }
}
