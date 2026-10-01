using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HVO.RoofControllerV4.Client;

/// <summary>Why the controller's certificate was refused (<see cref="RoofCertificateRefusedException.Reason"/>).</summary>
public enum RoofCertificateRefusal
{
    /// <summary>The controller sent no certificate.</summary>
    NoCertificate,

    /// <summary>The certificate does not chain to the trusted CA: another CA issued it, or it is self-signed.</summary>
    OtherAuthority,

    /// <summary>The certificate, or a CA certificate above it, has expired or is not valid yet.</summary>
    Expired,

    /// <summary>The certificate names a host outside the CA's name constraints.</summary>
    OutsideNameConstraints,

    /// <summary>The certificate does not name the host being reached.</summary>
    NameMismatch,

    /// <summary>
    /// The certificate is not the pinned one (<see cref="RoofConnectionOptions.ServerCertificateSha256"/>), and the
    /// system does not trust it either.
    /// </summary>
    NotPinned,

    /// <summary>Another problem: for example a certificate that is not for a server.</summary>
    Invalid
}

/// <summary>
/// The controller's certificate was refused at the TLS handshake, and why. A request fails with an
/// <see cref="HttpRequestException"/> (the status hub with its own error) that holds this as an inner exception;
/// <see cref="Find"/> looks for it there. The message is written for the operator.
/// </summary>
public sealed class RoofCertificateRefusedException : AuthenticationException
{
    public RoofCertificateRefusedException(RoofCertificateRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public RoofCertificateRefusal Reason { get; }

    /// <summary>The refusal in <paramref name="error"/> or its inner exceptions; null when the failure was something else.</summary>
    public static RoofCertificateRefusedException? Find(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is RoofCertificateRefusedException refused)
            {
                return refused;
            }

            if (current is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Select(Find).FirstOrDefault(found => found is not null);
            }
        }

        return null;
    }
}

/// <summary>
/// The CA the controller serves could not be fetched, or is not one to trust (<see cref="RoofCertificateAuthority.FetchAsync"/>).
/// The message is written for the operator.
/// </summary>
public sealed class RoofCaFetchException : Exception
{
    public RoofCaFetchException(string message)
        : base(message)
    {
    }

    public RoofCaFetchException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Accepts the controller's certificate when a private CA issued it (#65), such as the one the installer makes. Only
/// that CA is trusted, not the system's. The host name is checked as usual, and the CA's name constraints apply. The
/// controller's certificate can be reissued under the same CA with no change to the client.
/// </summary>
public static class RoofCertificateAuthority
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private const X509ChainStatusFlags OtherAuthorityFlags = X509ChainStatusFlags.UntrustedRoot
        | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.ExplicitDistrust;

    private const X509ChainStatusFlags NameConstraintFlags = X509ChainStatusFlags.HasNotPermittedNameConstraint
        | X509ChainStatusFlags.HasExcludedNameConstraint | X509ChainStatusFlags.HasNotSupportedNameConstraint
        | X509ChainStatusFlags.HasNotDefinedNameConstraint | X509ChainStatusFlags.InvalidNameConstraints;

