using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HVO.RoofControllerV4.Installer.Certificates;

/// <summary>
/// Makes the controller's certificates: the installer's own certificate authority, the certificate it issues the
/// controller, and a self-signed one. Each meets the rules browsers and Apple's platforms set for a server certificate
/// (at most 825 days, its names in the subject alternative name, the server authentication usage, SHA-256), so a Mac, an
/// iPhone and a browser accept it once the CA is trusted.
/// </summary>
public static class ControllerCertificates
{
    /// <summary>How long the CA lasts: long, since every client must be given a new one.</summary>
    public static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(3650);

    /// <summary>How long a certificate the CA issues lasts: the most browsers accept.</summary>
    public static readonly TimeSpan IssuedLifetime = TimeSpan.FromDays(397);

    /// <summary>How long a self-signed certificate lasts: the most Apple's platforms accept.</summary>
    public static readonly TimeSpan SelfSignedLifetime = TimeSpan.FromDays(825);

    /// <summary>A certificate this close to expiring is issued again.</summary>
    public static readonly TimeSpan RenewWithin = TimeSpan.FromDays(30);

    /// <summary>A CA this close to expiring is made again, so its last certificate lasts the full <see cref="IssuedLifetime"/>.</summary>
    public static readonly TimeSpan RenewAuthorityWithin = TimeSpan.FromDays(400);

    /// <summary>
    /// How a PKCS#12 file's key is loaded: kept in memory only, where the platform allows it (macOS does not, so there it
    /// goes in the default place).
    /// </summary>
    public static X509KeyStorageFlags KeyStorage => OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    /// <summary>How the name of every CA the installer makes starts: "HVO Roof CA (host, date)".</summary>
    public const string AuthorityNamePrefix = "HVO Roof CA (";

