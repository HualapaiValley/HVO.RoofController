using System.Net;
using HVO.RoofControllerV4.Alpaca.Security;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Alpaca;

/// <summary>
/// The Alpaca server's settings: the <c>Alpaca</c> section, from the environment (<c>Alpaca__*</c>) or the command line
/// (<c>--Alpaca:Enabled=true</c>). Off by default: the server listens only once it is enabled and told where to listen.
/// </summary>
public sealed class AlpacaOptions
{
    public const string SectionName = "Alpaca";

    /// <summary>The Alpaca port by convention.</summary>
    public const int DefaultPort = 11111;

    /// <summary>The longest <see cref="Location"/>.</summary>
    public const int MaximumLocationLength = 128;

    /// <summary>
    /// Whether the Alpaca server runs at all. False (the default): the program says so and ends without listening or
    /// answering discovery.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The addresses the server listens on, required once it is enabled: this computer's LAN address (the usual choice),
    /// 127.0.0.1 for this computer alone, or 0.0.0.0 in a container whose published port is bound to the LAN address.
    /// Never a public address.
    /// </summary>
    public string[] ListenAddresses { get; set; } = [];

    /// <summary>The server's port. Default 11111.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// The networks (CIDR, such as 192.168.1.0/24) whose clients the server answers, discovery included. None: loopback
    /// and the private ranges (<see cref="AlpacaNetworks.Local"/>). A network beyond them is refused.
    /// </summary>
    public string[] AllowedNetworks { get; set; } = [];

    /// <summary>
    /// Host names a request's <c>Host</c> header may name besides an IP address, <c>localhost</c> and this computer's
    /// own name: for example <c>observatory-roof.local</c>. Any other name is refused (DNS rebinding).
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>The controller's API. Default http://localhost:8080.</summary>
    public Uri ControllerUrl { get; set; } = new("http://localhost:8080");

    /// <summary>
    /// A file holding the server's own API key for the controller, on one line, readable only by the server's user. A
    /// Viewer key is all this phase needs: it reads the status and sends Stop, which every role may send.
    /// </summary>
    public string? ControllerApiKeyFile { get; set; }

    /// <summary>For a controller with a self-signed certificate: its SHA-256, 64 hex digits.</summary>
    public string? ControllerCertificateSha256 { get; set; }

    /// <summary>For a controller whose certificate a private CA issued (the installer's): that CA's certificate file.</summary>
    public string? ControllerCaCertificatePath { get; set; }

    /// <summary>
    /// Where the server keeps its device identities (<c>alpaca-devices.json</c>): made at the first start and never
    /// changed, so clients keep recognising the roof. None: <c>~/.local/share/hvo-roof-alpaca</c>.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>The <c>Location</c> the management API reports, such as the observatory's name. Default empty.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Requests one client address may make per second, on average, before it is refused (HTTP 429). Default 100.</summary>
    public int RequestsPerSecondPerAddress { get; set; } = 100;

    /// <summary>The most clients connected at once; connecting one more forgets the one idle longest. Default 32.</summary>
    public int MaxConnectedClients { get; set; } = 32;

    /// <summary>How long a client that sends nothing stays connected, in minutes. Default 60.</summary>
    public int ConnectionIdleMinutes { get; set; } = 60;

    /// <summary>The discovery responder (UDP).</summary>
    public AlpacaDiscoveryOptions Discovery { get; set; } = new();

    /// <summary>The networks the server answers: <see cref="AllowedNetworks"/>, or the local ranges when it names none.</summary>
    public IReadOnlyList<System.Net.IPNetwork> EffectiveAllowedNetworks
        => AllowedNetworks is { Length: > 0 }
            ? AllowedNetworks.Select(n => System.Net.IPNetwork.Parse(n.Trim())).ToArray()
            : AlpacaNetworks.Local;

    /// <summary>The listen addresses as parsed.</summary>
    public IReadOnlyList<IPAddress> ParsedListenAddresses => ListenAddresses.Select(a => IPAddress.Parse(a.Trim())).ToArray();

    /// <summary>The port discovery advertises: <see cref="AlpacaDiscoveryOptions.AdvertisedPort"/>, or <see cref="Port"/>.</summary>
    public int AdvertisedPort => Discovery.AdvertisedPort ?? Port;

    /// <summary>The device identities' directory: <see cref="DataPath"/>, or the default under the user's data directory.</summary>
    public string EffectiveDataPath => !string.IsNullOrWhiteSpace(DataPath)
        ? DataPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hvo-roof-alpaca");

