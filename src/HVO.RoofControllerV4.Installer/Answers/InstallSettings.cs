using System.Globalization;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Common.Models;

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

    /// <summary>
    /// The first person, an admin, whom the installer adds when the controller has no admin yet: a name here, and a
    /// password (and a PIN, when asked for) that a person types or gives in a file. Nobody is added once there is an admin.
    /// </summary>
    public FirstAdminSettings? FirstAdmin { get; init; }

    /// <summary>
    /// The Blue Iris server whose camera the controller shows (the controller only: a rig shows the HAT emulator's). None
    /// leaves the camera as it is: set up by hand, or not at all.
    /// </summary>
    public CameraSettings? Camera { get; init; }

    /// <summary>
    /// Where the controller exports its telemetry: an OTLP/HTTP endpoint (http://collector:4318). None turns export off.
    /// </summary>
    public string? TelemetryEndpoint { get; init; }

    /// <summary>A test rig's HAT emulator, and who may reach the rig (a rig only).</summary>
    public RigSettings? Rig { get; init; }

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

        foreach (var problem in (FirstAdmin?.Problems() ?? []).Concat(Camera?.Problems() ?? []).Concat(Rig?.Problems() ?? []))
        {
            yield return problem;
        }

        if (!string.IsNullOrWhiteSpace(TelemetryEndpoint) && WebAddress.Problem(TelemetryEndpoint, allowPath: true) is { } telemetry)
        {
            yield return $"The telemetry endpoint {telemetry}";
        }

        if (Rig is { OpenToLan: true } && !UsesHttps)
        {
            yield return "A rig opened to the network needs HTTPS: choose a connection other than http, or keep the rig on this machine.";
        }
    }

    /// <summary>These choices with the names and domains in lower case, each once, in order.</summary>
    public ControllerSettings Normalised() => this with
    {
        HostNames = DnsName.Distinct(HostNames),
        Domains = DnsName.Distinct(Domains.Select(domain => domain.TrimEnd('.'))),
        FirstAdmin = FirstAdmin is { } admin ? admin with { Name = admin.Name.Trim() } : null,
        Camera = Camera is { } camera
            ? camera with { BaseUrl = camera.BaseUrl.Trim(), UserName = string.IsNullOrWhiteSpace(camera.UserName) ? null : camera.UserName.Trim() }
            : null,
        TelemetryEndpoint = string.IsNullOrWhiteSpace(TelemetryEndpoint) ? null : TelemetryEndpoint.Trim()
    };

    public bool Equals(ControllerSettings? other)
        => other is not null
            && Connection == other.Connection
            && HttpsPort == other.HttpsPort
            && HttpPort == other.HttpPort
            && WebPort == other.WebPort
            && HostNames.SequenceEqual(other.HostNames, StringComparer.OrdinalIgnoreCase)
            && Domains.SequenceEqual(other.Domains, StringComparer.OrdinalIgnoreCase)
            && Equals(FirstAdmin, other.FirstAdmin)
            && Equals(Camera, other.Camera)
            && string.Equals(TelemetryEndpoint, other.TelemetryEndpoint, StringComparison.Ordinal)
            && Equals(Rig, other.Rig);

    public override int GetHashCode()
        => HashCode.Combine(Connection, HttpsPort, HttpPort, WebPort, HostNames.Count, Domains.Count, HashCode.Combine(FirstAdmin, Camera, TelemetryEndpoint, Rig));

    public static string Describe(ConnectionMode mode) => mode switch
    {
        ConnectionMode.PrivateCa => "HTTPS, with a certificate from this installer's certificate authority (recommended)",
        ConnectionMode.OwnCertificate => "HTTPS, with your own certificate",
        ConnectionMode.SelfSigned => "HTTPS, with a self-signed certificate that each client pins",
        ConnectionMode.Http => "HTTP, with no encryption (only on a network you trust)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}

/// <summary>The controller's first person: an admin, who signs in to the web UI and adds everyone else.</summary>
public sealed record FirstAdminSettings
{
    /// <summary>The name they sign in with: a letter or digit, then letters, digits, '.', '_', '@' or '-'.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True to give them a PIN as well, for signing in at the kiosk.</summary>
    public bool Pin { get; init; }

    public IEnumerable<string> Problems()
    {
        if (!RoofIdentityContract.IsValidName(Name))
        {
            yield return $"'{Name}' is not a name the controller takes for its first admin: give up to {RoofIdentityContract.MaximumNameLength} letters, digits, '.', '_', '@' or '-', starting with a letter or digit.";
        }
    }
}

/// <summary>
/// The Blue Iris server the controller shows the observatory's camera from. Its password is a secret: a person types it,
/// or gives it in a file, and it goes only into the controller's secrets folder.
/// </summary>
public sealed record CameraSettings
{
    /// <summary>The server's address (http://192.168.0.4:81): the controller reaches it, a client never does.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>A Blue Iris user that may only view, when the server asks for one; null when it does not.</summary>
    public string? UserName { get; init; }

    public IEnumerable<string> Problems()
    {
        if (WebAddress.Problem(BaseUrl, allowPath: false) is { } problem)
        {
            yield return $"The camera's address {problem}";
        }

        if (UserName is { } name && (name.Any(char.IsControl) || name.Contains(':')))
        {
            yield return "The camera's user name may not hold a colon or a control character.";
        }
    }
}

/// <summary>A test rig's HAT emulator, and who may reach the rig.</summary>
public sealed record RigSettings
{
    public const double DefaultTimeScale = 1;
    public const double MinimumTimeScale = 0.1;
    public const double MaximumTimeScale = 100;
    public const double DefaultCameraFramesPerSecond = 5;
    public const double MinimumCameraFramesPerSecond = 0.1;
    public const double MaximumCameraFramesPerSecond = 30;

    /// <summary>How many times as fast as real time the emulated roof runs (the controller's own timing is not scaled).</summary>
    public double TimeScale { get; init; } = DefaultTimeScale;

    /// <summary>The emulated camera's frame rate.</summary>
    public double CameraFramesPerSecond { get; init; } = DefaultCameraFramesPerSecond;

    /// <summary>
    /// True to publish the rig's API and web UI on every address, so other machines can use it; by default they are
    /// published on this machine's loopback address only. Needs HTTPS.
    /// </summary>
    public bool OpenToLan { get; init; }

    public IEnumerable<string> Problems()
    {
        if (!double.IsFinite(TimeScale) || TimeScale is < MinimumTimeScale or > MaximumTimeScale)
        {
            yield return $"The rig's time scale must be from {MinimumTimeScale.ToString(CultureInfo.InvariantCulture)} to {MaximumTimeScale.ToString(CultureInfo.InvariantCulture)}, not {TimeScale.ToString(CultureInfo.InvariantCulture)}.";
        }

        if (!double.IsFinite(CameraFramesPerSecond) || CameraFramesPerSecond is < MinimumCameraFramesPerSecond or > MaximumCameraFramesPerSecond)
        {
            yield return $"The rig's camera frame rate must be from {MinimumCameraFramesPerSecond.ToString(CultureInfo.InvariantCulture)} to {MaximumCameraFramesPerSecond.ToString(CultureInfo.InvariantCulture)} frames a second, not {CameraFramesPerSecond.ToString(CultureInfo.InvariantCulture)}.";
        }
    }
}

/// <summary>The addresses the controller reaches other servers at: a camera, a telemetry collector.</summary>
public static class WebAddress
{
    /// <summary>
    /// Why <paramref name="address"/> is not an absolute http or https address the installer may record, ending a
    /// sentence that starts with what it is; null when it is one. It may not carry a user name or password, which would
    /// be a secret in a file that is not one, nor a query or fragment.
    /// </summary>
    public static string? Problem(string? address, bool allowPath)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
        {
            return "must be an absolute http or https address (http://192.168.0.4:81).";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "may not hold a user name or password: the installer keeps those out of its files.";
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return "may not have a query or a fragment.";
        }

        return !allowPath && uri.AbsolutePath != "/" ? "must be the server alone, with no path." : null;
    }
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
