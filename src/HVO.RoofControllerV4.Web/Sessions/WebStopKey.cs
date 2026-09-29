using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// The web UI's own API key for Stop (<see cref="RoofWebOptions.StopKeyFile"/>), read once at startup. It is a secret:
/// it never appears in a message, a log or a page.
/// </summary>
public sealed class WebStopKey
{
    private WebStopKey(string? value) => Value = value;

    /// <summary>No Stop key: Stop uses the person's session alone.</summary>
    public static WebStopKey None { get; } = new(null);

    /// <summary>The key, or null when none is configured.</summary>
    public string? Value { get; }

    /// <summary>Reads the key from <paramref name="path"/> (none: <see cref="None"/>).</summary>
    /// <exception cref="RoofWebSettingsException">The file cannot be read, is empty, or holds more than a key.</exception>
    public static WebStopKey Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return None;
        }

        string key;
        try
        {
            key = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoofWebSettingsException($"{RoofWebOptions.SectionName}:StopKeyFile names {path}, which could not be read ({ex.GetType().Name}).");
        }

        if (key.Length == 0 || key.Any(c => c is < ' ' or > '~') || key.Trim().Length != key.Length)
        {
            throw new RoofWebSettingsException(
                $"{RoofWebOptions.SectionName}:StopKeyFile names {path}, which must hold one API key on one line. {RoofCredential.InvalidHeaderValue}");
        }

        return new WebStopKey(key);
    }

    public override string ToString() => Value is null ? "no Stop key" : "Stop key";
}