    /// <summary>
    /// The problems with these settings, empty when they are valid. <paramref name="discoveryOnly"/>: only what the
    /// discovery responder alone needs.
    /// </summary>
    public IReadOnlyList<string> Validate(bool discoveryOnly = false)
    {
        var problems = new List<string>();
        if (Port is < 1 or > 65535)
        {
            problems.Add($"{SectionName}:Port must be between 1 and 65535, got {Port}.");
        }

        ValidateNetworks(problems);
        Discovery.Validate(problems);
        if (discoveryOnly)
        {
            return problems;
        }

        ValidateListenAddresses(problems);
        foreach (var host in AllowedHosts)
        {
            if (Uri.CheckHostName(host?.Trim()) != UriHostNameType.Dns)
            {
                problems.Add($"{SectionName}:AllowedHosts has '{host}', which is not a host name.");
            }
        }

        if (!ControllerUrl.IsAbsoluteUri || ControllerUrl.Scheme is not ("http" or "https"))
        {
            problems.Add($"{SectionName}:ControllerUrl must be an absolute http or https URL, such as http://localhost:8080.");
        }

        if (string.IsNullOrWhiteSpace(ControllerApiKeyFile))
        {
            problems.Add($"{SectionName}:ControllerApiKeyFile must name the file holding the server's API key for the controller (a Viewer key).");
        }

        if (!string.IsNullOrWhiteSpace(ControllerCertificateSha256))
        {
            if (!string.IsNullOrWhiteSpace(ControllerCaCertificatePath))
            {
                problems.Add($"{SectionName}: set ControllerCertificateSha256 or ControllerCaCertificatePath, not both.");
            }
            else if (!RoofCertificatePin.IsValid(ControllerCertificateSha256))
            {
                problems.Add($"{SectionName}:ControllerCertificateSha256 must be a SHA-256 hash: 64 hex digits.");
            }
        }

        if (Location.Length > MaximumLocationLength)
        {
            problems.Add($"{SectionName}:Location must be at most {MaximumLocationLength} characters.");
        }

        if (RequestsPerSecondPerAddress is < 1 or > 10_000)
        {
            problems.Add($"{SectionName}:RequestsPerSecondPerAddress must be between 1 and 10000, got {RequestsPerSecondPerAddress}.");
        }

        if (MaxConnectedClients is < 1 or > 1024)
        {
            problems.Add($"{SectionName}:MaxConnectedClients must be between 1 and 1024, got {MaxConnectedClients}.");
        }

        if (ConnectionIdleMinutes is < 1 or > 10_080)
        {
            problems.Add($"{SectionName}:ConnectionIdleMinutes must be between 1 and 10080 (a week), got {ConnectionIdleMinutes}.");
        }

        return problems;
    }

    private void ValidateListenAddresses(List<string> problems)
    {
        if (ListenAddresses is not { Length: > 0 })
        {
            problems.Add($"{SectionName}:ListenAddresses must name the address the server listens on: this computer's LAN address, 127.0.0.1, or 0.0.0.0 in a container (docs/alpaca.md).");
            return;
        }

        foreach (var text in ListenAddresses)
        {
            if (!IPAddress.TryParse(text?.Trim(), out var address))
            {
                problems.Add($"{SectionName}:ListenAddresses has '{text}', which is not an IP address.");
            }
            else if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !AlpacaNetworks.IsLocal(address))
            {
                problems.Add($"{SectionName}:ListenAddresses has {address}, which is not a LAN address. The Alpaca server never listens on a public address.");
            }
        }
    }

    private void ValidateNetworks(List<string> problems)
    {
        foreach (var text in AllowedNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(text?.Trim(), out var network))
            {
                problems.Add($"{SectionName}:AllowedNetworks has '{text}', which is not a network such as 192.168.1.0/24.");
            }
            else if (!AlpacaNetworks.IsWithinLocal(network))
            {
                problems.Add($"{SectionName}:AllowedNetworks has {network}, which reaches beyond loopback and the private ranges. The Alpaca server answers only its LAN.");
            }
        }
    }
}

/// <summary>The discovery responder's settings: <c>Alpaca:Discovery</c>.</summary>
public sealed class AlpacaDiscoveryOptions
{
    /// <summary>The Alpaca discovery port.</summary>
    public const int DefaultPort = 32227;

    /// <summary>Whether to answer discovery (only while the server is enabled). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The UDP port discovery listens on. Default 32227.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// The port discovery tells clients, when it is not <see cref="AlpacaOptions.Port"/>: for example the port a container
    /// publishes the server on. None: <see cref="AlpacaOptions.Port"/>.
    /// </summary>
    public int? AdvertisedPort { get; set; }

    /// <summary>Whether to answer IPv6 discovery (multicast ff12::a1:9aca) as well as IPv4 broadcasts. Default false.</summary>
    public bool IPv6 { get; set; }

    internal void Validate(List<string> problems)
    {
        if (Port is < 1 or > 65535)
        {
            problems.Add($"{AlpacaOptions.SectionName}:Discovery:Port must be between 1 and 65535, got {Port}.");
        }

        if (AdvertisedPort is < 1 or > 65535)
        {
            problems.Add($"{AlpacaOptions.SectionName}:Discovery:AdvertisedPort must be between 1 and 65535, got {AdvertisedPort}.");
        }
    }
}

/// <summary>The Alpaca server's settings cannot be used. The message names the settings, never a secret.</summary>
public sealed class AlpacaSettingsException(string message) : Exception(message);
