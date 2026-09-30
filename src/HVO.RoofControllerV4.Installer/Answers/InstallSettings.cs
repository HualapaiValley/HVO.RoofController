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

    /// <summary>
    /// Other short names clients use for the controller, besides this machine's host name (roof, observatory-roof). The
    /// certificate names each, each under <c>.local</c> and under each domain, and the private CA may issue for them.
    /// </summary>
    public IReadOnlyList<string> HostNames { get; init; } = [];

    /// <summary>
    /// DNS domains clients reach the controller under (observatory.example). The certificate names each host name under
    /// each, and the private CA may issue for anything in them. Only these: the wizard offers the domains this machine's
    /// resolver searches, but none is added unless listed here.
    /// </summary>
    public IReadOnlyList<string> Domains { get; init; } = [];

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

        foreach (var name in HostNames.Where(name => !DnsName.IsLabel(name)))
        {
            yield return $"'{name}' is not a host name: give a single name of letters, digits and hyphens (roof), not an address or a name with dots.";
        }

        foreach (var domain in Domains.Where(domain => !DnsName.IsDomain(domain)))
        {
            yield return $"'{domain}' is not a domain: give names of letters, digits and hyphens joined by dots (observatory.example).";
        }
    }

    /// <summary>These choices with the names and domains in lower case, each once, in order.</summary>
    public ControllerSettings Normalised() => this with
    {
        HostNames = DnsName.Distinct(HostNames),
        Domains = DnsName.Distinct(Domains.Select(domain => domain.TrimEnd('.')))
    };

    public bool Equals(ControllerSettings? other)
        => other is not null
            && Connection == other.Connection
            && HttpsPort == other.HttpsPort
            && HttpPort == other.HttpPort
            && WebPort == other.WebPort
            && HostNames.SequenceEqual(other.HostNames, StringComparer.OrdinalIgnoreCase)
            && Domains.SequenceEqual(other.Domains, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Connection, HttpsPort, HttpPort, WebPort, HostNames.Count, Domains.Count);

    public static string Describe(ConnectionMode mode) => mode switch
    {
        ConnectionMode.PrivateCa => "HTTPS, with a certificate from this installer's certificate authority (recommended)",
        ConnectionMode.OwnCertificate => "HTTPS, with your own certificate",
        ConnectionMode.SelfSigned => "HTTPS, with a self-signed certificate that each client pins",
        ConnectionMode.Http => "HTTP, with no encryption (only on a network you trust)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}

/// <summary>Host names and domains, as a certificate and its CA's name constraints hold them.</summary>
public static class DnsName
{
    /// <summary>
    /// True for a host name of one DNS label: 1 to 63 letters, digits and hyphens, not starting or ending with a hyphen,
    /// and not only digits.
    /// </summary>
    public static bool IsLabel(string? name) => IsDnsLabel(name) && !name!.All(char.IsAsciiDigit);

    /// <summary>
    /// True for a domain: DNS labels joined by dots (a trailing dot allowed), at most 253 characters, the last not only
    /// digits (so never an address).
    /// </summary>
    public static bool IsDomain(string? domain)
        => domain is { Length: > 0 }
            && domain.TrimEnd('.') is { Length: > 0 and <= 253 } trimmed
            && trimmed.Split('.') is var labels
            && labels.All(IsDnsLabel)
            && !labels[^1].All(char.IsAsciiDigit);

    /// <summary>The names in lower case, each once, in the order first given.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> names)
        => names.Select(name => name.Trim().ToLowerInvariant()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static bool IsDnsLabel(string? label)
        => label is { Length: > 0 and <= 63 }
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && label[0] != '-' && label[^1] != '-';
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
