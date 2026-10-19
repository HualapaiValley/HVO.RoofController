using System.Runtime.InteropServices;
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
using HVO.RoofControllerV4.RPi.Tests.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Imported PKCS#12 keys are repackaged for a Mac rig, with their chain and private file permissions intact.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerMacCertificateImportTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MacRig_Pkcs12Import_RepackagesTheKeyAndChain_WithPrivateModes(bool encrypted)
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false);
        var original = InstallerGuardTests.Record(InstallRole.Rig, null) with { Scope = InstallScope.User };
        var recordPath = InstallPaths.UserRecord(mac.Machine);
        mac.Write(recordPath, original.ToJson());
        var layout = ControllerLayout.For(mac.Machine);
        layout.Should().Be(ControllerLayout.MacRig(mac.Home));
        var names = CertificateNames.For(mac.Machine, original.Controller!);
        using var authority = ControllerCertificates.CreateAuthority(names, FakeMachine.Today);
        using var certificate = ControllerCertificates.Issue(authority, names, FakeMachine.Today);
        var inputPassword = encrypted ? TestSecrets.Password : string.Empty;
        var content = ControllerCertificates.ExportPfx(certificate, authority, inputPassword);
        var asked = 0;
        using var imported = ImportedCertificate.Read("fixture.pfx", content, null, null, _ =>
        {
            asked++;
            return TestSecrets.Password;
        });
        asked.Should().Be(encrypted ? 1 : 0);
        imported.Certificate.RawData.Should().Equal(certificate.RawData);
        imported.Problems(FakeMachine.Today).Should().BeEmpty();

        var session = await InstallerSession.StartAsync(mac.Machine, InstallLog.None, "4.0.0", FakeMachine.Clock);
        var plan = PlanBuilder.BuildImport(mac.Machine, imported, "fixture.pfx", (InstallScope.User, recordPath));
        var check = await plan.CheckAsync(session.Context, CancellationToken.None);
        check.HasChanges.Should().BeTrue();
        await check.ApplyAsync(session.Context);

        foreach (var path in new[] { layout.Pfx, layout.PfxPassword })
        {
            mac.Mode(path).Should().Be(Modes.PrivateFile);
            File.GetUnixFileMode(mac.OnDisk(path)).Should().Be(Modes.PrivateFile);
        }
        mac.Mode(layout.Https).Should().Be(Modes.PrivateFolder);
        var password = mac.Read(layout.PfxPassword);
        string.IsNullOrEmpty(password).Should().BeFalse();
        string.Equals(password, inputPassword, StringComparison.Ordinal).Should().BeFalse("the installer keeps its own file password");
        using var installed = ControllerCertificates.LoadPfx(File.ReadAllBytes(mac.OnDisk(layout.Pfx)), password);
        installed.Should().NotBeNull();
        installed!.RawData.Should().Equal(certificate.RawData);
        installed.HasPrivateKey.Should().BeTrue();
        var chain = ControllerCertificates.PfxChain(File.ReadAllBytes(mac.OnDisk(layout.Pfx)), password);
        chain.Should().ContainSingle().Which.Should().Equal(authority.RawData);
        using var signingKey = installed.GetECDsaPrivateKey();
        using var publicKey = certificate.GetECDsaPublicKey();
        byte[] challenge = [1, 2, 3, 4];
        var signature = signingKey!.SignData(challenge, HashAlgorithmName.SHA256);
        publicKey!.VerifyData(challenge, signature, HashAlgorithmName.SHA256).Should().BeTrue();
        var recorded = InstallRecord.Parse(mac.Read(recordPath));
        recorded.Scope.Should().Be(original.Scope);
        recorded.Roles.Should().Equal(original.Roles);
        recorded.Hat.Should().Be(original.Hat);
        recorded.Controller!.Connection.Should().Be(ConnectionMode.OwnCertificate);
        var again = await plan.CheckAsync(session.Context, CancellationToken.None);
        again.HasChanges.Should().BeFalse("the imported certificate is already in place");
        mac.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public void MacRig_Pkcs12Import_RejectsAWrongPassword_BeforeWriting()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false);
        var names = CertificateNames.For(mac.Machine, new ControllerSettings());
        using var authority = ControllerCertificates.CreateAuthority(names, FakeMachine.Today);
        using var certificate = ControllerCertificates.Issue(authority, names, FakeMachine.Today);
        var content = ControllerCertificates.ExportPfx(certificate, authority, TestSecrets.Password);
        var before = mac.Snapshot();
        Action read = () =>
        {
            using var imported = ImportedCertificate.Read("fixture.pfx", content, null, null, _ => TestSecrets.OtherPassword);
        };

        read.Should().Throw<InstallerUsageException>().WithMessage("The password does not open fixture.pfx.");
        mac.Snapshot().Should().Equal(before);
        mac.Exists(ControllerLayout.For(mac.Machine).Pfx).Should().BeFalse();
    }
}
