using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HVO.RoofControllerV4.Installer.Certificates;

/// <summary>
/// A certificate a person gives for the controller (<c>hvo-roof-install cert import</c>): its key, and the certificates
/// that chain it to its CA, read from a PKCS#12 file or from PEM (the certificates, and the key in the same file or its
/// own). A password is asked for only when the file needs one, and is never kept.
/// </summary>
public sealed class ImportedCertificate : IDisposable
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    // Apple's platforms refuse a server certificate valid for longer.
    private static readonly TimeSpan AppleLongest = TimeSpan.FromDays(825);

    private static readonly string[] KeyLabels = ["PRIVATE KEY", "ENCRYPTED PRIVATE KEY", "RSA PRIVATE KEY", "EC PRIVATE KEY"];

    private ImportedCertificate(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain)
    {
        Certificate = certificate;
        Chain = chain;
    }

    /// <summary>The certificate, with its key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The other certificates given with it (its intermediates and CA), without keys.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>Its subject's common name.</summary>
    public string Subject => Certificate.GetNameInfo(X509NameType.SimpleName, false);

    /// <summary>Who issued it: its issuer's common name, or "itself".</summary>
    public string Issuer => ControllerCertificates.IsSelfSigned(Certificate) ? "itself" : Certificate.GetNameInfo(X509NameType.SimpleName, true);

    /// <summary>
    /// Reads the certificate in <paramref name="content"/> (the file <paramref name="name"/>), with its key from
    /// <paramref name="keyContent"/> (<paramref name="keyName"/>) or from the same file. <paramref name="password"/> is
    /// asked, with what needs it, only when the file or key is protected; it returns null when none is given.
    /// </summary>
    public static ImportedCertificate Read(string name, byte[] content, string? keyName, byte[]? keyContent, Func<string, string?> password)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(password);
        if (IsPem(content))
        {
            return ReadPem(name, Encoding.UTF8.GetString(content), keyName ?? name, keyContent is null ? null : Encoding.UTF8.GetString(keyContent), password);
        }

        if (keyContent is not null)
        {
            throw new InstallerUsageException($"--key is for a certificate in PEM, and {name} is not PEM: give the key in the PKCS#12 file, or give both as PEM.");
        }

        X509ContentType type;
        try
        {
            type = X509Certificate2.GetCertContentType(content);
        }
        catch (CryptographicException)
        {
            type = X509ContentType.Unknown;
        }

        return type switch
        {
            X509ContentType.Pkcs12 => ReadPkcs12(name, content, password),
            X509ContentType.Cert => throw new InstallerUsageException($"{name} holds a certificate but not its key: give the key with --key FILE (PEM), or give a PKCS#12 file with both."),
            _ => throw new InstallerUsageException($"{name} is not a certificate the installer can read: give a PKCS#12 file (.pfx, .p12), or PEM (.crt, .pem) with the key.")
        };
    }

    /// <summary>
    /// Why the controller cannot serve it at <paramref name="now"/>, a sentence each; empty when it can. The installer
    /// refuses it for any of these.
    /// </summary>
    public IReadOnlyList<string> Problems(DateTimeOffset now)
    {
        var problems = new List<string>();
        if (now.UtcDateTime < Certificate.NotBefore.ToUniversalTime())
        {
            problems.Add($"It is not valid until {AuthorityAssessment.Date(Certificate.NotBefore)}: check this machine's clock, or import it then.");
        }

        if (now.UtcDateTime >= Certificate.NotAfter.ToUniversalTime())
        {
            problems.Add($"It expired on {AuthorityAssessment.Date(Certificate.NotAfter)}.");
        }

        if (Certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true })
        {
            problems.Add("It is a certificate authority's certificate, not a server's: give the certificate issued for the controller.");
        }

        if (Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } usages
            && !usages.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == ServerAuthentication))
        {
            problems.Add("It is not for a server: its extended key usage leaves out server authentication, so clients refuse it.");
        }

        return problems;
    }

    /// <summary>What may trouble clients, a sentence each: names clients use that it is not for, and a lifetime Apple's platforms refuse.</summary>
    public IReadOnlyList<string> Warnings(CertificateNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var warnings = new List<string>();
        var (missing, _) = ControllerCertificates.CompareNames(Certificate, names);
        if (missing.Count > 0)
        {
            warnings.Add($"It is not for {string.Join(", ", missing)}: a client that uses {(missing.Count == 1 ? "it" : "one")} refuses it.");
        }

        if (Certificate.NotAfter - Certificate.NotBefore > AppleLongest)
        {
            warnings.Add("It is valid for more than 825 days, so macOS and iOS refuse it.");
        }

        return warnings;
    }

    public void Dispose()
    {
        Certificate.Dispose();
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }
    }

    private static bool IsPem(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(content).Contains("-----BEGIN ", StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ImportedCertificate ReadPkcs12(string name, byte[] content, Func<string, string?> password)
    {
        X509Certificate2Collection loaded;
        try
        {
            // Many files have no password: only ask when this one does.
            loaded = X509CertificateLoader.LoadPkcs12Collection(content, null, ControllerCertificates.KeyStorage);
        }
        catch (CryptographicException)
        {
            var given = password($"{name}'s password") ?? throw NoPassword(name);
            try
            {
                loaded = X509CertificateLoader.LoadPkcs12Collection(content, given, ControllerCertificates.KeyStorage);
            }
            catch (CryptographicException)
            {
                throw new InstallerUsageException($"The password does not open {name}.");
            }
        }

        var withKey = loaded.Where(certificate => certificate.HasPrivateKey).ToArray();
        var certificate = withKey.FirstOrDefault(certificate => !IsAuthority(certificate)) ?? withKey.FirstOrDefault();
        if (certificate is null)
        {
            Dispose(loaded);
            throw new InstallerUsageException($"{name} holds no private key: give the PKCS#12 file with the certificate's key in it.");
        }

        return new ImportedCertificate(certificate, Others(loaded, certificate));
    }

    private static ImportedCertificate ReadPem(string name, string text, string keyName, string? keyText, Func<string, string?> password)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(text);
        }
        catch (CryptographicException)
        {
            throw new InstallerUsageException($"{name} holds a certificate the installer cannot read.");
        }

        if (certificates.Count == 0)
        {
            throw new InstallerUsageException($"{name} holds no certificate.");
        }

        using var key = ReadKey(keyName, keyText ?? text, keyText is null, password);
        var publicKey = key switch
        {
            RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
            ECDsa ecdsa => ecdsa.ExportSubjectPublicKeyInfo(),
            _ => []
        };
        var match = certificates.FirstOrDefault(certificate => certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey));
        if (match is null)
        {
            Dispose(certificates);
            throw new InstallerUsageException($"The key in {keyName} is not the key of any certificate in {name}.");
        }

        var withKey = key switch
        {
            RSA rsa => match.CopyWithPrivateKey(rsa),
            ECDsa ecdsa => match.CopyWithPrivateKey(ecdsa),
            _ => throw new InvalidOperationException()
        };
        var chain = Others(certificates, match);
        match.Dispose();
        return new ImportedCertificate(withKey, chain);
    }

    // The one private key in the PEM: RSA or ECDSA, PKCS#8 or the older forms, encrypted or not.
    private static AsymmetricAlgorithm ReadKey(string keyName, string text, bool sameFile, Func<string, string?> password)
    {
        var blocks = new List<(string Label, string Pem)>();
        var rest = text.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            var label = rest[fields.Label].ToString();
            if (KeyLabels.Contains(label, StringComparer.Ordinal))
            {
                blocks.Add((label, rest[fields.Location].ToString()));
            }

            rest = rest[fields.Location.End..];
        }

        if (blocks.Count == 0)
        {
            throw new InstallerUsageException(sameFile
                ? $"{keyName} holds no private key: give the key's file with --key FILE."
                : $"{keyName} holds no private key.");
        }

        if (blocks.Count > 1)
        {
            throw new InstallerUsageException($"{keyName} holds more than one private key: give the certificate's own key.");
        }

        var (keyLabel, pem) = blocks[0];
        var encrypted = keyLabel == "ENCRYPTED PRIVATE KEY";
        var given = encrypted ? password($"{keyName}'s password") ?? throw NoPassword(keyName) : null;
        foreach (var create in new Func<AsymmetricAlgorithm>[] { RSA.Create, ECDsa.Create })
        {
            var key = create();
            try
            {
                switch (key)
                {
                    case RSA rsa when given is not null:
                        rsa.ImportFromEncryptedPem(pem, given);
                        break;
                    case RSA rsa:
                        rsa.ImportFromPem(pem);
                        break;
                    case ECDsa ecdsa when given is not null:
                        ecdsa.ImportFromEncryptedPem(pem, given);
                        break;
                    case ECDsa ecdsa:
                        ecdsa.ImportFromPem(pem);
                        break;
                }

                return key;
            }
            catch (Exception error) when (error is CryptographicException or ArgumentException)
            {
                key.Dispose();
            }
        }

        throw new InstallerUsageException(encrypted
            ? $"The password does not open the key in {keyName}, or it is not an RSA or ECDSA key."
            : $"The key in {keyName} is not an RSA or ECDSA key the installer can read.");
    }

    private static InstallerUsageException NoPassword(string name)
        => new($"{name} needs a password: give it with --password-file FILE, or run the installer in a terminal to type it.");

    private static bool IsAuthority(X509Certificate2 certificate)
        => certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true };

    // The collection's other certificates without keys (a chain never carries one), the collection disposed.
    private static X509Certificate2[] Others(X509Certificate2Collection collection, X509Certificate2? kept)
    {
        var others = collection
            .Where(certificate => !ReferenceEquals(certificate, kept) && !(kept is not null && certificate.RawData.AsSpan().SequenceEqual(kept.RawData)))
            .Select(certificate => X509CertificateLoader.LoadCertificate(certificate.RawData))
            .ToArray();
        foreach (var certificate in collection.Where(certificate => !ReferenceEquals(certificate, kept)))
        {
            certificate.Dispose();
        }

        return others;
    }

    private static void Dispose(X509Certificate2Collection collection)
    {
        foreach (var certificate in collection)
        {
            certificate.Dispose();
        }
    }
}
