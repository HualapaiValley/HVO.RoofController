using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Installer.Certificates;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>Certificates made by the installer's own code, for a controller called roofpi.</summary>
[UnsupportedOSPlatform("windows")]
internal static class InstallerCertificates
{
    /// <summary>The PKCS#12 file's password in these tests (not a secret).</summary>
    public const string Password = "test-pfx-password-not-a-real-secret";

    /// <summary>roofpi's names and addresses.</summary>
    public static CertificateNames Names { get; } = new(
        ["roofpi"],
        ["observatory.example"],
        ["roofpi", "roofpi.local", "roofpi.observatory.example", "localhost"],
        [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback, IPAddress.IPv6Loopback],
        []);

    /// <summary>A CA and the certificate it issues, both made at <paramref name="now"/>.</summary>
    public static (X509Certificate2 Authority, X509Certificate2 Issued) Issue(DateTimeOffset now)
    {
        var authority = ControllerCertificates.CreateAuthority(Names, now);
        return (authority, ControllerCertificates.Issue(authority, Names, now));
    }

    /// <summary>The installer's PKCS#12 file for a certificate from a new CA, written to <paramref name="path"/>; returns the CA's certificate (no key).</summary>
    public static X509Certificate2 WritePfx(string path, DateTimeOffset now)
    {
        var (authority, issued) = Issue(now);
        using (authority)
        using (issued)
        {
            File.WriteAllBytes(path, ControllerCertificates.ExportPfx(issued, authority, Password));
            return X509CertificateLoader.LoadCertificate(authority.RawData);
        }
    }
}