    // A clock a little behind this one still accepts a certificate made now.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>A new CA, with its key, that may issue only for <paramref name="names"/>' short names, domains and private addresses.</summary>
    public static X509Certificate2 CreateAuthority(CertificateNames names, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(names);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            CommonName($"{AuthorityNamePrefix}{names.Host}, {AuthorityAssessment.Date(now.UtcDateTime)})"),
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints.For(names).ToExtension());
        return request.CreateSelfSigned(now - ClockSkew, now + AuthorityLifetime);
    }

    /// <summary>
    /// A certificate for <paramref name="names"/>, with its key, issued by <paramref name="authority"/> (which holds its
    /// key). It lasts <see cref="IssuedLifetime"/>, or until the CA expires if sooner, and never starts before the CA.
    /// </summary>
    /// <exception cref="InstallerException">This machine's clock is before the CA's start.</exception>
    public static X509Certificate2 Issue(X509Certificate2 authority, CertificateNames names, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(names);
        var authorityStarts = new DateTimeOffset(authority.NotBefore);
        var authorityEnds = new DateTimeOffset(authority.NotAfter);
        if (now < authorityStarts || now >= authorityEnds)
        {
            throw new InstallerException(
                $"This machine's clock ({AuthorityAssessment.Date(now.UtcDateTime)}) is outside its CA's dates ({AuthorityAssessment.Date(authority.NotBefore)} to {AuthorityAssessment.Date(authority.NotAfter)}): set the time (NTP) and run it again.");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = ServerRequest(key, names);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var notBefore = now - ClockSkew > authorityStarts ? now - ClockSkew : authorityStarts;
        var notAfter = now + IssuedLifetime < authorityEnds ? now + IssuedLifetime : authorityEnds;
        using var issued = request.Create(authority, notBefore, notAfter, SerialNumber());
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>A self-signed certificate for <paramref name="names"/>, with its key: each client must trust it alone.</summary>
    public static X509Certificate2 SelfSigned(CertificateNames names, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(names);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ServerRequest(key, names).CreateSelfSigned(now - ClockSkew, now + SelfSignedLifetime);
    }

    /// <summary>
    /// The certificate and its key as a PKCS#12 file protected by <paramref name="password"/> (AES-256), with the CA's
    /// certificate after it (never its key), as Kestrel loads it.
    /// </summary>
    public static byte[] ExportPfx(X509Certificate2 certificate, X509Certificate2? authority, string password)
        => ExportPfxWithChain(certificate, authority is null ? [] : [authority], password);

    /// <summary>
    /// The certificate and its key as a PKCS#12 file protected by <paramref name="password"/> (AES-256), with the
    /// certificates of <paramref name="chain"/> after it (never their keys).
    /// </summary>
    public static byte[] ExportPfxWithChain(X509Certificate2 certificate, IEnumerable<X509Certificate2> chain, string password)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(password);
        var publicChain = chain.Select(link => X509CertificateLoader.LoadCertificate(link.RawData)).ToArray();
        try
        {
            var collection = new X509Certificate2Collection(certificate);
            collection.AddRange(publicChain);
            return collection.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
        }
        finally
        {
            foreach (var link in publicChain)
            {
                link.Dispose();
            }
        }
    }

    /// <summary>The certificate with its key from a PKCS#12 file, or null when the password does not open it or it holds none.</summary>
    public static X509Certificate2? LoadPfx(byte[] pfx, string password)
    {
        ArgumentNullException.ThrowIfNull(pfx);
        try
        {
            var loaded = X509CertificateLoader.LoadPkcs12Collection(pfx, password, KeyStorage);
            X509Certificate2? found = null;
            foreach (var certificate in loaded)
            {
                if (found is null && certificate.HasPrivateKey)
                {
                    found = certificate;
                }
                else
                {
                    certificate.Dispose();
                }
            }

            return found;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The certificates of a PKCS#12 file that have no key there (the chain after the certificate), as DER, or null when
    /// the password does not open it.
    /// </summary>
    public static IReadOnlyList<byte[]>? PfxChain(byte[] pfx, string password)
    {
        ArgumentNullException.ThrowIfNull(pfx);
        X509Certificate2Collection loaded;
        try
        {
            loaded = X509CertificateLoader.LoadPkcs12Collection(pfx, password, KeyStorage);
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            return loaded.Where(certificate => !certificate.HasPrivateKey).Select(certificate => certificate.RawData).ToArray();
        }
        finally
        {
            foreach (var certificate in loaded)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>A password for the PKCS#12 file: 32 random bytes, base64url, never shown.</summary>
    public static string NewPassword() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The DNS names and addresses <paramref name="certificate"/> is for (its subject alternative name).</summary>
    public static (IReadOnlyList<string> DnsNames, IReadOnlyList<IPAddress> Addresses) NamesIn(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        return names is null
            ? ([], [])
            : (names.EnumerateDnsNames().ToArray(), names.EnumerateIPAddresses().ToArray());
    }

    /// <summary>The names in <paramref name="names"/> <paramref name="certificate"/> lacks, and those it has that they do not: none when it names exactly them.</summary>
    public static (IReadOnlyList<string> Missing, IReadOnlyList<string> Extra) CompareNames(X509Certificate2 certificate, CertificateNames names)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(names);
        var (dnsNames, addresses) = NamesIn(certificate);
        var have = dnsNames.Select(name => name.ToLowerInvariant()).Concat(addresses.Select(address => address.ToString())).ToHashSet(StringComparer.Ordinal);
        var want = names.DnsNames.Concat(names.Addresses.Select(address => address.ToString())).ToHashSet(StringComparer.Ordinal);
        return (want.Where(name => !have.Contains(name)).ToArray(), have.Where(name => !want.Contains(name)).ToArray());
    }

    /// <summary>True when <paramref name="authority"/> signed <paramref name="certificate"/>.</summary>
    public static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 authority)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(authority);
        if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(authority.SubjectName.RawData))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        // A self-signed certificate is its own chain.
        var links = certificate.RawData.AsSpan().SequenceEqual(authority.RawData) ? 1 : 2;
        return chain.Build(certificate)
            && chain.ChainElements.Count == links
            && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(authority.RawData);
    }

    /// <summary>True when <paramref name="certificate"/> signed itself.</summary>
    public static bool IsSelfSigned(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) && IsIssuedBy(certificate, certificate);
    }

    /// <summary>The certificate's SHA-256 fingerprint, as colon-separated hex pairs (as browsers and openssl show it).</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(pair => new string(pair)).Aggregate((left, right) => $"{left}:{right}");
    }

    private static CertificateRequest ServerRequest(ECDsa key, CertificateNames names)
    {
        var request = new CertificateRequest(CommonName(names.Host), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var name in names.DnsNames)
        {
            alternativeNames.AddDnsName(name);
        }

        foreach (var address in names.Addresses)
        {
            alternativeNames.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());
        return request;
    }

    private static X500DistinguishedName CommonName(string name)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(name);
        return builder.Build();
    }

    // 16 random bytes, the top bit clear so it reads as a positive number.
    private static byte[] SerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        return serial;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
