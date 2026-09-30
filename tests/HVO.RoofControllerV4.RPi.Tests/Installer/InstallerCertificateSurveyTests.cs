using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.RPi.Tests.Client;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// What the installer finds of the controller's certificate and CA (#68), the warnings every run gives about them, the
/// confirmation plain HTTP needs, and the controller's choices when nothing is recorded.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCertificateSurveyTests
{
    private static readonly ControllerLayout Layout = ControllerLayout.System;

    private const string Password = "/etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password";

    [TestMethod]
    public async Task TheSurvey_FindsTheCertificate_AndTheCaThatIssuedIt()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Certificate!.Subject.Should().Be("roofpi");
        survey.Certificate.Issuer.Should().Be("HVO Roof CA (roofpi, 2026-10-01)");
        survey.Certificate.FromAuthority.Should().BeTrue();
        survey.Certificate.Names.Should().Equal("roofpi", "roofpi.local", "localhost", "192.168.1.50", "127.0.0.1", "::1");
        survey.Certificate.Uncovered.Should().BeEmpty();
        survey.Certificate.Fingerprint.Should().MatchRegex("^([0-9A-F]{2}:){31}[0-9A-F]{2}$");
        survey.Authority!.Subject.Should().Be("HVO Roof CA (roofpi, 2026-10-01)");
        survey.Authority.Permits.Should().StartWith(["local", "localhost", "roofpi"]).And.Contain("192.168.0.0/16").And.Contain("127.0.0.0/8");
        InstallerSession.DescribeSurvey(survey, FakeMachine.Today).Should().Contain([
            "Certificate: roofpi, until 2027-11-02, issued by HVO Roof CA (roofpi, 2026-10-01)",
            "CA:          HVO Roof CA (roofpi, 2026-10-01), until 2036-09-28"]);
        InstallerSession.CertificateWarnings(survey, FakeMachine.Today).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ASelfSignedCertificate_IsSaidToBe()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates(new ControllerSettings { Connection = ConnectionMode.SelfSigned });

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Certificate!.Issuer.Should().BeNull();
        survey.Authority.Should().BeNull();
        InstallerSession.DescribeSurvey(survey, FakeMachine.Today).Should().Contain("Certificate: roofpi, until 2029-01-03, self-signed");
    }

    [TestMethod]
    public async Task EveryRun_WarnsBeforeTheCertificateExpires_AndAfter()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        InstallerSession.CertificateWarnings(survey, FakeMachine.Today.AddDays(366)).Should().BeEmpty("31 days are left");
        InstallerSession.CertificateWarnings(survey, FakeMachine.Today.AddDays(370)).Should().Equal(
            "The certificate expires on 2027-11-02, in 27 days. Renew it with: sudo hvo-roof-install cert");
        InstallerSession.CertificateWarnings(survey, FakeMachine.Today.AddDays(400)).Should().Equal(
            "The certificate expired on 2027-11-02: clients refuse it. Renew it with: sudo hvo-roof-install cert");
        InstallerSession.DescribeSurvey(survey, FakeMachine.Today.AddDays(370)).Should().Contain(
            "             The certificate expires on 2027-11-02, in 27 days. Renew it with: sudo hvo-roof-install cert");
    }

    [TestMethod]
    public async Task YourOwnCertificate_IsRenewedByImportingANewOne()
    {
        using var pi = new FakeMachine().WithPi();
        using var theirs = WriteTheirs(pi, notAfter: FakeMachine.Today.AddDays(20));

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Certificate!.FromAuthority.Should().BeFalse();
        survey.Certificate.Issuer.Should().Be("Their CA");
        InstallerSession.CertificateWarnings(survey, FakeMachine.Today).Should().Equal(
            "The certificate expires on 2026-10-21, in 20 days. Put a new one in place with: sudo hvo-roof-install cert import FILE");
    }

    [TestMethod]
    public async Task ACertificateNotForANameClientsUse_IsWarnedOf()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        pi.Addresses[0] = new NetworkAddress("eth0", IPAddress.Parse("10.0.0.5"));

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Certificate!.Uncovered.Should().Equal("10.0.0.5");
        InstallerSession.CertificateWarnings(survey, FakeMachine.Today).Should().Equal("The certificate is not for 10.0.0.5: a client that uses it refuses it.");
    }

    [TestMethod]
    public async Task ACertificateItsPasswordDoesNotOpen_CannotBeServed()
    {
        using var wrong = new FakeMachine().WithPi().WithCertificates();
        wrong.Write(Layout.PfxPassword, "not-the-password-not-a-secret");
        using var missing = new FakeMachine().WithPi().WithCertificates();
        File.Delete(missing.OnDisk(Layout.PfxPassword));

        var wrongSurvey = await MachineSurveyor.SurveyAsync(wrong.Machine);
        var missingSurvey = await MachineSurveyor.SurveyAsync(missing.Machine);

        InstallerSession.CertificateWarnings(wrongSurvey, FakeMachine.Today).Should().Equal($"The controller cannot serve its certificate: the password in {Password} does not open it.");
        InstallerSession.DescribeSurvey(wrongSurvey, FakeMachine.Today).Should().Contain($"Certificate: {Layout.Pfx}: the password in {Password} does not open it");
        InstallerSession.CertificateWarnings(missingSurvey, FakeMachine.Today).Should().Equal($"The controller cannot serve its certificate: there is no password for it in {Password}.");
    }

    [TestMethod]
    public async Task WithoutRoot_TheCertificateIsNotRead_AndNothingIsWarnedOf()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("root reads a file whatever its mode");
        }

        using var pi = new FakeMachine(root: false).WithPi().WithCertificates();
        File.SetUnixFileMode(pi.OnDisk(Layout.Pfx), UnixFileMode.None);
        try
        {
            var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

            survey.Certificate.Should().Be(new CertificateSurvey { Path = Layout.Pfx, Problem = "only root can read it", NeedsRoot = true });
            InstallerSession.CertificateWarnings(survey, FakeMachine.Today.AddDays(400)).Should().BeEmpty();
            InstallerSession.DescribeSurvey(survey, FakeMachine.Today).Should().Contain($"Certificate: {Layout.Pfx}: only root can read it");
        }
        finally
        {
            File.SetUnixFileMode(pi.OnDisk(Layout.Pfx), Modes.PrivateFile);
        }
    }

    [TestMethod]
    public async Task EveryRun_WarnsBeforeTheCaExpires()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        InstallerSession.CertificateWarnings(null, survey.Authority, FakeMachine.Today.AddDays(3200)).Should().BeEmpty("450 days are left");
        InstallerSession.CertificateWarnings(null, survey.Authority, FakeMachine.Today.AddDays(3300)).Should().Equal(
            "The CA expires on 2036-09-28: the installer makes a new one, which every client must then trust.");
        InstallerSession.CertificateWarnings(null, survey.Authority, FakeMachine.Today.AddDays(3700)).Should().Equal(
            "The CA expired on 2036-09-28: the installer makes a new one, which every client must then trust.");
    }

    [TestMethod]
    public async Task PlainHttp_NeedsAPersonToTypeHttp_UnlessItIsAlreadyServed()
    {
        using var pi = new FakeMachine().WithPi();
        using var served = new FakeMachine().WithPi();
        served.Write(InstallPaths.SystemRecord, (InstallerGuardTests.Record(InstallRole.Controller, null) with { Controller = Http }).ToJson());
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);
        var answers = new InstallAnswers { Roles = [InstallRole.Controller], Controller = Http };

        RoleGuards.NeedsHttpConfirmation(survey, answers).Should().BeTrue();
        RoleGuards.NeedsHttpConfirmation(await MachineSurveyor.SurveyAsync(served.Machine), answers).Should().BeFalse("it already serves HTTP");
        RoleGuards.NeedsHttpConfirmation(survey, answers with { Controller = new ControllerSettings() }).Should().BeFalse();
        RoleGuards.NeedsHttpConfirmation(survey, new InstallAnswers { Roles = [InstallRole.Cli] }).Should().BeFalse();

        RoleGuards.Check(survey, answers).Should().Contain(
            "Confirm plain HTTP: API keys, session tokens and PINs would cross the network unencrypted. Type http to confirm (httpConfirmation in an answers file), or choose private-ca.");
        RoleGuards.Check(survey, answers with { HttpConfirmation = "https" }).Should().Contain(
            "The confirmation does not match: type http to serve plain HTTP, or choose private-ca.");
        RoleGuards.Check(survey, answers with { HttpConfirmation = " HTTP " }).Should().NotContain(problem => problem.Contains("HTTP", StringComparison.OrdinalIgnoreCase));
        RoleGuards.Check(survey, answers, planOnly: true).Should().NotContain(problem => problem.Contains("HTTP", StringComparison.OrdinalIgnoreCase), "--plan changes nothing");
    }

    [TestMethod]
    public void TheHttpConfirmation_IsNeverSaved_AndOnlyKeptForHttp()
    {
        var answers = new InstallAnswers { Roles = [InstallRole.Controller], Controller = Http, HttpConfirmation = "http" };

        answers.Normalised().HttpConfirmation.Should().Be("http");
        (answers with { Controller = new ControllerSettings() }).Normalised().HttpConfirmation.Should().BeNull();
        answers.ToJson().Should().NotContain("httpConfirmation");
        InstallAnswers.Parse("""{ "roles": ["controller"], "controller": { "connection": "http" }, "httpConfirmation": "http" }""").HttpConfirmation.Should().Be("http");
    }

    [TestMethod]
    public async Task AnAnswersFile_ForPlainHttp_IsRefusedWithoutTheConfirmation()
    {
        using var pi = HttpPi();
        var without = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller], Controller = Http }, "without.json");
        var withIt = Path.Join(pi.Home, "with.json");
        pi.Write(withIt, """{ "roles": ["controller"], "controller": { "connection": "http" }, "httpConfirmation": "http" }""");

        var refused = await pi.RunAsync("--answers", without);
        var confirmed = await pi.RunAsync("--answers", withIt);

        refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("Confirm plain HTTP:");
        confirmed.ExitCode.Should().Be(0, confirmed.ToString());
        confirmed.Output.Should().Contain("The controller's API:  http://roofpi.local:8080/");
        pi.Read(InstallPaths.SystemRecord).Should().NotContain("httpConfirmation");
    }

    [TestMethod]
    public async Task WithNothingRecorded_TheControllerKeepsYourOwnCertificate_OrGetsAPrivateCa()
    {
        using var fresh = new FakeMachine(hostName: "roofpi").Write(CertificateNames.ResolverConfiguration, "search observatory.example\n");
        fresh.WithPi();
        using var own = new FakeMachine().WithPi();
        using var theirs = WriteTheirs(own, FakeMachine.Today.AddDays(90));
        using var selfSigned = new FakeMachine().WithPi().WithCertificates(new ControllerSettings { Connection = ConnectionMode.SelfSigned });
        using var unreadable = new FakeMachine().WithPi();
        using (WriteTheirs(unreadable, FakeMachine.Today.AddDays(90)))
        {
            unreadable.Write(Layout.PfxPassword, "not-the-password-not-a-secret");
        }

        var defaults = (await InstallerPlanTests.StartAsync(fresh, InstallRole.Controller)).DefaultController;

        defaults.Connection.Should().Be(ConnectionMode.PrivateCa);
        defaults.Domains.Should().Equal("observatory.example");
        (await InstallerPlanTests.StartAsync(own, InstallRole.Controller)).DefaultController.Connection.Should().Be(ConnectionMode.OwnCertificate, "a certificate another CA issued is never replaced unasked");
        (await InstallerPlanTests.StartAsync(selfSigned, InstallRole.Controller)).DefaultController.Connection.Should().Be(ConnectionMode.PrivateCa);
        (await InstallerPlanTests.StartAsync(unreadable, InstallRole.Controller)).DefaultController.Connection.Should().Be(ConnectionMode.PrivateCa, "one the controller cannot serve is not kept");
    }

    private static ControllerSettings Http => new() { Connection = ConnectionMode.Http };

    // A controller serving plain HTTP, set up by the deploy script: the installer can adopt it.
    private static FakeMachine HttpPi()
    {
        var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = false, Https = false }).WithCertificates(Http);
        pi.PortsInUse.UnionWith([8080, 8088]);
        return pi;
    }

    // "Their CA"'s certificate for roofpi's names, in the controller's file with its password; returns the CA.
    private static X509Certificate2 WriteTheirs(FakeMachine machine, DateTimeOffset notAfter)
    {
        var authority = TestCertificates.CreateAuthority("Their CA", notBefore: FakeMachine.Today.AddDays(-30), notAfter: FakeMachine.Today.AddYears(5));
        using var certificate = TestCertificates.Issue(
            authority,
            ["roofpi", "roofpi.local", "localhost", "192.168.1.50", "127.0.0.1", "::1"],
            FakeMachine.Today.AddDays(-1),
            notAfter);
        var password = ControllerCertificates.NewPassword();
        machine.Machine.CreateDirectory(Layout.Configuration, Modes.Folder);
        machine.Machine.CreateDirectory(Layout.Secrets, Modes.PrivateFolder);
        machine.Machine.CreateDirectory(Layout.Https, Modes.PrivateFolder);
        machine.Machine.WriteAtomically(Layout.PfxPassword, password, Modes.PrivateFile);
        machine.Machine.WriteAtomically(Layout.Pfx, ControllerCertificates.ExportPfx(certificate, authority, password), Modes.PrivateFile);
        return authority;
    }
}
