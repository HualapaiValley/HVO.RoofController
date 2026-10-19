using System.Text;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>Optional emphasis for a real interactive console; text, redirected streams and JSON stay intact.</summary>
public sealed class TerminalOutputWriter(TextWriter inner, TerminalCapabilities capabilities) : TextWriter
{
    /// <inheritdoc/>
    public override Encoding Encoding => inner.Encoding;

    /// <inheritdoc/>
    public override void Write(char value) => inner.Write(value);

    /// <inheritdoc/>
    public override void Write(string? value) => inner.Write(value);

    /// <inheritdoc/>
    public override void WriteLine(string? value)
    {
        var tone = Tone(value);
        if (tone is null || capabilities.Colours == TerminalColours.None) { inner.WriteLine(value); return; }
        var (hex, ansi) = tone.Value;
        var prefix = capabilities.Colours == TerminalColours.Ansi16 ? $"\u001b[{ansi}m"
            : $"\u001b[38;2;{Convert.ToInt32(hex[1..3], 16)};{Convert.ToInt32(hex[3..5], 16)};{Convert.ToInt32(hex[5..7], 16)}m";
        inner.Write(prefix);
        inner.Write(value);
        inner.WriteLine("\u001b[39m");
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    // Only unquoted human-readable lines receive emphasis. No JSON key/value or notice-body rewriting.
    private static (string Hex, int Ansi)? Tone(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.TrimStart();
        if (text.StartsWith('"') || text.StartsWith('{') || text.StartsWith('[')) return null;
        if (text.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) || text.StartsWith("STALE", StringComparison.Ordinal)
            || text.Contains("unverified", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Blocked:", StringComparison.Ordinal)) return (RoofUiPalette.Warning, 93);
        if (text.StartsWith("Not sent", StringComparison.Ordinal) || text.StartsWith("The installer cannot", StringComparison.Ordinal)
            || text.StartsWith("Stop failed", StringComparison.Ordinal) || text.StartsWith("The installer stopped", StringComparison.Ordinal)) return (RoofUiPalette.Danger, 91);
        if (text.StartsWith("Stop acknowledged", StringComparison.Ordinal) || text.StartsWith("Done.", StringComparison.Ordinal)) return (RoofUiPalette.Success, 92);
        if (text.StartsWith("Installing ", StringComparison.Ordinal) || text.StartsWith("The plan for", StringComparison.Ordinal)
            || text.StartsWith("Roof:", StringComparison.Ordinal) || text.StartsWith("Controller:", StringComparison.Ordinal)) return (RoofUiPalette.Accent, 96);
        return null;
    }
}