    /// <summary>Reads a CA certificate from a PEM or DER file.</summary>
    /// <exception cref="ArgumentException">The file holds no certificate, or one that is not a CA's.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public static X509Certificate2 Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromBytes(File.ReadAllBytes(path), path);
    }

    /// <summary>A CA certificate from PEM text, the form <see cref="ToPem"/> gives.</summary>
    /// <exception cref="ArgumentException">The text holds no certificate, or one that is not a CA's.</exception>
    public static X509Certificate2 FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        return FromBytes(Encoding.ASCII.GetBytes(pem), "The CA certificate");
    }

    /// <summary>The certificate as PEM text.</summary>
    public static string ToPem(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.ExportCertificatePem();
    }

    /// <summary>True when the certificate's basic constraints say it is a CA's.</summary>
    public static bool IsCertificateAuthority(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true };
    }

    /// <summary>
    /// The certificate's SHA-256 fingerprint as colon-separated hex pairs, as the installer's Done page and
    /// <c>hvo-roof-install cert show</c> give it (and as browsers and openssl show it).
    /// </summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(pair => new string(pair)));
    }

    /// <summary>
    /// True when <paramref name="text"/> is <paramref name="certificate"/>'s SHA-256: 64 hex digits, in either case, with
    /// or without colons, spaces or dashes between them.
    /// </summary>
    public static bool HasFingerprint(X509Certificate2 certificate, string text)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(text);
        var hex = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray());
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit)
            && string.Equals(hex, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The CA the controller serves at <c>GET /ca.crt</c> (#71), for a client to save once the person has compared its
    /// <see cref="Fingerprint"/> with the one the installer showed. The request is anonymous, and nothing about the
    /// connection is trusted yet, so what is served is checked: it must be a CA's certificate, and the certificate the
    /// controller presents must chain to it and name the host reached. A TLS handshake alone cannot give the CA, since a
    /// server leaves its root out of the chain it sends.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="controller"/> is not an https address.</exception>
    /// <exception cref="RoofCaFetchException">The controller serves no CA, or one that did not issue its certificate.</exception>
    /// <exception cref="RoofCertificateRefusedException">
    /// The CA issued the controller's certificate, which is refused anyway: it has expired, or does not name the host.
    /// </exception>
    /// <exception cref="HttpRequestException">The controller cannot be reached, or its answer is cut off part way.</exception>
    /// <exception cref="TimeoutException">The CA did not arrive, its headers and body both, within <paramref name="timeout"/>.</exception>
    public static async Task<X509Certificate2> FetchAsync(Uri controller, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!controller.IsAbsoluteUri || controller.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"The CA is fetched over HTTPS, and {controller} is not an https address.", nameof(controller));
        }

        byte[]? presented = null;
        List<byte[]> sent = [];
        var errors = SslPolicyErrors.None;
        string? host = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = timeout,
            SslOptions =
            {
                // Nothing is trusted yet: what the controller presents is kept, and checked once the CA is in hand.
                RemoteCertificateValidationCallback = (sender, certificate, chain, policyErrors) =>
                {
                    presented = certificate?.GetRawCertData();
                    sent = chain is null ? [] : [.. chain.ChainPolicy.ExtraStore.Select(extra => extra.RawData)];
                    errors = policyErrors;
                    host = (sender as SslStream)?.TargetHostName;
                    return true;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var address = new Uri(controller.AbsoluteUri.EndsWith('/') ? controller : new Uri(controller.AbsoluteUri + "/"), RoofApiRoutes.CaCertificate);

        // One limit for the answer and its body: HttpClient's own ends once the headers are in.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        byte[] body;
        try
        {
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new RoofCaFetchException(
                    $"The controller serves no CA at {address}: a private CA did not issue its certificate, which is self-signed or one of your own.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RoofCaFetchException($"The controller answered {(int)response.StatusCode} {response.ReasonPhrase} for {address}.");
            }

            body = await ReadLimitedAsync(response, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{address} did not send the controller's CA within {timeout.TotalSeconds:0} s.", ex);
        }
        catch (IOException ex)
        {
            // The connection closed part way through the body: as unreachable as a connection refused.
            throw new HttpRequestException($"The controller's answer at {address} was cut off: {ex.Message}", ex);
        }

        X509Certificate2 authority;
        try
        {
            authority = FromBytes(body, $"What the controller serves at {address}");
        }
        catch (ArgumentException ex)
        {
            throw new RoofCaFetchException(ex.Message, ex);
        }

        var extras = sent.Select(X509CertificateLoader.LoadCertificate).ToArray();
        try
        {
            using var leaf = presented is null ? null : X509CertificateLoader.LoadCertificate(presented);
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(extras);
            if (Check(authority, leaf, chain, errors, host) is { } refused)
            {
                if (refused.Reason is RoofCertificateRefusal.OtherAuthority or RoofCertificateRefusal.OutsideNameConstraints)
                {
                    throw new RoofCaFetchException(
                        $"The CA the controller serves ({Describe(authority)}) did not issue the certificate it presents, so it is not saved.", refused);
                }

                throw refused;
            }

            return authority;
        }
        catch
        {
            authority.Dispose();
            throw;
        }
        finally
        {
            foreach (var extra in extras)
            {
                extra.Dispose();
            }
        }
    }

    // A CA certificate is a few kilobytes: more than this is not one.
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int Limit = 64 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > Limit)
            {
                throw new RoofCaFetchException($"What the controller serves as its CA is larger than {Limit / 1024} KiB, so it is not a CA certificate.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The CA's name as the refusals give it: its common name, or its whole subject.</summary>
    public static string Describe(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
    }

    private static X509Certificate2 FromBytes(byte[] data, string source)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(data);
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"{source} does not hold a certificate in PEM or DER form.", ex);
        }

        if (!IsCertificateAuthority(certificate))
        {
            certificate.Dispose();
            throw new ArgumentException($"{source} is not a CA certificate: its basic constraints do not say CA.");
        }

        return certificate;
    }

    /// <summary>
    /// A TLS callback that accepts a certificate issued by <paramref name="authority"/> for the host being reached, and
    /// throws <see cref="RoofCertificateRefusedException"/> for any other.
    /// </summary>
    internal static RemoteCertificateValidationCallback CreateValidator(X509Certificate2 authority)
    {
        // A copy, so the caller may dispose theirs.
        var trusted = X509CertificateLoader.LoadCertificate(authority.RawData);
        return (sender, certificate, chain, errors) =>
            Check(trusted, certificate, chain, errors, (sender as SslStream)?.TargetHostName) is { } refusal ? throw refusal : true;
    }

    /// <summary>
    /// Why <paramref name="certificate"/> is refused, or null when <paramref name="authority"/> issued it for
    /// <paramref name="host"/>. The chain is built again, trusting only the CA: <paramref name="presented"/> is the
    /// platform's chain, which supplies the intermediate certificates the controller sent. The host name is checked as
    /// the platform checked it (<paramref name="errors"/>).
    /// </summary>
    internal static RoofCertificateRefusedException? Check(
        X509Certificate2 authority,
        X509Certificate? certificate,
        X509Chain? presented,
        SslPolicyErrors errors,
        string? host)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return new RoofCertificateRefusedException(RoofCertificateRefusal.NoCertificate, "The controller sent no certificate.");
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        if (presented is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
        }

        try
        {
            var built = chain.Build(leaf);
            var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, status) => all | status.Status);
            var rooted = chain.ChainElements.Count > 0 && chain.ChainElements[^1].Certificate.RawDataMemory.Span.SequenceEqual(authority.RawDataMemory.Span);
            var name = Describe(authority);
            if ((flags & OtherAuthorityFlags) != 0 || (built && !rooted))
            {
                return new RoofCertificateRefusedException(
                    RoofCertificateRefusal.OtherAuthority,
                    $"The controller's certificate was not issued by the CA this client trusts ({name}).");
            }

            if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid))
            {
                return DescribeTime(chain, leaf);
            }

            if ((flags & NameConstraintFlags) != 0)
            {
                return new RoofCertificateRefusedException(
                    RoofCertificateRefusal.OutsideNameConstraints,
                    $"The controller's certificate names a host that the CA ({name}) may not issue for: it is outside the CA's name constraints.");
            }

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return new RoofCertificateRefusedException(
                    RoofCertificateRefusal.NameMismatch,
                    $"The controller's certificate is not for {(string.IsNullOrEmpty(host) ? "the address used" : host)}; it names {DescribeNames(leaf)}.");
            }

            if (!built || flags != X509ChainStatusFlags.NoError)
            {
                return new RoofCertificateRefusedException(
                    RoofCertificateRefusal.Invalid,
                    flags.HasFlag(X509ChainStatusFlags.NotValidForUsage)
                        ? "The controller's certificate is not for a server: its extended key usage leaves out server authentication."
                        : $"The controller's certificate was refused: {string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation.Trim()).Where(text => text.Length > 0).Distinct())}.");
            }

            return null;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }

    private static RoofCertificateRefusedException DescribeTime(X509Chain chain, X509Certificate2 leaf)
    {
        var index = 0;
        for (var i = 0; i < chain.ChainElements.Count; i++)
        {
            if (chain.ChainElements[i].ChainElementStatus.Any(status => status.Status.HasFlag(X509ChainStatusFlags.NotTimeValid)))
            {
                index = i;
                break;
            }
        }

        var certificate = index == 0 ? leaf : chain.ChainElements[index].Certificate;
        var whose = index == 0 ? "The controller's certificate" : $"The CA certificate ({Describe(certificate)})";
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var message = DateTime.UtcNow > notAfter
            ? $"{whose} expired on {notAfter:yyyy-MM-dd HH:mm} UTC."
            : $"{whose} is not valid until {certificate.NotBefore.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Check this computer's clock.";
        return new RoofCertificateRefusedException(RoofCertificateRefusal.Expired, message);
    }

    private static string DescribeNames(X509Certificate2 certificate)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(names => names.EnumerateDnsNames().Concat(names.EnumerateIPAddresses().Select(address => address.ToString())))
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : $"only {certificate.Subject}";
    }
}
