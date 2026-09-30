using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The certificates the installer makes (#68): its CA's name constraints, the names and addresses a certificate is for,
/// the rules Apple's platforms set, a chain the client and openssl accept, and a person's own certificate read from
/// PKCS#12 or PEM, with what it refuses and what it warns about.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCertificateTests
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private static readonly DateTimeOffset Today = FakeMachine.Today;

    [TestMethod]
    public void TheCa_MayIssueOnlyForTheControllersNames_AndPrivateAddresses()
    {
        using var authority = ControllerCertificates.CreateAuthority(InstallerCertificates.Names, Today);

        var extension = authority.Extensions[NameConstraints.Oid];
        extension.Should().NotBeNull();
        extension!.Critical.Should().BeTrue("a client that cannot read the constraints must refuse the CA");
        var constraints = NameConstraints.Of(authority)!;
        constraints.DnsNames.Should().Equal("local", "localhost", "roofpi", "observatory.example");
        constraints.Networks.Should().Equal(NameConstraints.PrivateNetworks);
        constraints.Permits("roofpi").Should().BeTrue();
        constraints.Permits("roofpi.local").Should().BeTrue();
        constraints.Permits("ROOFPI.Observatory.Example.").Should().BeTrue();
        constraints.Permits("evil.example").Should().BeFalse();
        constraints.Permits("notobservatory.example").Should().BeFalse("a name is permitted only under a permitted one, label by label");
        constraints.Permits(IPAddress.Parse("192.168.1.20")).Should().BeTrue();
        constraints.Permits(IPAddress.Parse("10.1.2.3")).Should().BeTrue();
        constraints.Permits(IPAddress.Parse("fd12::1")).Should().BeTrue();
        constraints.Permits(IPAddress.IPv6Loopback).Should().BeTrue();
        constraints.Permits(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        constraints.Permits(IPAddress.Parse("2001:db8::1")).Should().BeFalse();
        constraints.NotPermitted(InstallerCertificates.Names).Should().BeEmpty();
        constraints.NotPermitted(InstallerCertificates.Names with { DnsNames = ["roofpi", "roof.evil.example"], Addresses = [IPAddress.Parse("203.0.113.9")] })
            .Should().Equal("roof.evil.example", "203.0.113.9");
    }

    [TestMethod]
    public void ACaWithoutNameConstraints_HasNone()
    {
        using var unconstrained = TestCertificates.CreateAuthority("Unconstrained");

        NameConstraints.Of(unconstrained).Should().BeNull();
    }

    [TestMethod]
    public void TheCa_AndTheCertificateItIssues_FollowApplesRules()
    {
        var (authority, issued) = InstallerCertificates.Issue(Today);
        using (authority)
        using (issued)
        {
            authority.Extensions.OfType<X509BasicConstraintsExtension>().Single()
                .Should().Match<X509BasicConstraintsExtension>(constraints => constraints.CertificateAuthority && constraints.HasPathLengthConstraint && constraints.PathLengthConstraint == 0 && constraints.Critical);
            authority.GetNameInfo(X509NameType.SimpleName, false).Should().Be("HVO Roof CA (roofpi, 2026-10-01)");
            (authority.NotAfter.ToUniversalTime() - Today.UtcDateTime).Should().Be(ControllerCertificates.AuthorityLifetime);
            authority.GetECDsaPublicKey()!.KeySize.Should().Be(256);

            issued.GetNameInfo(X509NameType.SimpleName, false).Should().Be("roofpi");
            (issued.NotAfter.ToUniversalTime() - Today.UtcDateTime).Should().Be(TimeSpan.FromDays(397), "Apple's platforms refuse longer than 398 days");
            issued.NotBefore.ToUniversalTime().Should().Be(Today.UtcDateTime.AddHours(-1), "a clock a little behind still accepts it");
            issued.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority.Should().BeFalse();
            issued.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages.Should().Be(X509KeyUsageFlags.DigitalSignature);
            issued.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(usage => usage.Value).Should().Equal(ServerAuthentication);
            issued.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Should().ContainSingle();
            issued.SerialNumberBytes.Length.Should().BeGreaterThanOrEqualTo(16);
            var (dnsNames, addresses) = ControllerCertificates.NamesIn(issued);
            dnsNames.Should().Equal("roofpi", "roofpi.local", "roofpi.observatory.example", "localhost");
            addresses.Select(address => address.ToString()).Should().Equal("192.168.1.20", "127.0.0.1", "::1");
            ControllerCertificates.IsIssuedBy(issued, authority).Should().BeTrue();
            ControllerCertificates.IsSelfSigned(issued).Should().BeFalse();
            ControllerCertificates.CompareNames(issued, InstallerCertificates.Names).Should().Be((Array.Empty<string>(), Array.Empty<string>()));
        }
    }

    [TestMethod]
    public void ASelfSignedCertificate_LastsNoLongerThanApplesLimit()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);

        (selfSigned.NotAfter.ToUniversalTime() - Today.UtcDateTime).Should().Be(TimeSpan.FromDays(825));
        ControllerCertificates.IsSelfSigned(selfSigned).Should().BeTrue();
        selfSigned.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(usage => usage.Value).Should().Equal(ServerAuthentication);
    }

    [TestMethod]
    public void ACertificate_NeverOutlivesItsCa()
    {
        using var authority = ControllerCertificates.CreateAuthority(InstallerCertificates.Names, Today);

        using var issued = ControllerCertificates.Issue(authority, InstallerCertificates.Names, Today.AddDays(3500));

        issued.NotAfter.Should().Be(authority.NotAfter);
    }

    [TestMethod]
    public void ThePfx_HoldsTheCertificatesKey_AndTheCasCertificate_NeverItsKey()
    {
        var (authority, issued) = InstallerCertificates.Issue(Today);
        using (authority)
        using (issued)
        {
            var pfx = ControllerCertificates.ExportPfx(issued, authority, InstallerCertificates.Password);

            var loaded = X509CertificateLoader.LoadPkcs12Collection(pfx, InstallerCertificates.Password, ControllerCertificates.KeyStorage);
            loaded.Should().HaveCount(2);
            loaded.Single(certificate => certificate.HasPrivateKey).RawData.Should().Equal(issued.RawData);
            loaded.Single(certificate => !certificate.HasPrivateKey).RawData.Should().Equal(authority.RawData);
            using var opened = ControllerCertificates.LoadPfx(pfx, InstallerCertificates.Password);
            opened!.RawData.Should().Equal(issued.RawData);
            ControllerCertificates.LoadPfx(pfx, "not-the-password").Should().BeNull();
            foreach (var certificate in loaded)
            {
                certificate.Dispose();
            }
        }
    }

    [TestMethod]
    public void ThePassword_Is32RandomBytes_InBase64Url()
    {
        var passwords = Enumerable.Range(0, 3).Select(_ => ControllerCertificates.NewPassword()).ToArray();

        passwords.Should().OnlyHaveUniqueItems();
        passwords.Should().AllSatisfy(password => password.Should().MatchRegex("^[A-Za-z0-9_-]{43}$"));
    }

    [TestMethod]
    public void TheFingerprint_IsSha256_AsOpensslShowsIt()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);

        ControllerCertificates.Fingerprint(selfSigned).Should()
            .Be(string.Join(':', Convert.ToHexString(SHA256.HashData(selfSigned.RawData)).Chunk(2).Select(pair => new string(pair))));
    }

    [TestMethod]
    public void TheNames_AreTheHosts_UnderLocalAndEachDomain_AndItsPrivateAddresses()
    {
        using var pi = new FakeMachine();
        pi.Addresses.AddRange(
        [
            new NetworkAddress("wlan0", IPAddress.Parse("203.0.113.9")),
            new NetworkAddress("eth0", IPAddress.Parse("fd00::50")),
            new NetworkAddress("br-5e1f", IPAddress.Parse("172.18.0.1")),
            new NetworkAddress("veth1", IPAddress.Parse("10.200.0.1")),
            new NetworkAddress("eth0", IPAddress.Parse("169.254.10.1")),
            new NetworkAddress("lo", IPAddress.Loopback)
        ]);

        var names = CertificateNames.For(pi.Machine, new ControllerSettings { HostNames = ["Roof", "roofpi"], Domains = ["observatory.example."] }.Normalised());

        names.ShortNames.Should().Equal("roofpi", "roof");
        names.Host.Should().Be("roofpi");
        names.Domains.Should().Equal("observatory.example");
        names.DnsNames.Should().Equal("roofpi", "roofpi.local", "roofpi.observatory.example", "roof", "roof.local", "roof.observatory.example", "localhost");
        names.Addresses.Select(address => address.ToString()).Should().Equal("192.168.1.50", "fd00::50", "127.0.0.1", "::1");
        names.LeftOut.Should().Equal(new NetworkAddress("wlan0", IPAddress.Parse("203.0.113.9")));
        names.AllowedHosts.Should().Be("roofpi;roofpi.local;roofpi.observatory.example;roof;roof.local;roof.observatory.example;localhost;192.168.1.50;[fd00::50];127.0.0.1;[::1]");
    }

    [TestMethod]
    public void AHostNameThatIsNotALabel_LeavesLocalhost()
    {
        using var odd = new FakeMachine(hostName: "_odd_");
        odd.Addresses.Clear();

        var names = CertificateNames.For(odd.Machine, new ControllerSettings());

        names.ShortNames.Should().Equal("localhost");
        names.DnsNames.Should().Equal("localhost", "localhost.local");
        names.Addresses.Should().Equal(IPAddress.Loopback, IPAddress.IPv6Loopback);
    }

    [TestMethod]
    public void TheSuggestedDomains_AreTheResolversAndTheHostNames_NotLocalOnes()
    {
        using var pi = new FakeMachine(hostName: "roofpi.site.example")
            .Write(CertificateNames.ResolverConfiguration, "# made by NetworkManager\nsearch observatory.example local lan.local\ndomain site.example.\nnameserver 192.168.1.1\n");

        CertificateNames.SuggestedDomains(pi.Machine).Should().Equal("site.example", "observatory.example");
        CertificateNames.ShortName("RoofPi.site.example").Should().Be("roofpi");
    }

    [TestMethod]
    public void NoResolverConfiguration_SuggestsNoDomain()
    {
        using var pi = new FakeMachine();

        CertificateNames.SuggestedDomains(pi.Machine).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "by address")]
    [DataRow(true, DisplayName = "by name")]
    public async Task TheClient_TrustsTheCa_AndAReissuedCertificate_WithNoChange(bool byName)
    {
        var now = DateTimeOffset.UtcNow;
        var (authority, first) = InstallerCertificates.Issue(now);
        using (authority)
        using (first)
        using (var reissued = ControllerCertificates.Issue(authority, InstallerCertificates.Names, now))
        using (var trusted = X509CertificateLoader.LoadCertificate(authority.RawData))
        {
            await using var host = await TlsTestController.StartAsync(first);
            using var client = new RoofControllerClient(new RoofConnectionOptions
            {
                BaseAddress = byName ? host.LocalhostAddress : host.BaseAddress,
                Credential = new RoofApiKeyCredential(TestApiKeys.Viewer),
                ServerCaCertificate = trusted,
                StatusFeed = FastFeed
            });

            (await client.Auth.GetCallerAsync()).Name.Should().Be(TlsTestController.CallerName);
            host.Present(reissued);
            (await client.StopAsync()).Outcome.Should().Be(RoofStopOutcome.Acknowledged, "Stop opens its own connection, which meets the reissued certificate");
            host.Presented.Should().Contain(first.Thumbprint).And.Contain(reissued.Thumbprint);
        }
    }

    [TestMethod]
    public async Task Openssl_VerifiesTheChain_ForAServer_ByNameAndAddress()
    {
        if (!File.Exists("/usr/bin/openssl"))
        {
            Assert.Inconclusive("openssl is not installed here.");
        }

        var folder = Directory.CreateTempSubdirectory("hvo-roof-openssl-");
        try
        {
            var (authority, issued) = InstallerCertificates.Issue(DateTimeOffset.UtcNow);
            using (authority)
            using (issued)
            {
                var ca = Path.Join(folder.FullName, "ca.crt");
                var certificate = Path.Join(folder.FullName, "roofpi.crt");
                await File.WriteAllTextAsync(ca, authority.ExportCertificatePem());
                await File.WriteAllTextAsync(certificate, issued.ExportCertificatePem());

                foreach (var check in new[] { new[] { "-verify_hostname", "roofpi.local" }, ["-verify_hostname", "roofpi.observatory.example"], ["-verify_ip", "192.168.1.20"] })
                {
                    var (exit, output) = await OpensslAsync(["verify", "-x509_strict", "-purpose", "sslserver", .. check, "-CAfile", ca, certificate]);
                    exit.Should().Be(0, output);
                    output.Should().Contain($"{certificate}: OK");
                }

                var (refused, why) = await OpensslAsync(["verify", "-purpose", "sslserver", "-verify_hostname", "roof.evil.example", "-CAfile", ca, certificate]);
                refused.Should().NotBe(0, "the certificate is not for that name");
                why.Should().Contain("hostname mismatch");
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void APfx_WithAPassword_IsReadWithItsChain()
    {
        var (authority, issued) = InstallerCertificates.Issue(Today);
        using (authority)
        using (issued)
        {
            var asked = new List<string>();

            using var imported = ImportedCertificate.Read("/root/roof.pfx", ControllerCertificates.ExportPfx(issued, authority, InstallerCertificates.Password), null, null, what =>
            {
                asked.Add(what);
                return InstallerCertificates.Password;
            });

            asked.Should().Equal("/root/roof.pfx's password");
            imported.Certificate.RawData.Should().Equal(issued.RawData);
            imported.Certificate.HasPrivateKey.Should().BeTrue();
            imported.Chain.Should().ContainSingle().Which.Should().Match<X509Certificate2>(link => link.RawData.SequenceEqual(authority.RawData) && !link.HasPrivateKey);
            imported.Subject.Should().Be("roofpi");
            imported.Issuer.Should().Be("HVO Roof CA (roofpi, 2026-10-01)");
            imported.Problems(Today).Should().BeEmpty();
            imported.Warnings(InstallerCertificates.Names).Should().BeEmpty();
        }
    }

    [TestMethod]
    public void APfx_WithoutAPassword_AsksForNone()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);

        using var imported = ImportedCertificate.Read("roof.pfx", selfSigned.Export(X509ContentType.Pkcs12), null, null, _ => throw new AssertFailedException("it needs no password"));

        imported.Certificate.RawData.Should().Equal(selfSigned.RawData);
        imported.Chain.Should().BeEmpty();
        imported.Issuer.Should().Be("itself");
    }

    [TestMethod]
    public void APfx_WhosePasswordIsWrongOrNotGiven_IsAUsageError()
    {
        var pfx = SelfSignedPfx();

        FluentActions.Invoking(() => ImportedCertificate.Read("roof.pfx", pfx, null, null, _ => "wrong-password-not-a-secret"))
            .Should().Throw<InstallerUsageException>().WithMessage("The password does not open roof.pfx.");
        FluentActions.Invoking(() => ImportedCertificate.Read("roof.pfx", pfx, null, null, _ => null))
            .Should().Throw<InstallerUsageException>().WithMessage("roof.pfx needs a password: give it with --password-file FILE, or run the installer in a terminal to type it.");
        FluentActions.Invoking(() => ImportedCertificate.Read("roof.pfx", pfx, "roof.key", Encoding.UTF8.GetBytes("key"), _ => null))
            .Should().Throw<InstallerUsageException>().WithMessage("--key is for a certificate in PEM, and roof.pfx is not PEM:*");
    }

    [TestMethod]
    public void Pem_WithTheKeyInTheSameFile_OrItsOwn_IsRead()
    {
        var (authority, issued) = InstallerCertificates.Issue(Today);
        using (authority)
        using (issued)
        using (var key = issued.GetECDsaPrivateKey()!)
        {
            var chain = issued.ExportCertificatePem() + "\n" + authority.ExportCertificatePem() + "\n";

            using (var together = ImportedCertificate.Read("roof.pem", Encoding.UTF8.GetBytes(chain + key.ExportPkcs8PrivateKeyPem()), null, null, _ => null))
            {
                together.Certificate.RawData.Should().Equal(issued.RawData);
                together.Certificate.HasPrivateKey.Should().BeTrue();
                together.Chain.Should().ContainSingle().Which.RawData.Should().Equal(authority.RawData);
            }

            using var apart = ImportedCertificate.Read("roof.crt", Encoding.UTF8.GetBytes(chain), "roof.key", Encoding.UTF8.GetBytes(key.ExportECPrivateKeyPem()), _ => null);
            apart.Certificate.RawData.Should().Equal(issued.RawData);
            apart.Certificate.GetECDsaPrivateKey()!.ExportSubjectPublicKeyInfo().Should().Equal(key.ExportSubjectPublicKeyInfo());
        }
    }

    [TestMethod]
    public void AnEncryptedKey_IsOpenedWithItsPassword()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);
        using var key = selfSigned.GetECDsaPrivateKey()!;
        var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(InstallerCertificates.Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));
        var certificate = Encoding.UTF8.GetBytes(selfSigned.ExportCertificatePem());
        var asked = new List<string>();

        using var imported = ImportedCertificate.Read("roof.crt", certificate, "roof.key", Encoding.UTF8.GetBytes(encrypted), what =>
        {
            asked.Add(what);
            return InstallerCertificates.Password;
        });

        asked.Should().Equal("roof.key's password");
        imported.Certificate.HasPrivateKey.Should().BeTrue();
        FluentActions.Invoking(() => ImportedCertificate.Read("roof.crt", certificate, "roof.key", Encoding.UTF8.GetBytes(encrypted), _ => "wrong-password-not-a-secret"))
            .Should().Throw<InstallerUsageException>().WithMessage("The password does not open the key in roof.key, or it is not an RSA or ECDSA key.");
        FluentActions.Invoking(() => ImportedCertificate.Read("roof.crt", certificate, "roof.key", Encoding.UTF8.GetBytes(encrypted), _ => null))
            .Should().Throw<InstallerUsageException>().WithMessage("roof.key needs a password:*");
    }

    [TestMethod]
    public void AnRsaCertificate_InPem_IsRead()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=roofpi", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], false));
        using var certificate = request.CreateSelfSigned(Today.AddHours(-1), Today.AddDays(90));

        using var imported = ImportedCertificate.Read("roof.pem", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem() + "\n" + rsa.ExportRSAPrivateKeyPem() + "\n"), null, null, _ => null);

        imported.Certificate.GetRSAPrivateKey().Should().NotBeNull();
        imported.Problems(Today).Should().BeEmpty();
    }

    [TestMethod]
    public void WhatCannotBeRead_SaysWhatToGive()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key = selfSigned.GetECDsaPrivateKey()!;
        var pem = selfSigned.ExportCertificatePem() + "\n";

        Refusal("roof.crt", Encoding.UTF8.GetBytes(pem)).Should().Be("roof.crt holds no private key: give the key's file with --key FILE.");
        Refusal("roof.crt", Encoding.UTF8.GetBytes(pem), "roof.key", Encoding.UTF8.GetBytes(other.ExportPkcs8PrivateKeyPem()))
            .Should().Be("The key in roof.key is not the key of any certificate in roof.crt.");
        Refusal("roof.pem", Encoding.UTF8.GetBytes(pem + key.ExportPkcs8PrivateKeyPem() + "\n" + other.ExportPkcs8PrivateKeyPem() + "\n"))
            .Should().Be("roof.pem holds more than one private key: give the certificate's own key.");
        Refusal("roof.cer", selfSigned.RawData).Should().Be("roof.cer holds a certificate but not its key: give the key with --key FILE (PEM), or give a PKCS#12 file with both.");
        Refusal("notes.txt", Encoding.UTF8.GetBytes("not a certificate")).Should().StartWith("notes.txt is not a certificate the installer can read:");
        Refusal("random.bin", RandomNumberGenerator.GetBytes(64)).Should().StartWith("random.bin is not a certificate the installer can read:");
        Refusal("key.pem", Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem())).Should().Be("key.pem holds no certificate.");
    }

    [TestMethod]
    public void ACertificateTheControllerCannotServe_HasItsProblems()
    {
        using var authority = TestCertificates.CreateAuthority("Their CA");
        using var client = TestCertificates.Issue(authority, usage: TestCertificates.ClientAuthentication);
        using var later = TestCertificates.Issue(authority, notBefore: Today.AddDays(2), notAfter: Today.AddDays(90));
        using var expired = TestCertificates.Issue(authority, notBefore: Today.AddDays(-90), notAfter: Today.AddDays(-1));

        Problems(authority, DateTimeOffset.UtcNow).Should().Equal("It is a certificate authority's certificate, not a server's: give the certificate issued for the controller.");
        Problems(client, DateTimeOffset.UtcNow).Should().Equal("It is not for a server: its extended key usage leaves out server authentication, so clients refuse it.");
        Problems(later, Today).Should().Equal($"It is not valid until {AuthorityAssessment.Date(later.NotBefore)}: check this machine's clock, or import it then.");
        Problems(expired, Today).Should().Equal($"It expired on {AuthorityAssessment.Date(expired.NotAfter)}.");
    }

    [TestMethod]
    public void ACertificateSomeClientsRefuse_HasWarnings()
    {
        using var authority = TestCertificates.CreateAuthority("Their CA");
        using var narrow = TestCertificates.Issue(authority, ["roofpi.observatory.example", "192.168.1.20"], Today, Today.AddDays(900));

        using var imported = ImportedCertificate.Read("roof.pfx", narrow.Export(X509ContentType.Pkcs12), null, null, _ => null);

        imported.Warnings(InstallerCertificates.Names).Should().Equal(
            "It is not for roofpi, roofpi.local, localhost, 127.0.0.1, ::1: a client that uses one refuses it.",
            "It is valid for more than 825 days, so macOS and iOS refuse it.");
        imported.Issuer.Should().Be("Their CA");
    }

    private static string Refusal(string name, byte[] content, string? keyName = null, byte[]? keyContent = null)
    {
        try
        {
            ImportedCertificate.Read(name, content, keyName, keyContent, _ => null).Dispose();
        }
        catch (InstallerUsageException error)
        {
            return error.Message;
        }

        throw new AssertFailedException($"{name} was read");
    }

    private static IReadOnlyList<string> Problems(X509Certificate2 certificate, DateTimeOffset now)
    {
        using var imported = ImportedCertificate.Read("roof.pfx", certificate.Export(X509ContentType.Pkcs12), null, null, _ => null);
        return imported.Problems(now);
    }

    private static byte[] SelfSignedPfx()
    {
        using var selfSigned = ControllerCertificates.SelfSigned(InstallerCertificates.Names, Today);
        return ControllerCertificates.ExportPfx(selfSigned, null, InstallerCertificates.Password);
    }

    private static async Task<(int Exit, string Output)> OpensslAsync(string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/openssl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
