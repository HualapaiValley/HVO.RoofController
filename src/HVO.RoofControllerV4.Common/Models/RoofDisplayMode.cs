using System.Globalization;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>A requested pixel mode, independent of rotation and UI scaling.</summary>
public static class RoofDisplayMode
{
    public static bool TryParse(string? value, out int width, out int height)
    {
        width = height = 0;
        var parts = (value ?? string.Empty).Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width is >= 200 and <= 8000 && height is >= 200 and <= 8000;
    }
}
