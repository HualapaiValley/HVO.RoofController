using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.RPi.Tests.Client;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>The fake Linux machine retains real opened-file identity on each supported test host.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class FakeMachinePortabilityTests
{
    [TestMethod]
    [DataRow("/etc/hvo-roof/settings.json.", ".tmp")]
    [DataRow("\"id\": \"", "\"")]
    [DataRow("\"principalId\":\"", "\"")]
    [DataRow("\"ownerStamp\" : \"", "\"")]
    [DataRow("\"stamp\": \"", "\"")]
    public void RandomNames_NormalizesAtomicFileAndKioskGuids_ThatContainASyntheticPin(string before, string after)
    {
        using var machine = new FakeMachine();
        const string guid = "aabbccdd9081eeff0011223344556677";
        var probe = before + guid + after;

        // The PIN check used to match the synthetic digits inside this otherwise unrelated GUID.
        machine.WithoutRandomNames(probe).Contains("9081", StringComparison.Ordinal).Should().BeFalse();
        machine.WithoutRandomNames(probe).Should().Be(before + "<random>" + after);
    }

    [TestMethod]
    public void RandomNames_PreservesActualPinsAndUnrelatedOrIncompleteHexValues()
    {
        using var machine = new FakeMachine();
        const string guid = "aabbccdd9081eeff0011223344556677";
        string[] probes =
        [
            "PIN: 9081",
            "\"pin\": \"9081\"",
            "/etc/hvo-roof/settings.json.9081.tmp",
            "/etc/hvo-roof/settings.json." + guid + "a.tmp",
            "/etc/hvo-roof/settings.json." + guid + ".tmpother",
            "hvo-roof-install-" + guid + "a",
            "\"principalId\":\"" + guid + "a\"",
            "\"apiKey\":\"" + guid + "\"",
            "a plain hex value: " + guid
        ];

        foreach (var probe in probes)
            machine.WithoutRandomNames(probe).Should().Be(probe);
    }

    [TestMethod]
    [DataRow("rename")]
    [DataRow("unlink")]
    [DataRow("replace")]
    public async Task OpenedFileStat_UsesTheLiveStreamAndOwner_AfterItsPathChanges(string change)
    {
        using var machine = new FakeMachine().Write("/root/opened", "original bytes");
        machine.Owners["/root/opened"] = "root:root";
        using var stream = (FileStream)machine.Machine.OpenReadOnDisk(machine.OnDisk("/root/opened"));
        var descriptor = stream.SafeFileHandle.DangerousGetHandle();
        var opened = $"/proc/{Environment.ProcessId}/fd/{descriptor}";

        if (change == "unlink")
            File.Delete(machine.OnDisk("/root/opened"));
        else if (change == "rename")
            File.Move(machine.OnDisk("/root/opened"), machine.OnDisk("/root/moved"));
        else
        {
            machine.Write("/root/replacement", "replacement bytes");
            File.Move(machine.OnDisk("/root/replacement"), machine.OnDisk("/root/opened"), overwrite: true);
        }
        // These are the current path's metadata, not the already-opened inode's owner.
        machine.Owners["/root/opened"] = "someone:else";

        var stat = await machine.RunAsync(new CommandLine("stat", "-L", "-c", "%F|%U:%G", "--", opened));

        stat.ExitCode.Should().Be(0);
        stat.Output.Trim().Should().Be("regular file|root:root");
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Encoding.UTF8.GetString(bytes.ToArray()).Should().Be("original bytes");
    }

    [TestMethod]
    public async Task OpenedFileStat_RefusesClosedForeignUnregisteredAndMalformedDescriptors()
    {
        using var machine = new FakeMachine().Write("/root/opened", "original bytes");
        var stream = (FileStream)machine.Machine.OpenReadOnDisk(machine.OnDisk("/root/opened"));
        var descriptor = stream.SafeFileHandle.DangerousGetHandle();
        var opened = $"/proc/{Environment.ProcessId}/fd/{descriptor}";
        var foreign = $"/proc/{Environment.ProcessId + 1}/fd/{descriptor}";
        (await StatAsync(foreign)).ExitCode.Should().NotBe(0);
        (await StatAsync(opened + "/extra")).ExitCode.Should().NotBe(0);
        (await StatAsync($"/proc/{Environment.ProcessId}/fd/+{descriptor}")).ExitCode.Should().NotBe(0);
        (await StatAsync($"/proc/{Environment.ProcessId}/fd/0{descriptor}")).ExitCode.Should().NotBe(0);
        (await StatAsync($"/proc/{Environment.ProcessId}/fd/not-a-descriptor")).ExitCode.Should().NotBe(0);
        stream.Dispose();
        (await StatAsync(opened)).ExitCode.Should().NotBe(0);

        // Even a real host descriptor (including a reused number) is not a file this fake opened.
        using var unregistered = File.OpenRead(machine.OnDisk("/root/opened"));
        (await StatAsync($"/proc/{Environment.ProcessId}/fd/{unregistered.SafeFileHandle.DangerousGetHandle()}")).ExitCode.Should().NotBe(0);

        Task<CommandResult> StatAsync(string path)
            => machine.RunAsync(new CommandLine("stat", "-L", "-c", "%F|%U:%G", "--", path));
    }

    [TestMethod]
    public async Task OpenedFileStat_RetainsTheNonRootOwnerAndActualEmptyFileType()
    {
        using var machine = new FakeMachine().Write("/root/opened", string.Empty);
        machine.Owners["/root/opened"] = "someone:else";
        using var stream = (FileStream)machine.Machine.OpenReadOnDisk(machine.OnDisk("/root/opened"));
        File.Delete(machine.OnDisk("/root/opened"));
        machine.Write("/root/opened", "a different file");
        machine.Owners["/root/opened"] = "root:root";

        var stat = await machine.RunAsync(new CommandLine("stat", "-L", "-c", "%F|%U:%G", "--", $"/proc/{Environment.ProcessId}/fd/{stream.SafeFileHandle.DangerousGetHandle()}"));

        stat.ExitCode.Should().Be(0);
        stat.Output.Trim().Should().Be("regular empty file|someone:else");
    }

    [TestMethod]
    public void FixtureCertificateRoundTrip_RetainsAnExportablePrivateKey()
    {
        using var authority = TestCertificates.CreateAuthority("Portable fixture authority");
        var pfx = authority.Export(X509ContentType.Pkcs12);
        using var exported = X509CertificateLoader.LoadPkcs12(pfx, null);

        exported.HasPrivateKey.Should().BeTrue();
        exported.RawData.Should().Equal(authority.RawData);
        using var signingKey = exported.GetECDsaPrivateKey()!;
        using var publicKey = authority.GetECDsaPublicKey()!;
        byte[] challenge = [1, 2, 3];
        publicKey.VerifyData(challenge, signingKey.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256).Should().BeTrue();
    }
}
