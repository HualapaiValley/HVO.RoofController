using System.Text;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// Writes to the console, and drops what cannot be written once the console is gone: after SIGHUP a write to the
/// closed terminal throws <see cref="IOException"/>, and a command that is sending Stop must not fail on its output.
/// </summary>
internal sealed class RoofCliConsoleWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override IFormatProvider FormatProvider => inner.FormatProvider;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine
    {
        get => inner.NewLine;
        set => inner.NewLine = value;
    }

    public override void Write(char value) => Try(() => inner.Write(value));

    public override void Write(string? value) => Try(() => inner.Write(value));

    public override void Write(char[] buffer, int index, int count) => Try(() => inner.Write(buffer, index, count));

    public override void WriteLine() => Try(inner.WriteLine);

    public override void WriteLine(string? value) => Try(() => inner.WriteLine(value));

    public override void Flush() => Try(inner.Flush);

    private static void Try(Action write)
    {
        try
        {
            write();
        }
        catch (IOException)
        {
            // The console is gone.
        }
    }
}
