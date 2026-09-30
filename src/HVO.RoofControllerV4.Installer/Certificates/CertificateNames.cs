using System.Net;
using System.Net.Sockets;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Certificates;

/// <summary>
/// Every name and address clients may use to reach the controller, which its certificate names and its
/// <c>AllowedHosts</c> lists:
/// <list type="bullet">
/// <item>the short names: this machine's host name, and the other names the person lists;</item>
/// <item>each short name alone, under <c>.local</c>, and under each domain the person lists;</item>
/// <item><c>localhost</c>;</item>
/// <item>the machine's private addresses, <c>127.0.0.1</c> and <c>::1</c>.</item>
/// </list>
/// Addresses on container and virtual networks are not included, and neither are link-local addresses. Public addresses
/// are left out (<see cref="LeftOut"/>), because the CA may not issue for them.
/// </summary>
public sealed record CertificateNames(
    IReadOnlyList<string> ShortNames,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<NetworkAddress> LeftOut)
{
    /// <summary>The resolver's configuration, whose search domains the installer suggests.</summary>
    public const string ResolverConfiguration = "/etc/resolv.conf";

    // Container, virtual machine and Apple peer-to-peer networks: nothing on the LAN reaches the controller there.
    private static readonly string[] VirtualInterfaces = ["docker", "br-", "veth", "virbr", "cni", "flannel", "vmnet", "vboxnet", "bridge", "awdl", "llw"];

    // Domains a resolver may search that are not the network's own.
    private static readonly string[] NotDomains = ["local", "localdomain", "lan.local"];

    /// <summary>The name the certificate is issued to: this machine's short host name.</summary>
    public string Host => ShortNames[0];

    /// <summary>The names and addresses as <c>AllowedHosts</c> takes them: separated by semicolons, IPv6 in brackets.</summary>
    public string AllowedHosts
        => string.Join(';', DnsNames.Concat(Addresses.Select(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())));

    /// <summary>The names and addresses for the controller on <paramref name="machine"/> with <paramref name="settings"/>.</summary>
    public static CertificateNames For(InstallerMachine machine, ControllerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(settings);
        var host = ShortName(machine.HostName);
        var shortNames = DnsName.Distinct([.. host is null ? [] : new[] { host }, .. settings.HostNames]);
        if (shortNames.Count == 0)
        {
            shortNames = ["localhost"];
        }

        var domains = DnsName.Distinct(settings.Domains.Select(domain => domain.TrimEnd('.')));
        var dnsNames = DnsName.Distinct(shortNames
            .SelectMany(name => new[] { name, $"{name}.local" }.Concat(domains.Select(domain => $"{name}.{domain}")))
            .Append("localhost"));

        var addresses = new List<IPAddress>();
        var leftOut = new List<NetworkAddress>();
        foreach (var found in machine.NetworkAddresses())
        {
            var address = found.Address;
            if (IsVirtual(found.Interface) || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast
                || address.IsIPv4MappedToIPv6 || address.IsIPv6SiteLocal || IsIPv4LinkLocal(address) || IsIPv4Multicast(address))
            {
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address = new IPAddress(address.GetAddressBytes());
            }

            if (NameConstraints.PrivateNetworks.Any(network => network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address)))
            {
                addresses.Add(address);
            }
            else
            {
                leftOut.Add(found with { Address = address });
            }
        }

        addresses.Add(IPAddress.Loopback);
        addresses.Add(IPAddress.IPv6Loopback);
        return new CertificateNames(shortNames, domains, dnsNames, addresses.Distinct().ToArray(), leftOut.Distinct().ToArray());
    }

    /// <summary>
    /// The domains this machine seems to be in: its resolver's search domains and its host name's own. The wizard offers
    /// them, but the CA only ever issues for the domains the person lists.
    /// </summary>
    public static IReadOnlyList<string> SuggestedDomains(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var found = new List<string>();
        var dot = machine.HostName.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            found.Add(machine.HostName[(dot + 1)..]);
        }

        string? resolver = null;
        try
        {
            resolver = machine.ReadText(ResolverConfiguration);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var line in (resolver ?? string.Empty).Split('\n'))
        {
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words is ["search" or "domain", .. var domains])
            {
                found.AddRange(domains);
            }
        }

        return DnsName.Distinct(found.Select(domain => domain.TrimEnd('.')))
            .Where(domain => DnsName.IsDomain(domain) && !NotDomains.Contains(domain, StringComparer.Ordinal))
            .ToArray();
    }

    /// <summary>The first label of <paramref name="hostName"/> in lower case, or null when it is not a DNS label.</summary>
    public static string? ShortName(string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        var label = hostName.Split('.')[0].Trim().ToLowerInvariant();
        return DnsName.IsLabel(label) ? label : null;
    }

    private static bool IsVirtual(string name) => VirtualInterfaces.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsIPv4LinkLocal(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..];

    private static bool IsIPv4Multicast(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224;
}
