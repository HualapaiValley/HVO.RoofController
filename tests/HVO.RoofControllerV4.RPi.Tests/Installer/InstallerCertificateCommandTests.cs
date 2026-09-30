using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// <c>hvo-roof-install cert</c>, <c>cert show</c> and <c>cert import</c> on fake machines (#68): they need root to change
/// the controller's files but not to plan or show, make or renew only what needs it, refuse what the controller could
/// not serve, ask for a password only when a file needs one, and never show, log or keep it.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCertificateCommandTests
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    // Not a secret: the password of the test's own certificate file.
    private const string TheirPassword = "their-pfx-password-not-a-secret";

    private static readonly ControllerLayout Layout = ControllerLayout.System;

    [TestMethod]
    public async Task Cert_MakesTheCaAndTheCertificate_ForTheRecordedController_ThenChangesNothing()
    {
        using var pi = Recorded(new FakeMachine().WithPi());

        var run = await pi.RunAsync("cert");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("  create     /etc/hvo-roof/ca.crt")
            .And.Contain("Creating file /etc/hvo-roof/ca.crt: done.")
            .And.Contain("Creating file /etc/hvo-roof/https/roof-controller.pfx: done.");
        var lines = Lines(run.Output);
        lines.Should().ContainInOrder(
            "Certificate: /etc/hvo-roof/https/roof-controller.pfx",
            "  Subject:   roofpi",
            "  Issued by: HVO Roof CA (roofpi, 2026-10-01), this machine's CA",
            "  Valid:     until 2027-11-02, 397 days left",
            "  For:       roofpi, roofpi.local, localhost, 192.168.1.50, 127.0.0.1, ::1");
        lines.Should().Contain(line => line.StartsWith("  SHA-256:   ", StringComparison.Ordinal) && line.Length == "  SHA-256:   ".Length + 95);
        lines.Should().ContainInOrder("CA:          /etc/hvo-roof/ca.crt", "  Subject:   HVO Roof CA (roofpi, 2026-10-01)");
        lines.Should().Contain(line => line.StartsWith("  Issues for: local, localhost, roofpi, ", StringComparison.Ordinal));
        lines.Should().Contain("  Clients get it from https://roofpi.local:8443/ca.crt, or from /etc/hvo-roof/ca.crt");
        run.Output.Should().EndWith("The controller serves it once it is deployed again: when the roof is idle, run the deploy script again (docs/deployment.md).\n");
        run.Error.Should().BeEmpty();
        pi.Read(InstallPaths.SystemLog).Should().Contain("Put the controller's certificate in place.");
        pi.Mode(Layout.CaKey).Should().Be(Modes.PrivateFile);

        var before = pi.Snapshot(InstallPaths.SystemLog);
        var again = await pi.RunAsync("cert");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("Nothing to change: the controller's certificate is in place as it should be.").And.NotContain("Creating").And.NotContain("deployed again");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CertPlan_ChangesNothing_AndSaysWhenChangesNeedRoot()
    {
        using var pi = Recorded(new FakeMachine(root: false).WithPi());
        var before = pi.Snapshot();

        var run = await pi.RunAsync("cert", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("  create     /etc/hvo-roof/ca.crt")
            .And.Contain("  create     /etc/hvo-roof/https/roof-controller.pfx")
            .And.Contain("Making these changes needs root: run it with sudo.")
            .And.NotContain("Creating");
        pi.Snapshot().Should().Equal(before);
    }

    [TestMethod]
    public async Task Cert_WithoutRoot_IsRefused_BeforeAnythingChanges()
    {
        using var pi = Recorded(new FakeMachine(root: false).WithPi());
        var before = pi.Snapshot();

        var run = await pi.RunAsync("cert");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("The controller's certificate files are root's: run it with sudo, sudo hvo-roof-install cert. To see what it would do first, add --plan.");
        pi.Snapshot().Should().Equal(before);
    }

    [TestMethod]
    public async Task CertRenew_IssuesItAgain_AndNewCa_MakesANewCa()
    {
        using var pi = Recorded(new FakeMachine().WithPi().WithCertificates());
        var authority = pi.Read(Layout.CaCertificate);

        var renewed = await pi.RunAsync("cert", "--renew");

        renewed.ExitCode.Should().Be(0, renewed.ToString());
        renewed.Output.Should().Contain("(issued again, as asked (--renew): it was valid until 2027-11-02)").And.Contain("deployed again");
        pi.Read(Layout.CaCertificate).Should().Be(authority);

        var replaced = await pi.RunAsync("cert", "--new-ca");

        replaced.ExitCode.Should().Be(0, replaced.ToString());
        replaced.Output.Should().Contain("a person asked for a new one (--new-ca): a new CA, which every client must trust in place of this one");
        pi.Read(Layout.CaCertificate).Should().NotBe(authority);
    }

    [TestMethod]
    public async Task Cert_WhatItCannotDo_IsRefusedOrAUsageError()
    {
        using var none = new FakeMachine().WithPi();
        using var http = Recorded(new FakeMachine().WithPi(), new ControllerSettings { Connection = ConnectionMode.Http });
        using var selfSigned = Recorded(new FakeMachine().WithPi(), new ControllerSettings { Connection = ConnectionMode.SelfSigned });
        using var own = Recorded(new FakeMachine().WithPi(), new ControllerSettings { Connection = ConnectionMode.OwnCertificate });

        var nothing = await none.RunAsync("cert");
        var plain = await http.RunAsync("cert");
        var newCa = await selfSigned.RunAsync("cert", "--new-ca");
        var renew = await own.RunAsync("cert", "--renew");
        var missing = await own.RunAsync("cert");

        nothing.ExitCode.Should().Be((int)InstallerExitCode.Refused, nothing.ToString());
        nothing.Error.Should().Contain("Nothing here is recorded as running the controller or a test rig: install it first (hvo-roof-install).");
        plain.ExitCode.Should().Be((int)InstallerExitCode.Refused, plain.ToString());
        plain.Error.Should().Contain("The controller serves plain HTTP, so it has no certificate.");
        newCa.ExitCode.Should().Be((int)InstallerExitCode.Usage, newCa.ToString());
        newCa.Error.Should().Contain("--new-ca is for a controller whose certificate the installer's CA issues, and this one's is self-signed.");
        renew.ExitCode.Should().Be((int)InstallerExitCode.Usage, renew.ToString());
        renew.Error.Should().Contain("The controller serves your own certificate, which its issuer renews: put a new one in place with hvo-roof-install cert import FILE.");
        missing.ExitCode.Should().Be((int)InstallerExitCode.Refused, missing.ToString());
        missing.Output.Should().Contain("there is none yet: give yours with hvo-roof-install cert import FILE, or choose private-ca");
    }

    [TestMethod]
    public async Task CertShow_SaysWhatIsInPlace_AndWarnsBeforeItExpires()
    {
        using var pi = Recorded(new FakeMachine().WithPi().WithCertificates());
        var before = pi.Snapshot();

        var now = await pi.RunAsync("cert", "show");
        pi.RunsAt = new FixedClock(FakeMachine.Today.AddDays(370));
        var soon = await pi.RunAsync("cert", "show");
        pi.RunsAt = new FixedClock(FakeMachine.Today.AddDays(400));
        var expired = await pi.RunAsync("cert", "show");

        now.ExitCode.Should().Be(0, now.ToString());
        Lines(now.Output).Should().ContainInOrder(
            "Certificate: /etc/hvo-roof/https/roof-controller.pfx",
            "  Subject:   roofpi",
            "  Issued by: HVO Roof CA (roofpi, 2026-10-01), this machine's CA",
            "  Valid:     until 2027-11-02, 397 days left",
            "  For:       roofpi, roofpi.local, localhost, 192.168.1.50, 127.0.0.1, ::1",
            "CA:          /etc/hvo-roof/ca.crt",
            "  Subject:   HVO Roof CA (roofpi, 2026-10-01)",
            "  Valid:     until 2036-09-28, 3650 days left");
        now.Error.Should().BeEmpty();
        Lines(soon.Output).Should().Contain("  Valid:     until 2027-11-02, 27 days left");
        soon.Error.Should().Be("Warning: The certificate expires on 2027-11-02, in 27 days. Renew it with: sudo hvo-roof-install cert\n");
        expired.ExitCode.Should().Be(0, "it only reports");
        Lines(expired.Output).Should().Contain("  Valid:     until 2027-11-02, expired 3 days ago");
        expired.Error.Should().Contain("Warning: The certificate expired on 2027-11-02: clients refuse it. Renew it with: sudo hvo-roof-install cert");
        pi.Snapshot().Should().Equal(before, "show changes nothing, not even the log");
    }

    [TestMethod]
    public async Task CertShow_SaysWhatIsMissing_AndWhatTheControllerCannotServe()
    {
        using var none = new FakeMachine().WithPi();
        using var noNames = Recorded(new FakeMachine().WithPi().WithCertificates());
        noNames.Write(Layout.PfxPassword, "not-the-password-not-a-secret");

        var empty = await none.RunAsync("cert", "show");
        var wrong = await noNames.RunAsync("cert", "show");

        empty.ExitCode.Should().Be(0, empty.ToString());
        empty.Output.Should().Be("Certificate: none in /etc/hvo-roof/https/roof-controller.pfx\n");
        wrong.Output.Should().StartWith("Certificate: /etc/hvo-roof/https/roof-controller.pfx: the password in /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password does not open it\n");
        wrong.Error.Should().Be("Warning: The controller cannot serve its certificate: the password in /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password does not open it.\n");
    }

    [TestMethod]
    public async Task CertShow_WithoutRoot_SaysToUseSudo()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("root reads a file whatever its mode");
        }

        using var pi = Recorded(new FakeMachine(root: false).WithPi().WithCertificates());
        File.SetUnixFileMode(pi.OnDisk(Layout.Pfx), UnixFileMode.None);
        try
        {
            var run = await pi.RunAsync("cert", "show");

            run.ExitCode.Should().Be(0, run.ToString());
            run.Output.Should().StartWith("Certificate: /etc/hvo-roof/https/roof-controller.pfx: only root can read it: run with sudo to see it\n");
            run.Error.Should().BeEmpty("without root there is nothing to warn about");
        }
        finally
        {
            File.SetUnixFileMode(pi.OnDisk(Layout.Pfx), Modes.PrivateFile);
        }
    }

    [TestMethod]
    public async Task CertImport_APfxWithAPassword_IsAskedFor_PutInPlace_AndRecorded()
    {
        using var pi = Recorded(new FakeMachine().WithPi().WithCertificates());
        var (their, theirs) = TheirCertificate();
        using (their)
        using (theirs)
        {
            WriteBytes(pi, "/root/their.pfx", theirs.Export(X509ContentType.Pkcs12, TheirPassword));
            pi.Types = what => what == "/root/their.pfx's password" ? TheirPassword : null;

            var run = await pi.RunAsync("cert", "import", "~/their.pfx");

            run.ExitCode.Should().Be(0, run.ToString());
            pi.Asked.Should().Equal("/root/their.pfx's password");
            run.Output.Should().Contain("your certificate for the controller, from /root/their.pfx (roofpi, until 2026-12-30, issued by Their CA)")
                .And.Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.")
                .And.Contain("  Issued by: Their CA\n")
                .And.Contain("deployed again")
                .And.NotContain("recorded as running");
            run.Error.Should().BeEmpty();
            InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Controller!.Connection.Should().Be(ConnectionMode.OwnCertificate);
            using var inPlace = ControllerCertificates.LoadPfx(File.ReadAllBytes(pi.OnDisk(Layout.Pfx)), pi.Read(Layout.PfxPassword))!;
            inPlace.RawData.Should().Equal(theirs.RawData);
            inPlace.HasPrivateKey.Should().BeTrue();
            pi.Mode(Layout.Pfx).Should().Be(Modes.PrivateFile);
            pi.AllText().Should().NotContain(TheirPassword, "the password is never kept, logged or printed");
            (run.Output + run.Error).Should().NotContain(TheirPassword);

            var kept = await pi.RunAsync("cert");

            kept.ExitCode.Should().Be(0, kept.ToString());
            kept.Output.Should().Contain("(yours, kept: roofpi, until 2026-12-30)").And.Contain("Nothing to change: the controller's certificate is in place as it should be.");

            var again = await pi.RunAsync("cert", "import", "/root/their.pfx");

            again.ExitCode.Should().Be(0, again.ToString());
            again.Output.Should().Contain("already in place: roofpi, until 2026-12-30, issued by Their CA").And.Contain("Nothing to change: your certificate is in place as it should be.");
        }
    }

    [TestMethod]
    public async Task CertImport_PemWithItsKey_AndAPasswordFile_IsPutInPlace()
    {
        using var pi = new FakeMachine().WithPi();
        var (their, theirs) = TheirCertificate();
        using (their)
        using (theirs)
        using (var key = theirs.GetECDsaPrivateKey()!)
        {
            pi.Write("/root/their.crt", theirs.ExportCertificatePem() + "\n" + their.ExportCertificatePem() + "\n");
            pi.Write("/root/their.key", key.ExportEncryptedPkcs8PrivateKeyPem(TheirPassword, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)) + "\n");
            pi.Write("/root/password.txt", TheirPassword + "\n");

            var run = await pi.RunAsync("cert", "import", "their.crt", "--key", "their.key", "--password-file", "password.txt");

            run.ExitCode.Should().Be(0, run.ToString());
            pi.Asked.Should().BeEmpty("the file gives the password");
            run.Output.Should().Contain("Creating file /etc/hvo-roof/https/roof-controller.pfx: done.")
                .And.EndWith("Nothing here is recorded as running the controller yet: when you install it (hvo-roof-install), choose own-certificate to serve this one.\n");
            pi.Exists(InstallPaths.SystemRecord).Should().BeFalse("there is nothing to record yet");
            var inFile = X509CertificateLoader.LoadPkcs12Collection(File.ReadAllBytes(pi.OnDisk(Layout.Pfx)), pi.Read(Layout.PfxPassword), ControllerCertificates.KeyStorage);
            try
            {
                inFile.Select(certificate => certificate.RawData).Should().BeEquivalentTo([theirs.RawData, their.RawData], "its chain goes with it");
                inFile.Single(certificate => certificate.HasPrivateKey).RawData.Should().Equal(theirs.RawData);
            }
            finally
            {
                foreach (var certificate in inFile)
                {
                    certificate.Dispose();
                }
            }

            File.Delete(pi.OnDisk("/root/password.txt"));
            pi.AllText().Should().NotContain(TheirPassword, "the password is never kept, logged or printed");
            (run.Output + run.Error).Should().NotContain(TheirPassword);
        }
    }

    [TestMethod]
    public async Task CertImport_WithNoOneToAskForThePassword_IsAUsageError()
    {
        using var pi = new FakeMachine().WithPi();
        var (their, theirs) = TheirCertificate();
        using (their)
        using (theirs)
        {
            WriteBytes(pi, "/root/their.pfx", theirs.Export(X509ContentType.Pkcs12, TheirPassword));
            pi.Types = _ => null;

            var run = await pi.RunAsync("cert", "import", "/root/their.pfx");

            run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
            run.Error.Should().Contain("/root/their.pfx needs a password: give it with --password-file FILE, or run the installer in a terminal to type it.");
            pi.Exists(Layout.Pfx).Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task CertImport_WhatTheControllerCannotServe_IsRefused_AndWhatClientsMayRefuse_IsWarned()
    {
        using var pi = Recorded(new FakeMachine().WithPi().WithCertificates());
        var (their, expired) = TheirCertificate(notAfter: FakeMachine.Today.AddDays(-1));
        var (_, elsewhere) = TheirCertificate(names: ["other.example"]);
        using (their)
        using (expired)
        using (elsewhere)
        {
            WriteBytes(pi, "/root/expired.pfx", expired.Export(X509ContentType.Pkcs12));
            WriteBytes(pi, "/root/elsewhere.pfx", elsewhere.Export(X509ContentType.Pkcs12));
            pi.Folder("/var/log");
            var before = pi.Snapshot(InstallPaths.SystemLog);

            var refused = await pi.RunAsync("cert", "import", "/root/expired.pfx");

            refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
            refused.Error.Should().Contain("The installer cannot go ahead:").And.Contain("It expired on 2026-09-30.");
            pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
            pi.Read(InstallPaths.SystemLog).Should().Contain("Refused /root/expired.pfx: It expired on 2026-09-30.");

            var warned = await pi.RunAsync("cert", "import", "/root/elsewhere.pfx", "--plan");

            warned.ExitCode.Should().Be(0, warned.ToString());
            warned.Error.Should().Be("Warning: It is not for roofpi, roofpi.local, localhost, 192.168.1.50, 127.0.0.1, ::1: a client that uses one refuses it.\n");
            warned.Output.Should().Contain("  change     /etc/hvo-roof/https/roof-controller.pfx").And.Contain("  change     /etc/hvo-roof/install.json");
            pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before);
        }
    }

    [TestMethod]
    public async Task CertImport_WithoutRoot_IsRefused_BeforeItAsksForAPassword()
    {
        using var pi = new FakeMachine(root: false).WithPi();
        WriteBytes(pi, "/home/pi/their.pfx", RandomNumberGenerator.GetBytes(16));

        using var root = new FakeMachine().WithPi();

        var run = await pi.RunAsync("cert", "import", "their.pfx");
        var missing = await root.RunAsync("cert", "import", "/root/none.pfx");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("run it with sudo, sudo hvo-roof-install cert import FILE.");
        pi.Asked.Should().BeEmpty();
        missing.ExitCode.Should().Be((int)InstallerExitCode.Usage, missing.ToString());
        missing.Error.Should().Contain("There is no certificate file /root/none.pfx.");
    }

    [TestMethod]
    public async Task EveryRun_WarnsOfACertificateThatNeedsDoing()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.RunsAt = new FixedClock(FakeMachine.Today.AddDays(370));

        var plan = await pi.RunAsync("--plan", "--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Error.Should().Contain("Warning: The certificate expires on 2027-11-02, in 27 days. Renew it with: sudo hvo-roof-install cert");
    }

    [TestMethod]
    public async Task Help_ListsTheCertCommands()
    {
        using var pi = new FakeMachine();

        var help = await pi.RunAsync("--help");
        var cert = await pi.RunAsync("cert", "--help");

        help.Output.Should().Contain("cert");
        cert.Output.Should().Contain("show").And.Contain("import").And.Contain("--renew").And.Contain("--new-ca").And.Contain("--plan");
    }

    private static FakeMachine Recorded(FakeMachine machine, ControllerSettings? settings = null)
    {
        var record = InstallerGuardTests.Record(InstallRole.Controller, null) with { Controller = settings ?? new ControllerSettings() };
        machine.Write(InstallPaths.SystemRecord, record.ToJson());
        return machine;
    }

    private static string[] Lines(string output) => output.Split('\n');

    private static void WriteBytes(FakeMachine machine, string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(machine.OnDisk(path))!);
        File.WriteAllBytes(machine.OnDisk(path), content);
    }

    // "Their CA" and the certificate it issued for roofpi (by default), valid for 90 days from yesterday.
    private static (X509Certificate2 Authority, X509Certificate2 Certificate) TheirCertificate(string[]? names = null, DateTimeOffset? notAfter = null)
    {
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest("CN=Their CA", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        var authority = authorityRequest.CreateSelfSigned(FakeMachine.Today.AddDays(-400), FakeMachine.Today.AddDays(3650));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + (names?[0] ?? "roofpi"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var name in names ?? ["roofpi", "roofpi.local", "localhost"])
        {
            alternative.AddDnsName(name);
        }

        if (names is null)
        {
            foreach (var address in new[] { "192.168.1.50", "127.0.0.1", "::1" })
            {
                alternative.AddIpAddress(IPAddress.Parse(address));
            }
        }

        request.CertificateExtensions.Add(alternative.Build());
        var notBefore = notAfter is { } until ? until.AddDays(-90) : FakeMachine.Today.AddDays(-1);
        using var issued = request.Create(authority, notBefore, notAfter ?? FakeMachine.Today.AddDays(90), RandomNumberGenerator.GetBytes(16));
        return (authority, issued.CopyWithPrivateKey(key));
    }
}
