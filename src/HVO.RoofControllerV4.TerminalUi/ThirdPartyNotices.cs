using System.CommandLine;
using System.CommandLine.Invocation;

namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>Notices carried inside each standalone terminal program, including after installation.</summary>
public static class ThirdPartyNotices
{
    /// <summary>The discoverable option, terminating before a command can authorize, connect or change the machine.</summary>
    public static Option<bool> CreateOption(TextWriter output) => new("--third-party-notices")
    {
        Description = "Print the embedded third-party licence, copyright and author notices, then exit.",
        Recursive = true,
        Action = new NoticesAction(output)
    };

    /// <summary>Writes the embedded notice document without reading configuration or contacting a controller.</summary>
    public static void WriteTo(TextWriter output)
    {
        using var resource = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream("HVO.Terminal.ThirdPartyNotices")
            ?? throw new InvalidOperationException("The terminal third-party notices resource is missing.");
        using var reader = new StreamReader(resource);
        output.Write(reader.ReadToEnd());
    }

    private sealed class NoticesAction(TextWriter output) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            WriteTo(output);
            return 0;
        }
    }
}
