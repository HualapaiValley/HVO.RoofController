using System.Text.Json;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>Exports the driver's actual whole graphemes and cell geometry beside ANSI documentation captures.</summary>
internal static class TerminalCapture
{
    public static string SaveCells(IApplication app, string ansiPath)
    {
        var contents = app.Driver!.Contents!;
        var rows = new List<object?[]>();
        for (var y = 0; y < contents.GetLength(0); y++)
        {
            var row = new object?[contents.GetLength(1)];
            for (var x = 0; x < row.Length; x++)
            {
                var cell = contents[y, x];
                if (string.IsNullOrEmpty(cell.Grapheme) || cell.Attribute is not { } attribute) continue;
                row[x] = new
                {
                    text = cell.Grapheme,
                    columns = TerminalCells.Width(cell.Grapheme),
                    foreground = Hex(attribute.Foreground),
                    background = Hex(attribute.Background),
                    bold = attribute.Style.HasFlag(TextStyle.Bold),
                    dim = attribute.Style.HasFlag(TextStyle.Faint),
                    underline = attribute.Style.HasFlag(TextStyle.Underline),
                    reverse = attribute.Style.HasFlag(TextStyle.Reverse)
                };
            }
            rows.Add(row);
        }
        var path = Path.ChangeExtension(ansiPath, ".cells.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
        return path;
    }

    private static string? Hex(Color colour) => colour == Color.None ? null : $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";
}
