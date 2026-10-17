using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Client;
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

    /// <summary>Explicit opt-in host authentication. Omitted leaves legacy authentication unchanged.</summary>
    public LinuxAuthSettings? LinuxAuth { get; init; }

    /// <summary>
    /// The controller container's resource limits (docs/deployment.md, "The container's privileges and limits"): the
    /// process, CPU and memory limits, RoofController's default for each one not given. Kept in the install record, so an
    /// upgrade and a rollback give the container the same.
    /// </summary>
    public ContainerResources Resources { get; init; } = new();

    /// <summary>
    /// An <c>appsettings.Local.json</c> from a backup (its full path), to start the controller with: copied into the
    /// settings folder as it is when the controller has no settings yet, and never over settings it has. Not recorded:
    /// an import is done once.
    /// </summary>
    public string? ImportSettingsFrom { get; init; }

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

        foreach (var problem in (FirstAdmin?.Problems() ?? []).Concat(Camera?.Problems() ?? []).Concat(Rig?.Problems() ?? []).Concat(LinuxAuth?.Problems() ?? [])
            .Concat((Resources ?? new ContainerResources()).Problems()))
        {
            yield return problem;
        }

        if (!string.IsNullOrWhiteSpace(TelemetryEndpoint) && WebAddress.Problem(TelemetryEndpoint, allowPath: true) is { } telemetry)
        {
            yield return $"The telemetry endpoint {telemetry}";
        }

        if (ImportSettingsFrom is { } backup && !Path.IsPathFullyQualified(backup))
        {
            yield return $"The settings to import ({backup}) must be given by their full path (/home/pi/backup/appsettings.Local.json).";
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
        TelemetryEndpoint = string.IsNullOrWhiteSpace(TelemetryEndpoint) ? null : TelemetryEndpoint.Trim(),
        ImportSettingsFrom = string.IsNullOrWhiteSpace(ImportSettingsFrom) ? null : ImportSettingsFrom.Trim(),
        Resources = (Resources ?? new ContainerResources()).Normalised()
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
            && Equals(Rig, other.Rig)
            && Equals(LinuxAuth, other.LinuxAuth)
            && string.Equals(ImportSettingsFrom, other.ImportSettingsFrom, StringComparison.Ordinal)
            && Equals(Resources, other.Resources);

    public override int GetHashCode()
        => HashCode.Combine(Connection, HttpsPort, HttpPort, WebPort, HostNames.Count, Domains.Count, HashCode.Combine(FirstAdmin, Camera, TelemetryEndpoint, Rig, ImportSettingsFrom, LinuxAuth, Resources));

    public static string Describe(ConnectionMode mode) => mode switch
    {
        ConnectionMode.PrivateCa => "HTTPS, with a certificate from this installer's certificate authority (recommended)",
        ConnectionMode.OwnCertificate => "HTTPS, with your own certificate",
        ConnectionMode.SelfSigned => "HTTPS, with a self-signed certificate that each client pins",
        ConnectionMode.Http => "HTTP, with no encryption (only on a network you trust)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}

/// <summary>
/// The controller container's resource limits, as the deploy script gives them (CONTAINER_CPUS, CONTAINER_MEMORY and
/// CONTAINER_PIDS_LIMIT; docs/deployment.md, "The container's privileges and limits"), always: each one not set is
/// RoofController's default, 1 vCPU and 2 GiB (owner decision on #149) and 512 processes. A CPU or memory limit not set
/// is recorded as none, so that an upgrade moves it to the installer's default, while one an operator set is recorded and
/// kept, as SkyMonitor's instance manifest keeps its own. One that is set is at least the RoofController floor of
/// 1 vCPU and 1 GiB (owner decision on #129, 2026-10-16); less is refused, with no exception, and there is no setting
/// for no limit. The container gets no swap beyond its memory. The kernel ends a controller that reaches its memory
/// limit, and the container's supervisor then turns the relays off (docs/deployment.md, "The relays when the controller
/// exits").
/// </summary>
public sealed partial record ContainerResources
{
    public const int DefaultPidsLimit = 512;

    /// <summary>RoofController's default CPU limit, for one not set: 1 vCPU.</summary>
    public const decimal DefaultCpus = 1;

    /// <summary>RoofController's default memory limit, for one not set: 2 GiB.</summary>
    public const string DefaultMemory = "2g";

    /// <summary>RoofController's minimum CPUs, for a CPU limit that is set.</summary>
    public const decimal MinimumCpus = 1;

    /// <summary>RoofController's minimum memory, for a memory limit that is set: 1 GiB.</summary>
    public const long MinimumMemoryBytes = 1L * 1024 * 1024 * 1024;

    public const int MinimumPidsLimit = 256;
    public const int MaximumPidsLimit = 32768;

    /// <summary>CPUs, as <c>docker run --cpus</c> takes them (2, 2.5), with at most two decimals; none for the default (<see cref="DefaultCpus"/>).</summary>
    public decimal? Cpus { get; init; }

    /// <summary>Memory, as a whole number of MiB or GiB (1024m, 1g); none for the default (<see cref="DefaultMemory"/>).</summary>
    public string? Memory { get; init; }

    /// <summary>The most processes and threads the container runs at once (<c>docker run --pids-limit</c>).</summary>
    public int PidsLimit { get; init; } = DefaultPidsLimit;

    /// <summary>The CPU limit the container is given: the one set, or the default.</summary>
    [JsonIgnore]
    public decimal CpusOrDefault => Cpus ?? DefaultCpus;

    /// <summary>The memory limit the container is given: the one set, or the default.</summary>
    [JsonIgnore]
    public string MemoryOrDefault => Memory ?? DefaultMemory;

    /// <summary>The memory limit the container is given, in bytes; null when <see cref="Memory"/> is not a whole number of MiB or GiB.</summary>
    [JsonIgnore]
    public long? MemoryBytes
        => MemoryPattern().Match(MemoryOrDefault) is { Success: true } match
            && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number * (char.ToLowerInvariant(match.Groups[2].Value[0]) == 'g' ? 1024L * 1024 * 1024 : 1024L * 1024)
                : null;

    /// <summary>The CPU limit the container is given, as Docker counts it (HostConfig.NanoCpus).</summary>
    [JsonIgnore]
    public long NanoCpus => (long)(CpusOrDefault * 1_000_000_000m);

    /// <summary>The CPU limit the container is given, as the deploy script and Docker write it (1, 2.5).</summary>
    [JsonIgnore]
    public string CpusText => CpusOrDefault.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The limits, as the plan and the deploy script say them.</summary>
    public string Describe()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(CpusOrDefault == 1 ? "1 CPU" : $"{CpusText} CPUs")}, {MemoryOrDefault} of memory and no swap, at most {PidsLimit} processes and threads");

    public IEnumerable<string> Problems()
    {
        if (Cpus is { } cpus)
        {
            if (cpus <= 0 || cpus > 999 || decimal.Round(cpus, 2) != cpus)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"The controller's CPUs (controller.resources.cpus) must be a number such as 2 or 2.5, with at most two decimals, not {cpus}.");
            }
            else if (cpus < MinimumCpus)
            {
                yield return $"The controller's CPU limit of {CpusText} (controller.resources.cpus, or --cpus) is less than 1 vCPU, the minimum (RoofController's): give at least 1, or leave it out for the default of 1.";
            }
        }

        if (Memory is not null)
        {
            if (MemoryBytes is not { } memory)
            {
                yield return $"The controller's memory (controller.resources.memory) must be a whole number of MiB or GiB, such as 1024m or 1g, not '{Memory}'.";
            }
            else if (memory < MinimumMemoryBytes)
            {
                yield return $"The controller's memory limit of {Memory} (controller.resources.memory, or --memory) is less than 1g, the minimum (RoofController's): give at least 1g, or leave it out for the default of {DefaultMemory}.";
            }
        }

        if (PidsLimit is < MinimumPidsLimit or > MaximumPidsLimit)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"The controller's process limit (controller.resources.pidsLimit) must be from {MinimumPidsLimit} to {MaximumPidsLimit}, not {PidsLimit}.");
        }
    }

    /// <summary>
    /// These limits with the memory written plainly (4G as 4g, 04096m as 4096m), the CPUs without trailing zeros, and an
    /// empty memory as none.
    /// </summary>
    public ContainerResources Normalised() => this with
    {
        Cpus = Cpus is { } cpus && decimal.Round(cpus, 2) == cpus ? decimal.Parse(CpusText, NumberStyles.Number, CultureInfo.InvariantCulture) : Cpus,
        Memory = string.IsNullOrWhiteSpace(Memory)
            ? null
            : MemoryPattern().Match(Memory) is { Success: true } match
                && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? string.Create(CultureInfo.InvariantCulture, $"{number}{char.ToLowerInvariant(match.Groups[2].Value[0])}")
                    : Memory.Trim()
    };

    // As the deploy script reads CONTAINER_MEMORY.
    [GeneratedRegex("^([0-9]{1,6})([mMgG])$")]
    private static partial Regex MemoryPattern();
}

