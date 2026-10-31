using System.Net;
using IPNetwork = System.Net.IPNetwork;

namespace HVO.RoofControllerV4.Alpaca.Security;

/// <summary>The address ranges the Alpaca server treats as its LAN.</summary>
public static class AlpacaNetworks
{
    /// <summary>
    /// Loopback, the private IPv4 ranges (RFC 1918), IPv4 link-local, IPv6 unique local and link-local: the default
    /// <see cref="AlpacaOptions.AllowedNetworks"/>, and the bounds every allowed network must stay within. Carrier-grade
    /// NAT (100.64.0.0/10, which overlay VPNs use) is not among them.
    /// </summary>
    public static IReadOnlyList<IPNetwork> Local { get; } =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    /// <summary>An IPv4 address mapped into IPv6 (a dual-stack socket's view) as plain IPv4.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>True when <paramref name="address"/> is within <paramref name="networks"/>.</summary>
    public static bool Contains(IReadOnlyList<IPNetwork> networks, IPAddress address)
    {
        var normalized = Normalize(address);
        foreach (var network in networks)
        {
            if (network.BaseAddress.AddressFamily == normalized.AddressFamily && network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="address"/> is loopback or private (<see cref="Local"/>).</summary>
    public static bool IsLocal(IPAddress address) => Contains(Local, address);

    /// <summary>True when every address of <paramref name="network"/> is within <see cref="Local"/>.</summary>
    public static bool IsWithinLocal(IPNetwork network)
        => Local.Any(local => local.BaseAddress.AddressFamily == network.BaseAddress.AddressFamily
            && local.PrefixLength <= network.PrefixLength
            && local.Contains(network.BaseAddress));

    /// <summary>True when every one of <paramref name="addresses"/> is a loopback address.</summary>
    public static bool AllLoopback(IEnumerable<IPAddress> addresses) => addresses.All(IPAddress.IsLoopback);
}
