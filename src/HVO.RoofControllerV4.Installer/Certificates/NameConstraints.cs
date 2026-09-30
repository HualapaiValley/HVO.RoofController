using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace HVO.RoofControllerV4.Installer.Certificates;

/// <summary>
/// What the installer's certificate authority may issue for (RFC 5280's name constraints, permitted subtrees only): the
/// private networks, loopback, <c>local</c>, <c>localhost</c>, and the short names and domains the person lists. A
/// client that trusts the CA refuses a certificate for anything else, so a stolen Pi, or its CA's key, cannot pass for
/// another site to it.
/// </summary>
public sealed record NameConstraints(IReadOnlyList<string> DnsNames, IReadOnlyList<IPNetwork> Networks)
{
    /// <summary>The name constraints extension's OID.</summary>
    public const string Oid = "2.5.29.30";

    private static readonly Asn1Tag Permitted = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag DnsTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag AddressTag = new(TagClass.ContextSpecific, 7);

    /// <summary>The private IPv4 ranges, IPv4's loopback, IPv6's unique-local addresses and its loopback.</summary>
    public static IReadOnlyList<IPNetwork> PrivateNetworks { get; } =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("::1/128")
    ];

    /// <summary>The names a CA for <paramref name="names"/> may issue for.</summary>
    public static NameConstraints For(CertificateNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new(["local", "localhost", .. names.ShortNames, .. names.Domains], PrivateNetworks);
    }

    /// <summary>True when <paramref name="dnsName"/> is one of the names, or under one (RFC 5280: labels added on the left).</summary>
    public bool Permits(string dnsName)
    {
        ArgumentNullException.ThrowIfNull(dnsName);
        var name = dnsName.TrimEnd('.');
        return DnsNames.Any(permitted =>
            name.Equals(permitted, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + permitted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="address"/> is in one of the networks.</summary>
    public bool Permits(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Networks.Any(network => network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address));
    }

    /// <summary>The DNS names and addresses of <paramref name="names"/> these do not permit.</summary>
    public IReadOnlyList<string> NotPermitted(CertificateNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.DnsNames.Where(name => !Permits(name))
            .Concat(names.Addresses.Where(address => !Permits(address)).Select(address => address.ToString()))
            .ToArray();
    }

    /// <summary>The extension, critical, as the CA's certificate carries it.</summary>
    public X509Extension ToExtension()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Permitted))
            {
                foreach (var name in DnsNames)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, DnsTag);
                    }
                }

                foreach (var network in Networks)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteOctetString([.. network.BaseAddress.GetAddressBytes(), .. Mask(network)], AddressTag);
                    }
                }
            }
        }

        return new X509Extension(Oid, writer.Encode(), critical: true);
    }

    /// <summary>
    /// The constraints <paramref name="certificate"/> carries, or null when it has none, or any this installer does not
    /// make (excluded names, other kinds of name), which it then does not rely on.
    /// </summary>
    public static NameConstraints? Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var extension = certificate.Extensions[Oid];
        if (extension is null)
        {
            return null;
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var constraints = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            if (!constraints.HasData || !constraints.PeekTag().HasSameClassAndValue(Permitted))
            {
                return null;
            }

            var subtrees = constraints.ReadSequence(Permitted);
            if (constraints.HasData)
            {
                return null;
            }

            var dnsNames = new List<string>();
            var networks = new List<IPNetwork>();
            while (subtrees.HasData)
            {
                var subtree = subtrees.ReadSequence();
                var tag = subtree.PeekTag();
                if (tag.HasSameClassAndValue(DnsTag))
                {
                    dnsNames.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, DnsTag));
                }
                else if (tag.HasSameClassAndValue(AddressTag) && Network(subtree.ReadOctetString(AddressTag)) is { } network)
                {
                    networks.Add(network);
                }
                else
                {
                    return null;
                }

                if (subtree.HasData)
                {
                    return null;
                }
            }

            return new NameConstraints(dnsNames, networks);
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static byte[] Mask(IPNetwork network)
    {
        var mask = new byte[network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 16];
        for (var bit = 0; bit < network.PrefixLength; bit++)
        {
            mask[bit / 8] |= (byte)(0x80 >> (bit % 8));
        }

        return mask;
    }

    // An address and its mask, as iPAddress holds a network: 8 bytes for IPv4, 32 for IPv6, the mask's ones leading.
    private static IPNetwork? Network(byte[] value)
    {
        if (value.Length is not (8 or 32))
        {
            return null;
        }

        var half = value.Length / 2;
        var mask = value.AsSpan(half);
        var prefix = 0;
        while (prefix < half * 8 && (mask[prefix / 8] & (0x80 >> (prefix % 8))) != 0)
        {
            prefix++;
        }

        for (var bit = prefix; bit < half * 8; bit++)
        {
            if ((mask[bit / 8] & (0x80 >> (bit % 8))) != 0)
            {
                return null;
            }
        }

        var address = new IPAddress(value.AsSpan(0, half));
        return IPNetwork.TryParse($"{address}/{prefix}", out var network) ? network : null;
    }
}
