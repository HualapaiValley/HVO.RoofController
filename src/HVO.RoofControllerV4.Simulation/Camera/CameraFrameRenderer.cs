using System.Globalization;
using HVO.RoofControllerV4.Simulation.Emulator;

namespace HVO.RoofControllerV4.Simulation.Camera;

/// <summary>
/// Draws the emulated camera's view of the plant: the roof on its track between the two limit switches (lit while
/// actuated), the plant time, the position and a frame counter, so every frame differs and a viewer can tell a live
/// stream from a frozen one.
/// </summary>
public static class CameraFrameRenderer
{
    public const int Width = 320;
    public const int Height = 240;

    private const int Scale = 3;
    private const int TrackY = 190;
    private const int ClosedX = 40;
    private const int OpenX = 280;
    private const int RoofWidth = 120;

    // 3x5 glyphs, one string per row, '#' lit.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['1'] = [".#.", "##.", ".#.", ".#.", "###"],
        ['2'] = ["###", "..#", "###", "#..", "###"],
        ['3'] = ["###", "..#", "###", "..#", "###"],
        ['4'] = ["#.#", "#.#", "###", "..#", "..#"],
        ['5'] = ["###", "#..", "###", "..#", "###"],
        ['6'] = ["###", "#..", "###", "#.#", "###"],
        ['7'] = ["###", "..#", "..#", "..#", "..#"],
        ['8'] = ["###", "#.#", "###", "#.#", "###"],
        ['9'] = ["###", "#.#", "###", "..#", "###"],
        [':'] = ["...", ".#.", "...", ".#.", "..."],
        ['.'] = ["...", "...", "...", "...", ".#."],
        ['%'] = ["#.#", "..#", ".#.", "#..", "#.#"],
        ['-'] = ["...", "...", "###", "...", "..."],
        ['A'] = [".#.", "#.#", "###", "#.#", "#.#"],
        ['C'] = ["###", "#..", "#..", "#..", "###"],
        ['D'] = ["##.", "#.#", "#.#", "#.#", "##."],
        ['E'] = ["###", "#..", "##.", "#..", "###"],
        ['F'] = ["###", "#..", "##.", "#..", "#.."],
        ['L'] = ["#..", "#..", "#..", "#..", "###"],
        ['M'] = ["#.#", "###", "###", "#.#", "#.#"],
        ['N'] = ["##.", "#.#", "#.#", "#.#", "#.#"],
        ['O'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['P'] = ["###", "#.#", "###", "#..", "#.."],
        ['R'] = ["##.", "#.#", "##.", "#.#", "#.#"],
        ['S'] = ["###", "#..", "###", "..#", "###"],
        ['T'] = ["###", ".#.", ".#.", ".#.", ".#."],
        ['U'] = ["#.#", "#.#", "#.#", "#.#", "###"],
        ['V'] = ["#.#", "#.#", "#.#", "#.#", ".#."],
        [' '] = ["...", "...", "...", "...", "..."]
    };

    /// <summary>Draws frame <paramref name="frame"/> of camera <paramref name="cameraId"/> as greyscale pixels, row-major.</summary>
    public static byte[] Render(HatEmulatorStatus plant, int cameraId, long frame)
    {
        ArgumentNullException.ThrowIfNull(plant);
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)32);

        DrawText(pixels, 8, 8, string.Create(CultureInfo.InvariantCulture, $"EMULATED CAM {cameraId:D2}"), 230);
        DrawText(pixels, 8, 30, string.Create(CultureInfo.InvariantCulture, $"T {plant.Elapsed:hh\\:mm\\:ss\\.f}"), 200);
        DrawText(pixels, 8, 52, string.Create(CultureInfo.InvariantCulture, $"OPEN {plant.OpenPercent:0}%"), 200);
        DrawText(pixels, 8, 74, string.Create(CultureInfo.InvariantCulture, $"F {frame % 100000:00000}"), 150);

        // The track, the limit switches (bright while actuated) and the roof, whose left edge runs from the closed limit
        // (position 0) to the open limit less the roof's width (the travel).
        Fill(pixels, 20, TrackY, 300, TrackY + 3, 120);
        Fill(pixels, ClosedX - 4, TrackY - 40, ClosedX, TrackY, plant.ClosedLimitActuated ? (byte)255 : (byte)70);
        Fill(pixels, OpenX, TrackY - 40, OpenX + 4, TrackY, plant.OpenLimitActuated ? (byte)255 : (byte)70);
        var fraction = plant.TravelMeters > 0 ? plant.PositionMeters / plant.TravelMeters : 0;
        var left = (int)Math.Round(ClosedX + fraction * (OpenX - ClosedX - RoofWidth));
        Fill(pixels, left, TrackY - 34, left + RoofWidth, TrackY - 4, 190);

        // A marker that steps every frame, so consecutive frames always differ.
        var step = (int)(frame % 16);
        Fill(pixels, 300 - step * 4, 8, 304 - step * 4, 12, 255);
        return pixels;
    }

    /// <summary>Frame <paramref name="frame"/> of camera <paramref name="cameraId"/> as a JPEG.</summary>
    public static byte[] RenderJpeg(HatEmulatorStatus plant, int cameraId, long frame)
        => JpegEncoder.EncodeGreyscale(Render(plant, cameraId, frame), Width, Height);

    private static void DrawText(byte[] pixels, int x, int y, string text, byte shade)
    {
        foreach (var character in text)
        {
            if (Glyphs.TryGetValue(character, out var rows))
            {
                for (var row = 0; row < rows.Length; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        if (rows[row][column] == '#')
                        {
                            Fill(pixels, x + column * Scale, y + row * Scale, x + (column + 1) * Scale, y + (row + 1) * Scale, shade);
                        }
                    }
                }
            }

            x += 4 * Scale;
        }
    }

    /// <summary>Fills [x0, x1) x [y0, y1), clipped to the frame.</summary>
    private static void Fill(byte[] pixels, int x0, int y0, int x1, int y1, byte shade)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
            {
                pixels[y * Width + x] = shade;
            }
        }
    }
}
