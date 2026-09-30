using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Certificates made at run time for the TLS tests: CAs (with name constraints when asked), the controller's
/// certificates they issue, and self-signed ones. Every certificate carries its private key.
/// </summary>
internal static class TestCertificates
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    /// <summary>The names a controller's certificate holds by default: the loopback address and localhost.</summary>
    public static readonly IReadOnlyList<string> LoopbackNames = ["127.0.0.1", "localhost"];

    /// <summary>
    /// A CA: self-signed, or issued by <paramref name="issuer"/> as an intermediate. <paramref name="constraints"/> adds
    /// a critical name constraints extension.
    /// </summary>
    public static X509Certificate2 CreateAuthority(
        string name,
        X509Certificate2? issuer = null,
        X509Extension? constraints = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (constraints is not null)
        {
            request.CertificateExtensions.Add(constraints);
        }

        var from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-2);
        var until = notAfter ?? DateTimeOffset.UtcNow.AddYears(10);
        if (issuer is null)
        {
            using var created = request.CreateSelfSigned(from, until);
            return RoundTrip(created);
        }

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var issued = request.Create(issuer.SubjectName, Signer(issuer), from, until, NewSerial());
        using var withKey = issued.CopyWithPrivateKey(key);
        return RoundTrip(withKey);
    }

    /// <summary>
    /// A controller's certificate issued by <paramref name="issuer"/> for <paramref name="names"/> (IP addresses and DNS
    /// names). The issuer's own validity does not bound it, so a certificate valid now under an expired CA can be made.
    /// </summary>
    public static X509Certificate2 Issue(
        X509Certificate2 issuer,
        IEnumerable<string>? names = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string usage = ServerAuthentication)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateLeafRequest(key, names, usage);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var issued = request.Create(
            issuer.SubjectName,
            Signer(issuer),
            notBefore ?? DateTimeOffset.UtcNow.AddHours(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30),
            NewSerial());
        using var withKey = issued.CopyWithPrivateKey(key);
        return RoundTrip(withKey);
    }

    /// <summary>A self-signed controller certificate, as a controller without the installer's CA has.</summary>
    public static X509Certificate2 CreateSelfSigned(string subject = "CN=roof.local", IEnumerable<string>? names = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateLeafRequest(key, names, ServerAuthentication, subject);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return RoundTrip(created);
    }

    /// <summary>
    /// A critical name constraints extension (RFC 5280, 4.2.1.10) permitting <paramref name="dnsNames"/> and the
    /// <paramref name="networks"/> (an address and a prefix length).
    /// </summary>
    public static X509Extension PermitOnly(IEnumerable<string> dnsNames, IEnumerable<(IPAddress Address, int PrefixLength)> networks)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (var name in dnsNames)
            {
                using (writer.PushSequence())
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
                }
            }

            foreach (var (address, prefixLength) in networks)
            {
                var bytes = address.GetAddressBytes();
                var mask = new byte[bytes.Length];
                for (var bit = 0; bit < prefixLength; bit++)
                {
                    mask[bit / 8] |= (byte)(0x80 >> (bit % 8));
                }

                using (writer.PushSequence())
                {
                    writer.WriteOctetString([.. bytes, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }
        }

        return new X509Extension("2.5.29.30", writer.Encode(), critical: true);
    }

    private static CertificateRequest CreateLeafRequest(ECDsa key, IEnumerable<string>? names, string usage, string subject = "CN=roof controller")
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var alternatives = new SubjectAlternativeNameBuilder();
        foreach (var name in names ?? LoopbackNames)
        {
            if (IPAddress.TryParse(name, out var address))
            {
                alternatives.AddIpAddress(address);
            }
            else
            {
                alternatives.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(alternatives.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static X509SignatureGenerator Signer(X509Certificate2 issuer)
        => X509SignatureGenerator.CreateForECDsa(issuer.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The issuer has no ECDSA key."));

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return serial;
    }

    // Through PKCS#12, so the key is one the TLS stack can use on every platform.
    private static X509Certificate2 RoundTrip(X509Certificate2 certificate)
        => X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
}