/// <summary>
/// The limits given on the command line (<c>--cpus</c>, <c>--memory</c> and <c>--pids-limit</c>): each one given replaces
/// the answers', the record's or the default.
/// </summary>
public sealed record ContainerResourcesOverride(decimal? Cpus, string? Memory, int? PidsLimit)
{
    /// <summary>True when nothing is given.</summary>
    public bool IsEmpty => Cpus is null && Memory is null && PidsLimit is null;

    public ContainerResources Apply(ContainerResources? resources)
    {
        var current = resources ?? new ContainerResources();
        return (current with
        {
            Cpus = Cpus ?? current.Cpus,
            Memory = Memory ?? current.Memory,
            PidsLimit = PidsLimit ?? current.PidsLimit
        }).Normalised();
    }
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
    /// be a secret in a file that is not one, nor a query or fragment. <paramref name="example"/> is the address the
    /// sentence gives as an example.
    /// </summary>
    public static string? Problem(string? address, bool allowPath, string example = "http://192.168.0.4:81")
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
        {
            return $"must be an absolute http or https address ({example}).";
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

/// <summary>Where the Mac app goes, and who makes its device key.</summary>
public sealed record MacAppSettings
{
    public const string SharedFolder = "/Applications";
    public const string HomeFolder = "~/Applications";

    public string Folder { get; init; } = SharedFolder;

    /// <summary>
    /// An admin on the controller, by the name they sign in with: they sign in once, with a password typed or given in a
    /// file, so the installer can add the Mac's device key (a viewer's). Their session ends as soon as the key is made.
    /// </summary>
    public string Admin { get; init; } = string.Empty;

    public static IReadOnlyList<string> Folders { get; } = [SharedFolder, HomeFolder];

    public IEnumerable<string> Problems()
    {
        if (!Folders.Contains(Folder))
        {
            yield return $"The Mac app's folder must be {string.Join(" or ", Folders)}, not '{Folder}'.";
        }

        if (Admin.Length > 0 && !RoofIdentityContract.IsValidName(Admin))
        {
            yield return $"'{Admin}' is not a name the controller takes: give the admin who makes the Mac's device key by the name they sign in with.";
        }
    }

    public MacAppSettings Normalised() => this with { Admin = Admin.Trim() };
}

/// <summary>
/// The controller hvo-roof and the Mac app on this machine use, and how they trust its certificate: by its CA (a private
/// CA, fetched from the controller and saved only when its fingerprint is the one given here), by a pin on its own
/// certificate (a self-signed one), or as the machine trusts any website (a certificate of your own, or plain HTTP).
/// </summary>
public sealed record ClientSettings
{
    /// <summary>The controller's address, as clients reach it: https://roof.local:8443.</summary>
    public string Controller { get; init; } = string.Empty;

    /// <summary>
    /// The SHA-256 fingerprint of the controller's CA, as the controller's installer showed it on its Done page and
    /// <c>hvo-roof-install cert show</c> shows it: the CA the controller serves is trusted only when it matches.
    /// </summary>
    public string? CaSha256 { get; init; }

    /// <summary>The SHA-256 fingerprint of the controller's self-signed certificate, which each connection is pinned to.</summary>
    public string? CertificateSha256 { get; init; }

    /// <summary>
    /// On a Mac, true to trust the CA in the login keychain too, so Safari and Chrome open the web UI without a warning.
    /// Firefox keeps its own list: the installer says how to add the CA to it.
    /// </summary>
    public bool TrustInKeychain { get; init; }

    /// <summary>The controller's address, when it is one.</summary>
    [JsonIgnore]
    public Uri? Address => WebAddress.Problem(Controller, allowPath: false) is null ? new Uri(Controller.Trim()) : null;

    public IEnumerable<string> Problems()
    {
        if (WebAddress.Problem(Controller, allowPath: false, "https://roof.local:8443") is { } problem)
        {
            yield return $"The controller's address {problem}";
        }

        foreach (var (name, value) in new[] { ("CA's", CaSha256), ("certificate's", CertificateSha256) })
        {
            if (value is not null && Colons(value) is null)
            {
                yield return $"The controller's {name} fingerprint must be 64 hex digits (colons between pairs allowed), not '{value}'.";
            }
        }

        if (CaSha256 is not null && CertificateSha256 is not null)
        {
            yield return "Give the controller's CA's fingerprint or its certificate's, not both: a private CA's, or a self-signed certificate's.";
        }

        if (Address is { } address && address.Scheme == Uri.UriSchemeHttp && (CaSha256 is not null || CertificateSha256 is not null))
        {
            yield return "A controller reached over plain HTTP has no certificate to trust: give its https address, or no fingerprint.";
        }

        if (TrustInKeychain && CaSha256 is null)
        {
            yield return "Only the controller's CA can be trusted in the keychain: give its fingerprint too.";
        }
    }

    /// <summary>These choices with the address trimmed, and each fingerprint as colon-separated upper-case pairs.</summary>
    public ClientSettings Normalised() => this with
    {
        Controller = Controller.Trim(),
        CaSha256 = string.IsNullOrWhiteSpace(CaSha256) ? null : Colons(CaSha256) ?? CaSha256.Trim(),
        CertificateSha256 = string.IsNullOrWhiteSpace(CertificateSha256) ? null : Colons(CertificateSha256) ?? CertificateSha256.Trim()
    };

    /// <summary>
    /// A SHA-256 fingerprint as colon-separated upper-case pairs (as <c>hvo-roof-install cert show</c> gives it), from 64
    /// hex digits in either case with colons, spaces or dashes between them; null when it is not one.
    /// </summary>
    public static string? Colons(string? text)
    {
        if (!RoofCertificatePin.IsValid(text?.Trim()))
        {
            return null;
        }

        var hex = new string(text!.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        return string.Join(':', hex.Chunk(2).Select(pair => new string(pair)));
    }
}

/// <summary>The kiosk's choices: the touchscreen on the controller's own Pi.</summary>
public sealed record KioskSettings
{
    /// <summary>Null preserves the existing selection; empty selects a connected display on each boot. Prefer a by-path link for an explicit selection.</summary>
    public string? Card { get; init; }

    /// <summary>Optional clockwise display rotation; null preserves the installed setting.</summary>
    public int? Rotation { get; init; }

    /// <summary>Null preserves the installed DRM mode; empty restores the preferred mode.</summary>
    public string? VideoMode { get; init; }

    /// <summary>Optional boot splash setup. Null leaves any existing splash alone.</summary>
    public KioskSplashSettings? Splash { get; init; }

    /// <summary>
    /// True (the default) to hide the console's blinking cursor, which shows through the kiosk's screen otherwise: a
    /// setting in the Pi's <c>cmdline.txt</c>, which takes effect at the next reboot.
    /// </summary>
    public bool HideCursor { get; init; } = true;

    /// <summary>
    /// People already on the controller, by the name they sign in with, to give a PIN for signing in at the kiosk: each
    /// PIN is typed, or given in a file (<c>--pin-file NAME=FILE</c>). Someone who has a PIN keeps it. Not recorded: PINs
    /// are set once, and changed in the web UI.
    /// </summary>
    public IReadOnlyList<string> Pins { get; init; } = [];

    public IEnumerable<string> Problems()
    {
        if (!string.IsNullOrEmpty(VideoMode) && !RoofDisplayMode.TryParse(VideoMode, out _, out _))
            yield return "The kiosk videoMode must be WIDTHxHEIGHT, each from 200 to 8000 pixels, or empty for preferred.";
        if (Rotation is not null and not (0 or 90 or 180 or 270))
            yield return "The kiosk rotation must be 0, 90, 180 or 270.";
        if (!string.IsNullOrEmpty(Card) && !Card.StartsWith("/dev/dri/by-path/", StringComparison.Ordinal))
            yield return "Use an empty kiosk card for connected-display discovery, or an explicit /dev/dri/by-path/ link; numbered cards are not stable.";
        if (Splash is { Enabled: true } splash && (string.IsNullOrWhiteSpace(splash.Image) || !Path.IsPathFullyQualified(splash.Image)))
            yield return "An enabled kiosk splash needs an absolute path to an operator-supplied PNG image.";
        foreach (var name in Pins.Where(name => !RoofIdentityContract.IsValidName(name)))
        {
            yield return $"'{name}' is not a name the controller takes: give the kiosk's PINs to people by the name they sign in with.";
        }
    }

    /// <summary>These choices with the names trimmed, each once, in order.</summary>
    public KioskSettings Normalised() => this with
    {
        Pins = Pins.Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    };

    public bool Equals(KioskSettings? other)
        => other is not null
            && HideCursor == other.HideCursor
            && Card == other.Card && VideoMode == other.VideoMode && Rotation == other.Rotation && Splash == other.Splash
            && Pins.SequenceEqual(other.Pins, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(HideCursor, Pins.Count, Card, Rotation, Splash, VideoMode);
}

/// <summary>Opt-in Plymouth artwork supplied by the operator, never downloaded by the installer.</summary>
public sealed record KioskSplashSettings
{
    /// <summary>False restores the configuration this installer owned before enabling the splash.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Absolute path to a PNG the operator has permission to use. Keep it available for future upgrades.</summary>
    public string? Image { get; init; }
}
