using Avalonia.Platform;

namespace HVO.RoofControllerV4.Kiosk;

/// <summary>Finds the display: the DRM card with a connected screen, and the rotation Avalonia takes.</summary>
public static class KioskDisplay
{
    /// <summary>Where the kernel lists DRM cards and their connectors.</summary>
    public const string SysClassDrm = "/sys/class/drm";

    /// <summary>
    /// The first DRM card (/dev/dri/cardN) with a connector whose status is <c>connected</c>, by card number; null when
    /// none is found. A Pi 5 has more than one card and the first one is not always the display (the render-only v3d can
    /// be card0), so the kiosk does not simply take card0.
    /// </summary>
    /// <param name="sysClassDrm">Where to look: <see cref="SysClassDrm"/> (tests pass a copy).</param>
    public static string? FindCard(string sysClassDrm = SysClassDrm)
    {
        if (!Directory.Exists(sysClassDrm))
        {
            return null;
        }

        var cards = new SortedDictionary<int, string>();
        foreach (var connector in Directory.EnumerateDirectories(sysClassDrm, "card*-*"))
        {
            var name = Path.GetFileName(connector);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (!int.TryParse(name.AsSpan(4, dash - 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            string status;
            try
            {
                status = File.ReadAllText(Path.Combine(connector, "status")).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (status == "connected")
            {
                cards.TryAdd(number, $"/dev/dri/card{number}");
            }
        }

        return cards.Count > 0 ? cards.First().Value : null;
    }

    /// <summary>The Avalonia orientation for a clockwise turn of 0, 90, 180 or 270 degrees.</summary>
    public static SurfaceOrientation Orientation(int rotation) => rotation switch
    {
        90 => SurfaceOrientation.Rotation90,
        180 => SurfaceOrientation.Rotation180,
        270 => SurfaceOrientation.Rotation270,
        _ => SurfaceOrientation.Rotation0
    };
}
