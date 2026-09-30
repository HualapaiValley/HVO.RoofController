using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>How clients reach the controller.</summary>
public enum ConnectionMode
{
    /// <summary>HTTPS with a certificate from the installer's own certificate authority, which clients trust once.</summary>
    PrivateCa,

    /// <summary>HTTPS with a certificate you give (a PFX).</summary>
    OwnCertificate,

    /// <summary>HTTPS with a self-signed certificate, which each client pins.</summary>
    SelfSigned,

    /// <summary>HTTP with no encryption: only on a network you trust.</summary>
    Http
}

/// <summary>The controller's choices: for the controller, and for a test rig.</summary>
public sealed record ControllerSettings
{
    public const int DefaultHttpsPort = 8443;
    public const int DefaultHttpPort = 8080;
    public const int DefaultWebPort = 8088;

    public ConnectionMode Connection { get; init; } = ConnectionMode.PrivateCa;

    /// <summary>The port of the controller's API over HTTPS (the deploy script's HTTPS_HOST_PORT).</summary>
    public int HttpsPort { get; init; } = DefaultHttpsPort;

    /// <summary>The port of the controller's API over HTTP, with <see cref="ConnectionMode.Http"/> (HOST_PORT).</summary>
    public int HttpPort { get; init; } = DefaultHttpPort;

    /// <summary>The port of the web UI (WEB_HOST_PORT).</summary>
    public int WebPort { get; init; } = DefaultWebPort;

    [JsonIgnore]
    public bool UsesHttps => Connection != ConnectionMode.Http;

    /// <summary>The port the controller's API is published on.</summary>
    [JsonIgnore]
    public int ApiPort => UsesHttps ? HttpsPort : HttpPort;

    /// <summary>What is wrong with these choices, if anything.</summary>
    public IEnumerable<string> Problems()
    {
        foreach (var (name, port) in new[] { ("The HTTPS port", HttpsPort), ("The HTTP port", HttpPort), ("The web UI's port", WebPort) })
        {
            if (port is < 1 or > 65535)
            {
                yield return $"{name} must be from 1 to 65535, not {port}.";
            }
        }

        if (WebPort == ApiPort)
        {
            yield return $"The web UI needs a port of its own: {WebPort} is the controller's API's port too.";
        }
    }

    public static string Describe(ConnectionMode mode) => mode switch
    {
        ConnectionMode.PrivateCa => "HTTPS, with a certificate from this installer's certificate authority (recommended)",
        ConnectionMode.OwnCertificate => "HTTPS, with your own certificate",
        ConnectionMode.SelfSigned => "HTTPS, with a self-signed certificate that each client pins",
        ConnectionMode.Http => "HTTP, with no encryption (only on a network you trust)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}

/// <summary>Where hvo-roof goes.</summary>
public sealed record CliSettings
{
    public const string HomeFolder = "~/.local/bin";
    public const string SharedFolder = "/usr/local/bin";

    /// <summary><see cref="HomeFolder"/> (the default, the person's own) or <see cref="SharedFolder"/>, when the person may write to it.</summary>
    public string Folder { get; init; } = HomeFolder;

    public static IReadOnlyList<string> Folders { get; } = [HomeFolder, SharedFolder];
}

/// <summary>Where the Mac app goes.</summary>
public sealed record MacAppSettings
{
    public const string SharedFolder = "/Applications";
    public const string HomeFolder = "~/Applications";

    public string Folder { get; init; } = SharedFolder;

    public static IReadOnlyList<string> Folders { get; } = [SharedFolder, HomeFolder];
}
