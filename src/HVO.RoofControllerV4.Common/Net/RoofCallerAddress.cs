using System.Net;
using System.Net.Sockets;

namespace HVO.RoofControllerV4.Common.Net;

/// <summary>
/// The address a limit counts a caller by, shared by the controller's sign-in limit and the web UI's Stop limit.
/// </summary>
public static class RoofCallerAddress
{
    /// <summary>
    /// An IPv4 address as it is (also when mapped into IPv6). A public IPv6 address from another network is counted by
    /// its /64, since one host there usually holds a whole /64 and can pick a new address for every attempt. On a LAN
    /// every host shares one /64, so a loopback, link-local or unique local address, or one in the same /64 as
    /// <paramref name="local"/> (the address the request came in on), is counted as itself.
    /// </summary>
    public static string Of(IPAddress? address, IPAddress? local = null)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || (local is { AddressFamily: AddressFamily.InterNetworkV6 } && Network64(local) == Network64(address)))
        {
            return address.ToString();
        }

        return Network64(address) + "/64";
    }

    private static string Network64(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }
}
