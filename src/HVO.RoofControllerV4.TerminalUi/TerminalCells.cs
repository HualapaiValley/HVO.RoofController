using System.Globalization;
using System.Text;
using Terminal.Gui.Text;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>Cell geometry for tables, wrapped explanations and forms. Never cuts a grapheme cluster.</summary>
public static class TerminalCells
{
    /// <summary>Whole extended grapheme clusters, including combining sequences and joined emoji.</summary>
    public static IEnumerable<string> Graphemes(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) yield return elements.GetTextElement();
    }

    /// <summary>The cells a line occupies, using the renderer's rune widths per complete grapheme.</summary>
    public static int Width(string text) => Graphemes(text).Sum(GraphemeWidth);

    private static int GraphemeWidth(string text)
    {
        // A joined emoji, flag or emoji with a presentation selector is one terminal glyph, never the sum of its runes.
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Any(rune => rune.Value is 0x200d or 0xfe0f) || runes.Count(rune => rune.Value is >= 0x1f1e6 and <= 0x1f1ff) == 2)
            return 2;
        return Math.Max(0, runes.Select(rune => rune.GetColumns()).DefaultIfEmpty(0).Max());
    }

    /// <summary>Pads to a cell column, without truncating text.</summary>
    public static string PadRight(string text, int width) => text + new string(' ', Math.Max(0, width - Width(text)));

    /// <summary>The longest whole prefix fitting the requested cells.</summary>
    public static string Take(string text, int width)
    {
        var result = new StringBuilder();
        var used = 0;
        foreach (var grapheme in Graphemes(text))
        {
            var cells = GraphemeWidth(grapheme);
            if (used + cells > width) break;
            result.Append(grapheme);
            used += cells;
        }
        return result.ToString();
    }

    /// <summary>Wraps whole graphemes, preferring spaces; continuation lines start under the value column.</summary>
    public static IEnumerable<string> Wrap(string line, int width, int indent = 0)
    {
        if (width <= 0) { yield return line; yield break; }
        var rest = line;
        var first = true;
        do
        {
            var prefix = first ? string.Empty : new string(' ', Math.Clamp(indent, 0, Math.Max(0, width / 2)));
            var room = width - prefix.Length;
            if (Width(rest) <= room) { yield return prefix + rest; yield break; }
            var part = Take(rest, room);
            // Even a two-cell glyph in a one-cell viewport must make progress without being split.
            if (part.Length == 0) part = Graphemes(rest).First();
            var space = part.LastIndexOf(' ');
            if (space > part.Length - part.TrimStart(' ').Length) part = part[..space];
            yield return prefix + part.TrimEnd(' ');
            rest = rest[part.Length..].TrimStart(' ');
            first = false;
        } while (rest.Length > 0);
    }
}
