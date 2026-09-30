using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;

namespace HVO.RoofControllerV4.Installer.Certificates;

/// <summary>What the installer does with the CA it finds.</summary>
public enum AuthorityVerdict
{
    /// <summary>There is none: one is made.</summary>
    Missing,

    /// <summary>It cannot be kept (its key is gone, it expires soon, it may not issue for a name, or a person asked): a new one is made.</summary>
    Remake,

    /// <summary>It is kept.</summary>
    Keep,

    /// <summary>Its key cannot be read by this user (<c>--plan</c> without root): nothing can be said.</summary>
    Unreadable
}

/// <summary>
/// The installer's certificate authority on this machine, and whether it is kept. The CA's step and the certificate's both
/// use it, so the plan shows a certificate issued again whenever the CA is made again.
/// </summary>
public sealed class AuthorityAssessment : IDisposable
{
    private AuthorityAssessment(AuthorityVerdict verdict, string detail, X509Certificate2? authority)
    {
        Verdict = verdict;
        Detail = detail;
        Authority = authority;
    }

    public AuthorityVerdict Verdict { get; }

    /// <summary>What the plan says about it.</summary>
    public string Detail { get; }

    /// <summary>The CA, with its key, when it is kept.</summary>
    public X509Certificate2? Authority { get; }

    /// <summary>
    /// Looks at the CA in <paramref name="layout"/>. <paramref name="replacing"/> is the SHA-256 fingerprint of a CA a
    /// person asked to replace (<c>--new-ca</c>): that one is made again, and the new one kept.
    /// </summary>
    public static AuthorityAssessment Assess(InstallerMachine machine, ControllerLayout layout, CertificateNames names, string? replacing, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);
        string? certificatePem;
        string? keyPem;
        try
        {
            certificatePem = machine.ReadText(layout.CaCertificate);
            keyPem = machine.ReadText(layout.CaKey);
        }
        catch (UnauthorizedAccessException)
        {
            return new(AuthorityVerdict.Unreadable, "only root can read its key: run with sudo to check it", null);
        }

        var constraints = NameConstraints.For(names);
        if (certificatePem is null && keyPem is null)
        {
            return new(AuthorityVerdict.Missing, $"a new CA, which may issue only for {Describe(constraints)}", null);
        }

        if (certificatePem is null || keyPem is null)
        {
            return Remake($"its {(certificatePem is null ? "certificate" : "key")} is missing");
        }

        X509Certificate2 authority;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            using var key = ECDsa.Create();
            key.ImportFromPem(keyPem);
            authority = certificate.CopyWithPrivateKey(key);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            return Remake("its certificate or key cannot be read, or they do not match");
        }

        var permitted = NameConstraints.Of(authority);
        var notPermitted = permitted?.NotPermitted(names) ?? [];
        var reason = replacing is not null && string.Equals(ControllerCertificates.Fingerprint(authority), replacing, StringComparison.OrdinalIgnoreCase)
                ? "a person asked for a new one (--new-ca)"
            : new DateTimeOffset(authority.NotAfter) - now < ControllerCertificates.RenewAuthorityWithin
                ? $"it expires on {Date(authority.NotAfter)}"
            : permitted is null
                ? "it does not limit the names it may issue for"
            : notPermitted.Count > 0
                ? $"it may not issue for {string.Join(", ", notPermitted)}"
            : null;
        if (reason is not null)
        {
            authority.Dispose();
            return Remake(reason);
        }

        return new(AuthorityVerdict.Keep, $"kept: {authority.GetNameInfo(X509NameType.SimpleName, false)}, until {Date(authority.NotAfter)}", authority);
    }

    /// <summary>The date as the installer writes it: 2027-10-31.</summary>
    public static string Date(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose() => Authority?.Dispose();

    private static AuthorityAssessment Remake(string reason)
        => new(AuthorityVerdict.Remake, $"{reason}: a new CA, which every client must trust in place of this one", null);

    private static string Describe(NameConstraints constraints)
        => $"names in {string.Join(", ", constraints.DnsNames)}, and private addresses";
}
