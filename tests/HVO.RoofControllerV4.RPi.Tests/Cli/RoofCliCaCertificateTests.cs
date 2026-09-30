using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof</c> trusting a private CA instead of pinning the certificate (#65): <c>setup --ca-certificate</c>,
/// <c>setup --ca-sha256</c> (#71), which fetches the CA from the controller, and <c>HVO_ROOF_CA_CERT</c>, over real HTTPS to
/// a controller whose certificate a CA made here issued. The pin and the CA are never both in force, a fetched CA is
/// saved only when its SHA-256 is the one given or confirmed, and every refusal says why.
/// </summary>
[TestClass]
public sealed class RoofCliCaCertificateTests
{
    private const string AuthorityName = "HVO Roof test CA";

    private static readonly Uri Plain = new("http://localhost/");

    private static readonly string Pin = new('A', 64);

    // ---- Over HTTPS ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_WithTheCa_Connects_AndKeepsWorkingWhenTheCertificateIsReissued()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);
        var file = WriteCa(rig, authority);

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString(), "--ca-certificate", file);

        setup.Code.Should().Be(RoofExitCode.Success, setup.ToString());
        setup.Out.Should().Contain($"{controller.BaseAddress}, CA {AuthorityName} trusted.")
            .And.Contain("Reachable: the controller says it is healthy.");
        rig.Stored!.CaCertificate.Should().Be(RoofCertificateAuthority.ToPem(authority));
        rig.Stored.CertificateSha256.Should().BeNull();

        using var reissued = TestCertificates.Issue(authority);
        controller.Present(reissued);
        var whoami = await rig.RunAsync("whoami");

        whoami.Code.Should().Be(RoofExitCode.Success, whoami.ToString());
        whoami.Out.Should().Contain($"Name:       {TlsTestController.CallerName}");
        controller.Presented.Should().Contain(reissued.Thumbprint, "the reissued certificate was the one accepted");
    }

    [TestMethod]
    public async Task ACertificateAnotherCaIssued_IsRefused_AndSetupSaysWhatToSave()
    {
        using var trusted = TestCertificates.CreateAuthority(AuthorityName);
        using var other = TestCertificates.CreateAuthority("Some other CA");
        using var issued = TestCertificates.Issue(other);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString(), "--ca-certificate", WriteCa(rig, trusted));
        var whoami = await rig.RunAsync("whoami");

        setup.Code.Should().Be(RoofExitCode.Unreachable, setup.ToString());
        setup.Out.Should().Contain(
            $"Not reachable: The controller's certificate was not issued by the CA this client trusts ({AuthorityName}). "
            + "If the controller's CA was replaced on purpose, save the new one with --ca-sha256 or --ca-certificate.");
        whoami.Code.Should().Be(RoofExitCode.Unreachable, whoami.ToString());
        whoami.Error.Should().Contain($"The controller's certificate was not issued by the CA this client trusts ({AuthorityName}).");
    }

    [TestMethod]
    public async Task ACertificateThatIsNotThePinnedOne_SetupSaysWhatToSave()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString(), "--certificate-sha256", Pin);

        setup.Code.Should().Be(RoofExitCode.Unreachable, setup.ToString());
        setup.Out.Should().Contain(
            "Not reachable: The controller's certificate is not the pinned one, and this computer does not trust it. "
            + "If the controller's certificate was replaced on purpose, save its SHA-256 with --certificate-sha256, or the CA that issued it with --ca-sha256 or --ca-certificate.");
    }

    [TestMethod]
    public async Task ACertificateTheSystemDoesNotTrust_WithNeitherSaved_SetupSaysWhatToSave()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString());

        setup.Code.Should().Be(RoofExitCode.Unreachable, setup.ToString());
        setup.Out.Should().Contain(
            "The certificate was not accepted: save the CA that issued it with --ca-sha256 or --ca-certificate, or, for a self-signed certificate, its SHA-256 with --certificate-sha256.");
    }

    [TestMethod]
    public async Task ACertificateForAnotherName_IsRefused_WithoutAHintToSaveSomethingElse()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority, ["roof.example"]);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);

        var setup = await rig.RunAsync("setup", "--controller", controller.LocalhostAddress.ToString(), "--ca-certificate", WriteCa(rig, authority));

        setup.Code.Should().Be(RoofExitCode.Unreachable, setup.ToString());
        setup.Out.Should().Contain("Not reachable: The controller's certificate is not for localhost; it names roof.example.")
            .And.NotContain("--ca-certificate", "saving another CA would not help: the certificate needs reissuing")
            .And.NotContain("--certificate-sha256");
    }

    [TestMethod]
    public async Task TheEnvironmentsCa_IsUsed_InsteadOfTheFilesPin()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        using var rig = new CliRig((RoofApiTestHost?)null);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = controller.BaseAddress, CertificateSha256 = Pin });
        rig.Environment[RoofCredentialStore.CaCertificateVariable] = WriteCa(rig, authority);

        var whoami = await rig.RunAsync("whoami");

        whoami.Code.Should().Be(RoofExitCode.Success, whoami.ToString());
    }

    // ---- setup: fetching the CA (#71) ------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_WithTheCasSha256_FetchesTheCa_ReplacesAPin_AndConnects()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        controller.ServedAuthority = authority;
        using var rig = new CliRig((RoofApiTestHost?)null);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = controller.BaseAddress, CertificateSha256 = Pin });
        var sha = RoofCertificateAuthority.Fingerprint(authority).Replace(":", string.Empty).ToLowerInvariant();

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString(), "--ca-sha256", sha);

        setup.Code.Should().Be(RoofExitCode.Success, setup.ToString());
        setup.Out.Should().Contain($"Fetched the CA {AuthorityName} from the controller: its SHA-256 is the one given.")
            .And.Contain($"{controller.BaseAddress}, CA {AuthorityName} trusted.")
            .And.Contain("The saved certificate pin was removed: the CA is trusted instead.")
            .And.Contain("Reachable: the controller says it is healthy.");
        rig.Stored!.CaCertificate.Should().Be(RoofCertificateAuthority.ToPem(authority));
        rig.Stored.CertificateSha256.Should().BeNull();
        controller.CaRequestCredentials.Should().Equal([false], "the CA is fetched with no credential");

        using var reissued = TestCertificates.Issue(authority);
        controller.Present(reissued);
        (await rig.RunAsync("whoami")).Code.Should().Be(RoofExitCode.Success);
    }

    [TestMethod]
    public async Task Setup_WithTheCasSha256_AsJson_GivesTheSha256()
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        controller.ServedAuthority = authority;
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync(
            "setup", "--json", "--controller", controller.BaseAddress.ToString(), "--ca-sha256", RoofCertificateAuthority.Fingerprint(authority));

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        using var json = JsonDocument.Parse(result.Out);
        json.RootElement.GetProperty("caCertificate").GetString().Should().Be(AuthorityName);
        json.RootElement.GetProperty("caCertificateSha256").GetString().Should().Be(RoofCertificateAuthority.Fingerprint(authority));
    }

    [TestMethod]
    [DataRow("other", "The CA the controller serves (HVO Roof test CA, SHA-256 *) is not the one given, so it is not saved. Compare it with 'hvo-roof-install cert show' on the controller.", DisplayName = "another SHA-256")]
    [DataRow("remade", "The CA the controller serves (HVO Roof test CA) did not issue the certificate it presents, so it is not saved.", DisplayName = "a CA that did not issue the certificate")]
    [DataRow("none", "The controller serves no CA at *ca.crt: a private CA did not issue its certificate, *", DisplayName = "no CA")]
    [DataRow("name", "The controller's certificate is not for 127.0.0.1; it names roof.example.", DisplayName = "a certificate for another name")]
    public async Task Setup_WithTheCasSha256_SavesNothing_UnlessTheCaIsTheOneGiven_AndIssuedTheCertificate(string served, string message)
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var remade = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority, served == "name" ? ["roof.example"] : null);
        await using var controller = await TlsTestController.StartAsync(issued);
        controller.ServedAuthority = served switch { "remade" => remade, "none" => null, _ => authority };
        using var rig = new CliRig((RoofApiTestHost?)null);
        var sha = served == "other" ? new string('0', 64) : RoofCertificateAuthority.Fingerprint(served == "remade" ? remade : authority);

        var setup = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString(), "--ca-sha256", sha);

        setup.Code.Should().Be(RoofExitCode.Refused, setup.ToString());
        setup.Error.Should().Match(message + Environment.NewLine);
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("--ca-certificate", DisplayName = "with a CA file")]
    [DataRow("--certificate-sha256", DisplayName = "with a pin")]
    [DataRow("http", DisplayName = "over http")]
    [DataRow("short", DisplayName = "not a SHA-256")]
    public async Task Setup_WithTheCasSha256_IsAUsageError_WithAnotherTrustOrOverHttpOrMalformed(string kind)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        string[] arguments = kind switch
        {
            "--ca-certificate" => ["--controller", "https://127.0.0.1:9/", "--ca-sha256", Pin, "--ca-certificate", WriteCa(rig, authority)],
            "--certificate-sha256" => ["--controller", "https://127.0.0.1:9/", "--ca-sha256", Pin, "--certificate-sha256", Pin],
            "http" => ["--controller", Plain.ToString(), "--ca-sha256", Pin],
            _ => ["--controller", "https://127.0.0.1:9/", "--ca-sha256", "AB:CD"]
        };

        var result = await rig.RunAsync(["setup", .. arguments]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(kind switch
        {
            "http" => $"The CA is fetched over HTTPS, and {Plain} is not an https address.",
            "short" => "The CA's SHA-256 must be 64 hex digits (colons allowed).",
            _ => "--ca-sha256 fetches the CA to save, so give it without --ca-certificate or --certificate-sha256."
        });
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task Setup_WithTheCasSha256_WhenTheControllerCannotBeReached_IsUnreachable()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("setup", "--controller", "https://127.0.0.1:9/", "--ca-sha256", Pin);

        result.Code.Should().Be(RoofExitCode.Unreachable, result.ToString());
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("y", DisplayName = "confirmed")]
    [DataRow("", DisplayName = "not confirmed")]
    public async Task Setup_InATerminal_FetchesTheCa_ShowsItsSha256_AndSavesItOnlyWhenConfirmed(string answer)
    {
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        await using var controller = await TlsTestController.StartAsync(issued);
        controller.ServedAuthority = authority;
        using var rig = new CliRig((RoofApiTestHost?)null) { Interactive = true };
        rig.Input.Enqueue("fetch");
        rig.Input.Enqueue(answer);
        rig.Input.Enqueue(string.Empty);

        var result = await rig.RunAsync("setup", "--controller", controller.BaseAddress.ToString());

        rig.Prompts.Select(prompt => prompt.Prompt).Should().StartWith(
        [
            "CA certificate file, or 'fetch' to get it from the controller, when a private CA such as the installer's issued the controller's certificate (Enter keeps none; 'none' removes it): ",
            "Is that the SHA-256 on the installer's Done page, or from 'hvo-roof-install cert show' on the controller? [y/N] "
        ]);
        result.Out.Should().Contain($"The controller serves the CA {AuthorityName}, SHA-256:{Environment.NewLine}  {RoofCertificateAuthority.Fingerprint(authority)}");
        if (answer == "y")
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            rig.Stored!.CaCertificate.Should().Be(RoofCertificateAuthority.ToPem(authority));
        }
        else
        {
            result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
            result.Error.Should().Contain("The CA was not saved: its SHA-256 was not confirmed.");
            File.Exists(rig.CredentialsPath).Should().BeFalse();
        }
    }

    // ---- setup: saving ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_TheCaReplacesAPin_APinReplacesTheCa_AndNoneRemovesIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        var file = WriteCa(rig, authority);

        var pinned = await rig.RunAsync("setup", "--controller", Plain.ToString(), "--certificate-sha256", Pin);
        var trusted = await rig.RunAsync("setup", "--ca-certificate", file);
        var trustedStored = rig.Stored!;
        var repinned = await rig.RunAsync("setup", "--certificate-sha256", Pin);
        var repinnedStored = rig.Stored!;
        await rig.RunAsync("setup", "--ca-certificate", file);
        var removed = await rig.RunAsync("setup", "--ca-certificate", "none");

        pinned.Code.Should().Be(RoofExitCode.Success, pinned.ToString());
        trusted.Code.Should().Be(RoofExitCode.Success, trusted.ToString());
        trusted.Out.Should().Contain($"{Plain}, CA {AuthorityName} trusted.")
            .And.Contain("The saved certificate pin was removed: the CA is trusted instead.");
        trustedStored.CaCertificate.Should().Be(RoofCertificateAuthority.ToPem(authority));
        trustedStored.CertificateSha256.Should().BeNull();
        repinned.Out.Should().Contain($"{Plain}, certificate pinned.")
            .And.Contain("The saved CA certificate was removed: the certificate is pinned instead.");
        repinnedStored.CaCertificate.Should().BeNull();
        repinnedStored.CertificateSha256.Should().Be(Pin);
        removed.Code.Should().Be(RoofExitCode.Success, removed.ToString());
        removed.Out.Should().Contain($"Saved in {rig.CredentialsPath}: {Plain}.");
        rig.Stored!.CaCertificate.Should().BeNull();
        rig.Stored.CertificateSha256.Should().BeNull();
    }

    [TestMethod]
    public async Task Setup_WithTheCa_AsJson_NamesTheCa()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        using var authority = TestCertificates.CreateAuthority(AuthorityName);

        var result = await rig.RunAsync("setup", "--json", "--controller", Plain.ToString(), "--ca-certificate", WriteCa(rig, authority));

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        using var json = JsonDocument.Parse(result.Out);
        json.RootElement.GetProperty("caCertificate").GetString().Should().Be(AuthorityName);
        json.RootElement.GetProperty("certificatePinned").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task Setup_WithAPinAndACa_IsAUsageError()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var authority = TestCertificates.CreateAuthority(AuthorityName);

        var result = await rig.RunAsync(
            "setup", "--controller", Plain.ToString(), "--certificate-sha256", Pin, "--ca-certificate", WriteCa(rig, authority));

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(
            "Give a certificate SHA-256 or a CA certificate, not both: the pin is for a self-signed certificate, the CA for one it issued.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("missing", "The CA certificate cannot be used: Could not find file *missing.pem*", DisplayName = "no file")]
    [DataRow("leaf", "The CA certificate cannot be used: *leaf.pem is not a CA certificate: its basic constraints do not say CA.", DisplayName = "the controller's own certificate")]
    [DataRow("text", "The CA certificate cannot be used: *text.pem does not hold a certificate in PEM or DER form.", DisplayName = "not a certificate")]
    public async Task Setup_WithACaCertificateThatCannotBeUsed_IsAUsageError(string kind, string expected)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        var file = Path.Combine(rig.Directory, $"{kind}.pem");
        if (kind == "leaf")
        {
            using var authority = TestCertificates.CreateAuthority(AuthorityName);
            using var issued = TestCertificates.Issue(authority);
            File.WriteAllText(file, issued.ExportCertificatePem());
        }
        else if (kind == "text")
        {
            File.WriteAllText(file, "not a certificate");
        }

        var result = await rig.RunAsync("setup", "--controller", Plain.ToString(), "--ca-certificate", file);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Match(expected + Environment.NewLine);
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task Setup_InATerminal_ForHttps_AsksForTheCa_AndThenNotForAPin()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        rig.Input.Enqueue(WriteCa(rig, authority));
        rig.Input.Enqueue(string.Empty);

        var result = await rig.RunAsync("setup", "--controller", "https://localhost/");

        rig.Prompts.Should().Equal(
            ("CA certificate file, or 'fetch' to get it from the controller, when a private CA such as the installer's issued the controller's certificate (Enter keeps none; 'none' removes it): ", false),
            ("API key (Enter to skip, and sign in later with 'hvo-roof login NAME'): ", true));
        result.Out.Should().Contain($"https://localhost/, CA {AuthorityName} trusted.");
        rig.Stored!.CaCertificate.Should().Be(RoofCertificateAuthority.ToPem(authority));
    }

    [TestMethod]
    public async Task Setup_InATerminal_EnterKeepsTheSavedCa()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        var pem = RoofCertificateAuthority.ToPem(authority);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = new Uri("https://localhost/"), CaCertificate = pem });
        rig.Input.Enqueue(string.Empty);
        rig.Input.Enqueue(string.Empty);
        rig.Input.Enqueue(string.Empty);

        await rig.RunAsync("setup");

        rig.Prompts.Select(prompt => prompt.Prompt).Should().Equal(
            "Controller address [https://localhost/]: ",
            "CA certificate file, or 'fetch' to get it from the controller, when a private CA such as the installer's issued the controller's certificate (Enter keeps the saved one; 'none' removes it): ",
            "API key (Enter to skip, and sign in later with 'hvo-roof login NAME'): ");
        rig.Stored!.CaCertificate.Should().Be(pem);
    }

    // ---- The credentials file and the environment ------------------------------------------------------------------

    [TestMethod]
    public async Task CredentialsFile_WithAPinAndACa_IsNotConfigured()
    {
        using var rig = new CliRig();
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials
        {
            Controller = Plain,
            CertificateSha256 = Pin,
            CaCertificate = RoofCertificateAuthority.ToPem(authority)
        });

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be(
            $"{rig.CredentialsPath} has both a certificate pin and a CA certificate. Save one with 'hvo-roof setup'." + Environment.NewLine);
    }

    [TestMethod]
    public async Task CredentialsFile_WithACaThatIsNotOne_IsNotConfigured()
    {
        using var rig = new CliRig();
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        using var issued = TestCertificates.Issue(authority);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = Plain, CaCertificate = issued.ExportCertificatePem() });

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be(
            $"The CA certificate in {rig.CredentialsPath} is not a CA's certificate in PEM form. Save it again with 'hvo-roof setup --ca-certificate FILE'."
            + Environment.NewLine);
    }

    [TestMethod]
    public void TheEnvironmentsPinOrCa_ReplacesTheFilesTogether()
    {
        using var rig = new CliRig();
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        var file = WriteCa(rig, authority);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = Plain, CaCertificate = RoofCertificateAuthority.ToPem(authority) });
        rig.Environment[RoofCredentialStore.CertificateVariable] = Pin;

        var pinned = rig.CreateContext().ResolveConnection();

        pinned.CertificateSha256.Should().Be(Pin);
        pinned.CaCertificate.Should().BeNull("the environment's pin replaces the file's CA, not joins it");

        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = Plain, CertificateSha256 = Pin });
        rig.Environment.Remove(RoofCredentialStore.CertificateVariable);
        rig.Environment[RoofCredentialStore.CaCertificateVariable] = file;

        var trusted = rig.CreateContext().ResolveConnection();

        trusted.CaCertificate!.RawData.Should().Equal(authority.RawData);
        trusted.CertificateSha256.Should().BeNull("the environment's CA replaces the file's pin, not joins it");
        trusted.Controller.Should().Be(Plain, "the other values still come from the file");
    }

    [TestMethod]
    public async Task Environment_WithAPinAndACa_IsNotConfigured()
    {
        using var rig = new CliRig();
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        rig.Environment[RoofCredentialStore.ControllerVariable] = Plain.ToString();
        rig.Environment[RoofCredentialStore.CertificateVariable] = Pin;
        rig.Environment[RoofCredentialStore.CaCertificateVariable] = WriteCa(rig, authority);

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be(
            "HVO_ROOF_CERT_SHA256 and HVO_ROOF_CA_CERT are both set. Set one: the pin of a self-signed certificate, or the CA that issued the certificate."
            + Environment.NewLine);
    }

    [TestMethod]
    public async Task Environment_WithACaFileThatCannotBeRead_IsNotConfigured()
    {
        using var rig = new CliRig();
        rig.Environment[RoofCredentialStore.ControllerVariable] = Plain.ToString();
        rig.Environment[RoofCredentialStore.CaCertificateVariable] = Path.Combine(rig.Directory, "missing.pem");

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().StartWith("HVO_ROOF_CA_CERT cannot be used: Could not find file ");
    }

    [TestMethod]
    public async Task SigningIn_KeepsTheSavedCa()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        using var authority = TestCertificates.CreateAuthority(AuthorityName);
        var pem = RoofCertificateAuthority.ToPem(authority);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, CaCertificate = pem });
        await RoofClientApiTests.AddUserAsync(host, "ada", RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("login", "ada");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Stored!.Session!.Name.Should().Be("ada");
        rig.Stored.CaCertificate.Should().Be(pem);
    }

    /// <summary>The CA's certificate alone (no key), as PEM, in the rig's directory; returns the file.</summary>
    private static string WriteCa(CliRig rig, X509Certificate2 authority)
    {
        var file = Path.Combine(rig.Directory, "ca.pem");
        File.WriteAllText(file, RoofCertificateAuthority.ToPem(authority));
        return file;
    }
}
