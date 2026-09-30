using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// The Mac app's settings: the <c>Mac</c> section, from appsettings.Local.json in the settings folder
/// (<see cref="MacSettingsFolder"/>), the environment (Mac__*) and the command line (--Mac:Name=value), later ones winning.
/// </summary>
public sealed class MacOptions
{
    public const string SectionName = "Mac";

    /// <summary>The device key file's name when <see cref="DeviceKeyFile"/> is not set: in the settings folder.</summary>
    public const string DefaultDeviceKeyFile = "device-key";

    /// <summary>The window's size when it opens, in the screen's points.</summary>
    public static readonly (int Width, int Height) WindowSize = (1280, 800);

    /// <summary>The smallest the window may be made: Stop, the pages and the status still fit (MacScreenTests).</summary>
    public static readonly (int Width, int Height) MinimumWindowSize = (960, 600);

    /// <summary>The controller's API, such as https://roof-pi.local:8443/. Required: the Mac is not the controller's Pi.</summary>
    public Uri? ControllerUrl { get; set; }

    /// <summary>
    /// A file holding the app's device key (a viewer key made for this Mac) on one line, readable only by its owner.
    /// A relative path is in the settings folder. Default <see cref="DefaultDeviceKeyFile"/>.
    /// </summary>
    public string? DeviceKeyFile { get; set; }

    /// <summary>For an https:// <see cref="ControllerUrl"/> with a self-signed certificate: its SHA-256, in hex.</summary>
    public string? ServerCertificateSha256 { get; set; }

    /// <summary>How long, in seconds, the app stays signed in without use. Default 900 (15 minutes), at most 3600.</summary>
    public int IdleLockSeconds { get; set; } = 900;

    /// <summary>
    /// The screen's points per millimetre, which sets the size of everything. Default 4
    /// (<see cref="KioskMetrics.DeskPixelsPerMillimetre"/>): the kiosk's 12 mm touch target is a 48-point button, and the text is
    /// 16 points.
    /// </summary>
    public double PixelsPerMillimetre { get; set; } = KioskMetrics.DeskPixelsPerMillimetre;

    /// <summary>The problems with these settings, empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (ControllerUrl is null)
        {
            problems.Add($"{SectionName}:ControllerUrl must be the controller's address, such as https://roof-pi.local:8443/.");
        }
        else if (!ControllerUrl.IsAbsoluteUri || ControllerUrl.Scheme is not ("http" or "https"))
        {
            problems.Add($"{SectionName}:ControllerUrl must be an absolute http or https URL, such as https://roof-pi.local:8443/.");
        }

        if (!string.IsNullOrWhiteSpace(ServerCertificateSha256) && !RoofCertificatePin.IsValid(ServerCertificateSha256))
        {
            problems.Add($"{SectionName}:ServerCertificateSha256 must be the certificate's SHA-256: 64 hex digits, colons allowed.");
        }

        if (IdleLockSeconds < 10 || TimeSpan.FromSeconds(IdleLockSeconds) > KioskConsoleOptions.MaximumIdleLock)
        {
            problems.Add($"{SectionName}:IdleLockSeconds must be between 10 and {KioskConsoleOptions.MaximumIdleLock.TotalSeconds:0}, got {IdleLockSeconds}.");
        }

        if (!double.IsFinite(PixelsPerMillimetre) || PixelsPerMillimetre is < 2 or > 40)
        {
            problems.Add($"{SectionName}:PixelsPerMillimetre must be between 2 and 40, got {PixelsPerMillimetre}.");
        }

        return problems;
    }

    /// <summary>The device key file: <see cref="DeviceKeyFile"/>, or <see cref="DefaultDeviceKeyFile"/>, in <paramref name="folder"/> if relative.</summary>
    public string DeviceKeyPath(string folder)
    {
        var file = string.IsNullOrWhiteSpace(DeviceKeyFile) ? DefaultDeviceKeyFile : DeviceKeyFile.Trim();
        return Path.GetFullPath(file.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..])
            : Path.Combine(folder, file));
    }
}
