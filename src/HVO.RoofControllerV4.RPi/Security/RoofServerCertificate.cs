using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// The certificate the controller serves over HTTPS (<c>Kestrel:Certificates:Default</c>), read again whenever its file
/// or password changes. Its health check warns before it expires, and <c>GET /ca.crt</c> gives clients the CA that
/// issued it: the TLS handshake leaves a self-signed root out, so a client cannot learn it there.
/// </summary>
internal sealed class RoofServerCertificate
{
    /// <summary>The configuration section the deploy script sets.</summary>
    public const string Section = "Kestrel:Certificates:Default";

    private readonly IConfiguration _configuration;
    private readonly string _contentRoot;
    private readonly object _gate = new();
    private (string Path, DateTime Written, long Length, string? Password)? _readFrom;
    private ServerCertificateState? _state;

    public RoofServerCertificate(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _contentRoot = (environment ?? throw new ArgumentNullException(nameof(environment))).ContentRootPath;
    }

    /// <summary>The certificate as its file is now, or <see cref="ServerCertificateState.None"/> when no file is configured.</summary>
    public ServerCertificateState Read()
    {
        var configured = _configuration[$"{Section}:Path"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ServerCertificateState.None;
        }

        // Kestrel resolves a relative path against the content root.
        var path = Path.Combine(_contentRoot, configured);
        var password = _configuration[$"{Section}:Password"];
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return ServerCertificateState.Failed("the configured certificate file does not exist");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ServerCertificateState.Failed($"the configured certificate file cannot be read ({error.GetType().Name})");
        }

        var key = (path, file.LastWriteTimeUtc, file.Length, password);
        lock (_gate)
        {
            if (_state is null || _readFrom != key)
            {
                _state = Load(path, password);
                _readFrom = key;
            }

            return _state;
        }
    }

    private static ServerCertificateState Load(string path, string? password)
    {
        X509Certificate2Collection certificates;
        try
        {
            certificates = X509Certificate2.GetCertContentType(path) == X509ContentType.Pkcs12
                ? X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, KeyStorage)
                : LoadPem(path);
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The type only: a loader's message may name the file, and never helps a client.
            return ServerCertificateState.Failed($"the configured certificate file cannot be opened ({error.GetType().Name})");
        }

        try
        {
            var served = certificates.FirstOrDefault(certificate => certificate.HasPrivateKey) ?? certificates.FirstOrDefault();
            if (served is null)
            {
                return ServerCertificateState.Failed("the configured certificate file holds no certificate");
            }

            var authority = Authority(served, certificates);
            return new ServerCertificateState(
                true,
                null,
                served.GetNameInfo(X509NameType.SimpleName, false),
                new DateTimeOffset(served.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                Fingerprint(served.RawData),
                authority?.GetNameInfo(X509NameType.SimpleName, false),
                authority is null ? null : Fingerprint(authority.RawData),
                authority?.ExportCertificatePem());
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    // Keys stay in memory where the platform allows it (macOS does not).
    private static X509KeyStorageFlags KeyStorage => OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static X509Certificate2Collection LoadPem(string path)
    {
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPemFile(path);
        if (certificates.Count == 0)
        {
            certificates.Add(X509CertificateLoader.LoadCertificateFromFile(path));
        }

        return certificates;
    }

    /// <summary>The self-signed CA at the top of <paramref name="served"/>'s chain in the file, or null when it has none there (or signed itself).</summary>
    private static X509Certificate2? Authority(X509Certificate2 served, X509Certificate2Collection certificates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        foreach (var certificate in certificates.Where(certificate => !ReferenceEquals(certificate, served)))
        {
            if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            {
                chain.ChainPolicy.CustomTrustStore.Add(certificate);
            }
            else
            {
                chain.ChainPolicy.ExtraStore.Add(certificate);
            }
        }

        if (chain.ChainPolicy.CustomTrustStore.Count == 0 || !chain.Build(served) || chain.ChainElements.Count < 2)
        {
            return null;
        }

        var root = chain.ChainElements[^1].Certificate;
        return certificates.FirstOrDefault(certificate => certificate.RawData.AsSpan().SequenceEqual(root.RawData));
    }

    private static string Fingerprint(byte[] certificate)
        => string.Join(':', Convert.ToHexString(SHA256.HashData(certificate)).Chunk(2).Select(pair => new string(pair)));
}

/// <summary>
/// The served certificate, as <see cref="RoofServerCertificate"/> read it: its name, expiry and SHA-256 fingerprint, and
/// the CA that issued it (its certificate only, never a key).
/// </summary>
internal sealed record ServerCertificateState(
    bool Configured,
    string? Problem,
    string? Subject,
    DateTimeOffset? NotAfter,
    string? Fingerprint,
    string? AuthoritySubject,
    string? AuthorityFingerprint,
    string? AuthorityPem)
{
    /// <summary>No certificate file is configured: the controller serves plain HTTP, or a certificate from elsewhere.</summary>
    public static ServerCertificateState None { get; } = new(false, null, null, null, null, null, null, null);

    public static ServerCertificateState Failed(string problem) => new(true, problem, null, null, null, null, null, null);
}
