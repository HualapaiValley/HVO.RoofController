namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>The terminal's colour protocol, independent of its character repertoire.</summary>
public enum TerminalColours
{
    /// <summary>The terminal's own foreground and background.</summary>
    None,
    /// <summary>Exactly the sixteen named ANSI colours.</summary>
    Ansi16,
    /// <summary>24-bit RGB.</summary>
    TrueColour
}

/// <summary>Capabilities shared by the roof interface and installer; see the terminal guide for overrides.</summary>
public sealed record TerminalCapabilities(TerminalColours Colours, bool Ascii)
{
    /// <summary>Detects capabilities without probing or changing the terminal.</summary>
    public static TerminalCapabilities Detect(Func<string, string?> environment)
    {
        var term = environment("TERM") ?? string.Empty;
        var colourTerm = environment("COLORTERM") ?? string.Empty;
        var locale = new[] { "LC_ALL", "LC_CTYPE", "LANG" }.Select(environment).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
        var colours = colourTerm.Equals("truecolor", StringComparison.OrdinalIgnoreCase) || colourTerm.Equals("24bit", StringComparison.OrdinalIgnoreCase)
            ? TerminalColours.TrueColour : TerminalColours.Ansi16;
        var ascii = term.Equals("dumb", StringComparison.OrdinalIgnoreCase) || term.StartsWith("vt100", StringComparison.OrdinalIgnoreCase)
            || !(locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("UTF8", StringComparison.OrdinalIgnoreCase));
        if (term.Equals("dumb", StringComparison.OrdinalIgnoreCase)) colours = TerminalColours.None;
        foreach (var option in (environment("HVO_TERMINAL") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (option.ToLowerInvariant())
            {
                case "truecolor": colours = TerminalColours.TrueColour; break;
                case "ansi16": colours = TerminalColours.Ansi16; break;
                case "nocolor": colours = TerminalColours.None; break;
                case "ascii": ascii = true; break;
                case "unicode": ascii = false; break;
            }
        }
        if (!string.IsNullOrEmpty(environment("NO_COLOR"))) colours = TerminalColours.None;
        return new(colours, ascii);
    }
}
