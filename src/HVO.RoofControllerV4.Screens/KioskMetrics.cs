namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's sizes, from how many pixels make a millimetre on its screen: every button is at least
/// <see cref="MinimumTouchMillimetres"/> in both directions, and the text scales with it so it reads at a distance.
/// </summary>
public sealed record KioskMetrics
{
    /// <summary>The smallest touch target, in millimetres.</summary>
    public const double MinimumTouchMillimetres = 12;

    /// <summary>
    /// The sizes for a screen with <paramref name="pixelsPerMillimetre"/> device-independent pixels in a millimetre (the
    /// Raspberry Pi Touch Display 2 about 8.2; the first 7-inch display about 5.2).
    /// </summary>
    public KioskMetrics(double pixelsPerMillimetre)
    {
        if (!double.IsFinite(pixelsPerMillimetre) || pixelsPerMillimetre <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerMillimetre), pixelsPerMillimetre, "Pixels per millimetre must be positive.");
        }

        PixelsPerMillimetre = pixelsPerMillimetre;
        Touch = Math.Ceiling(MinimumTouchMillimetres * pixelsPerMillimetre);
        Font = Math.Round(Math.Clamp(Touch * 0.26, 16, 30));
        Small = Math.Round(Font * 0.8);
        Large = Math.Round(Font * 1.5);
        Huge = Math.Round(Font * 2.4);
        Gap = Math.Max(4, Math.Round(Touch / 16));
        StopWidth = Math.Round(Touch * 2.6);
        NavWidth = Math.Round(Touch * 1.35);
    }

    public double PixelsPerMillimetre { get; }

    /// <summary>The smallest width and height of anything touched.</summary>
    public double Touch { get; }

    /// <summary>The body text.</summary>
    public double Font { get; }

    /// <summary>Badges and times.</summary>
    public double Small { get; }

    /// <summary>Headings and the Stop answer.</summary>
    public double Large { get; }

    /// <summary>The roof's position, and Stop.</summary>
    public double Huge { get; }

    /// <summary>The space between things.</summary>
    public double Gap { get; }

    /// <summary>The width of the Stop column at the right of every screen.</summary>
    public double StopWidth { get; }

    /// <summary>The width of the navigation rail at the left.</summary>
    public double NavWidth { get; }
}
