using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.RPi.Tests.Client;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The certificate's steps in the plan (#68), on fake machines: the CA, the file's password and the certificate made
/// with their modes, a second run that changes nothing, and each reason one is made or issued again (a new CA asked
/// for, a CA that does not limit its names or may not issue for one, expiry, the names changing, another CA, a person
/// asking), and the controller redeployed when it would serve another certificate or answer to other names.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCertificatePlanTests
{
    private static readonly ControllerLayout Layout = ControllerLayout.System;

    private static readonly string AuthorityUntil = AuthorityAssessment.Date((FakeMachine.Today + ControllerCertificates.AuthorityLifetime).UtcDateTime);

    [TestMethod]
    public async Task OnAFreshPi_TheCa_ThePassword_AndTheCertificate_AreMade_OwnerOnly()
    {
        using var pi = new FakeMachine().WithPi();
        var settings = new ControllerSettings();

        var plan = await CheckAsync(pi, settings);

        plan.Steps.Select(step => (step.Step.Target, step.Check.Change, step.Check.Detail)).Should().Equal(
            ("/etc/hvo-roof", StepChange.Create, "0755"),
            ("/etc/hvo-roof/secrets", StepChange.Create, "0700"),
            ("/etc/hvo-roof/https", StepChange.Create, "0700"),
            ("/etc/hvo-roof/ca", StepChange.Create, "0700"),
            (Layout.CaCertificate, StepChange.Create, "a new CA, which may issue only for names in local, localhost, roofpi, and private addresses"),
            (Layout.PfxPassword, StepChange.Create, "0600"),
            (Layout.Pfx, StepChange.Create, "for 3 names and 3 addresses, until 2027-11-02"));

        await ApplyAsync(pi, plan);

        pi.Mode(Layout.CaKey).Should().Be(Modes.PrivateFile);
        pi.Mode(Layout.CaCertificate).Should().Be(Modes.File);
        pi.Mode(Layout.PfxPassword).Should().Be(Modes.PrivateFile);
        pi.Mode(Layout.Pfx).Should().Be(Modes.PrivateFile);
        pi.Mode(Layout.Ca).Should().Be(Modes.PrivateFolder);
        pi.Mode(Layout.Secrets).Should().Be(Modes.PrivateFolder);
        pi.Mode(Layout.Https).Should().Be(Modes.PrivateFolder);
        pi.Read(Layout.CaKey).Should().StartWith("-----BEGIN PRIVATE KEY-----");
        pi.Read(Layout.PfxPassword).Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "no line break: the controller reads the whole file");

        using var authority = X509Certificate2.CreateFromPem(pi.Read(Layout.CaCertificate));
        using var certificate = Current(pi)!;
        ControllerCertificates.IsIssuedBy(certificate, authority).Should().BeTrue();
        NameConstraints.Of(authority)!.DnsNames.Should().Equal("local", "localhost", "roofpi");
        ControllerCertificates.NamesIn(certificate).DnsNames.Should().Equal("roofpi", "roofpi.local", "localhost");
        ControllerCertificates.NamesIn(certificate).Addresses.Select(address => address.ToString()).Should().Equal("192.168.1.50", "127.0.0.1", "::1");
        using var inFile = X509CertificateLoader.LoadPkcs12Collection(File.ReadAllBytes(pi.OnDisk(Layout.Pfx)), pi.Read(Layout.PfxPassword), ControllerCertificates.KeyStorage).Single(link => !link.HasPrivateKey);
        inFile.RawData.Should().Equal(authority.RawData, "the file carries the CA's certificate, never its key");

        var before = pi.Snapshot();
        var again = await CheckAsync(pi, settings);

        again.HasChanges.Should().BeFalse(string.Join('\n', PlanText.Lines(again)));
        Change(again, Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Unchanged, $"kept: HVO Roof CA (roofpi, 2026-10-01), until {AuthorityUntil}"));
        Change(again, Layout.PfxPassword).Should().Be(new StepCheck(StepChange.Unchanged, "0600"));
        Change(again, Layout.Pfx).Should().Be(new StepCheck(StepChange.Unchanged, "kept: roofpi, until 2027-11-02"));
        await ApplyAsync(pi, again);
        pi.Snapshot().Should().Equal(before, "a second run changes nothing");
    }

    [TestMethod]
    public async Task ANewCa_AsAPersonAsks_IsMade_AndTheCertificateIssuedAgainFromIt()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var old = Fingerprint(pi);

        var plan = await CheckAsync(pi, new ControllerSettings(), replacing: old);

        Change(plan, Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Change, "a person asked for a new one (--new-ca): a new CA, which every client must trust in place of this one"));
        Change(plan, Layout.PfxPassword).Change.Should().Be(StepChange.Unchanged);
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "issued again by the new CA"));
        await ApplyAsync(pi, plan);

        Fingerprint(pi).Should().NotBe(old);
        using var authority = X509Certificate2.CreateFromPem(pi.Read(Layout.CaCertificate));
        using var certificate = Current(pi)!;
        ControllerCertificates.IsIssuedBy(certificate, authority).Should().BeTrue();
        (await CheckAsync(pi, new ControllerSettings(), replacing: old)).HasChanges.Should().BeFalse("the new CA is kept: only the one asked about is replaced");
    }

    [TestMethod]
    public async Task ACaThatDoesNotLimitItsNames_IsMadeAgain()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        using var unconstrained = TestCertificates.CreateAuthority("An old CA");
        WriteAuthority(pi, unconstrained);

        var plan = await CheckAsync(pi, new ControllerSettings());

        Change(plan, Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Change, "it does not limit the names it may issue for: a new CA, which every client must trust in place of this one"));
        Change(plan, Layout.Pfx).Detail.Should().Be("issued again by the new CA");
    }

    [TestMethod]
    public async Task ACaThatMayNotIssueForANewName_IsMadeAgain_ForTheNewNames()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var settings = new ControllerSettings { HostNames = ["roof"], Domains = ["observatory.example"] };

        var plan = await CheckAsync(pi, settings);

        Change(plan, Layout.CaCertificate).Should().Be(new StepCheck(
            StepChange.Change,
            "it may not issue for roofpi.observatory.example, roof, roof.observatory.example: a new CA, which every client must trust in place of this one"));
        await ApplyAsync(pi, plan);

        using var authority = X509Certificate2.CreateFromPem(pi.Read(Layout.CaCertificate));
        NameConstraints.Of(authority)!.DnsNames.Should().Equal("local", "localhost", "roofpi", "roof", "observatory.example");
        using var certificate = Current(pi)!;
        ControllerCertificates.NamesIn(certificate).DnsNames.Should().Contain(["roof.local", "roof.observatory.example", "roofpi.observatory.example"]);
    }

    [TestMethod]
    public async Task ACaExpiringWithin400Days_IsMadeAgain()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();

        var plan = await CheckAsync(pi, new ControllerSettings(), time: new FixedClock(FakeMachine.Today.AddYears(9)));

        Change(plan, Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Change, $"it expires on {AuthorityUntil}: a new CA, which every client must trust in place of this one"));
        Change(plan, Layout.Pfx).Detail.Should().Be("issued again by the new CA");
    }

    [TestMethod]
    public async Task TheRenewalWindows_AreMeasuredInUtc_InAnyTimeZone()
    {
        // X509Certificate2 gives its dates in local time. An hour either side of each window tells them apart only when
        // both sides are compared in UTC; on a machine whose zone is not UTC, a local date read as UTC moves the window.
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var certificateExpires = FakeMachine.Today + ControllerCertificates.IssuedLifetime;
        var authorityExpires = FakeMachine.Today + ControllerCertificates.AuthorityLifetime;
        var hour = TimeSpan.FromHours(1);
        async Task<StepChange> At(DateTimeOffset now, string target)
            => Change(await CheckAsync(pi, new ControllerSettings(), time: new FixedClock(now)), target).Change;

        (await At(certificateExpires - ControllerCertificates.RenewWithin - hour, Layout.Pfx)).Should().Be(StepChange.Unchanged);
        (await At(certificateExpires - ControllerCertificates.RenewWithin + hour, Layout.Pfx)).Should().Be(StepChange.Change);
        (await At(authorityExpires - ControllerCertificates.RenewAuthorityWithin - hour, Layout.CaCertificate)).Should().Be(StepChange.Unchanged);
        (await At(authorityExpires - ControllerCertificates.RenewAuthorityWithin + hour, Layout.CaCertificate)).Should().Be(StepChange.Change);
    }

    [TestMethod]
    public async Task ACertificateExpiringWithin30Days_IsIssuedAgain_UnderTheSameCa()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var authority = Fingerprint(pi);
        var later = new FixedClock(FakeMachine.Today.AddDays(370));

        var plan = await CheckAsync(pi, new ControllerSettings(), time: later);

        Change(plan, Layout.CaCertificate).Change.Should().Be(StepChange.Unchanged);
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "it expires on 2027-11-02: issued again"));
        await ApplyAsync(pi, plan, later);

        Fingerprint(pi).Should().Be(authority, "clients go on trusting the same CA");
        using var certificate = Current(pi)!;
        AuthorityAssessment.Date(certificate.NotAfter).Should().Be("2028-11-06");
        (await CheckAsync(pi, new ControllerSettings(), time: later)).HasChanges.Should().BeFalse();
    }

    [TestMethod]
    public async Task Renew_IssuesItAgain_ThoughItIsStillGood()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        using var before = Current(pi)!;

        var plan = await CheckAsync(pi, new ControllerSettings(), renew: true);

        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "issued again, as asked (--renew): it was valid until 2027-11-02"));
        Change(plan, Layout.CaCertificate).Change.Should().Be(StepChange.Unchanged);
        await ApplyAsync(pi, plan);
        using var after = Current(pi)!;
        after.SerialNumber.Should().NotBe(before.SerialNumber);
    }

    [TestMethod]
    public async Task WhenTheMachinesAddressesChange_TheCertificateIsIssuedAgain()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        pi.Addresses[0] = new NetworkAddress("eth0", IPAddress.Parse("10.0.0.5"));

        var plan = await CheckAsync(pi, new ControllerSettings());

        Change(plan, Layout.CaCertificate).Change.Should().Be(StepChange.Unchanged, "the CA may issue for any private address");
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "the names changed (adds 10.0.0.5; drops 192.168.1.50): issued again"));
    }

    [TestMethod]
    public async Task ACertificateAnotherCaIssued_IsIssuedAgain_ByThisMachinesCa()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var names = CertificateNames.For(pi.Machine, new ControllerSettings());
        using var other = ControllerCertificates.CreateAuthority(names, FakeMachine.Today);
        using var fromOther = ControllerCertificates.Issue(other, names, FakeMachine.Today);
        File.WriteAllBytes(pi.OnDisk(Layout.Pfx), ControllerCertificates.ExportPfx(fromOther, other, pi.Read(Layout.PfxPassword)));

        var plan = await CheckAsync(pi, new ControllerSettings());

        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "another CA issued it: issued again by this machine's CA"));
    }

    [TestMethod]
    public async Task APasswordThatIsEmpty_OrDoesNotOpenIt_IsReplaced()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        pi.Write(Layout.PfxPassword, string.Empty);

        var empty = await CheckAsync(pi, new ControllerSettings());

        Change(empty, Layout.PfxPassword).Should().Be(new StepCheck(StepChange.Change, "it is empty: a new password"));
        Change(empty, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "issued again, with a new password"));
        await ApplyAsync(pi, empty);
        using (var certificate = Current(pi))
        {
            certificate.Should().NotBeNull("the new password opens the new file");
        }

        pi.Write(Layout.PfxPassword, "not-the-password-not-a-secret");
        File.SetUnixFileMode(pi.OnDisk(Layout.PfxPassword), Modes.PrivateFile);
        Change(await CheckAsync(pi, new ControllerSettings()), Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "its password does not open it: issued again"));
    }

    [TestMethod]
    public async Task FilesWithTheWrongModes_AreSetRight_AndKept()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        File.SetUnixFileMode(pi.OnDisk(Layout.CaKey), Modes.File);
        File.SetUnixFileMode(pi.OnDisk(Layout.PfxPassword), Modes.File);
        File.SetUnixFileMode(pi.OnDisk(Layout.Pfx), Modes.File);
        var contents = new[] { Layout.CaKey, Layout.CaCertificate, Layout.PfxPassword, Layout.Pfx }.Select(path => File.ReadAllBytes(pi.OnDisk(path))).ToArray();

        var plan = await CheckAsync(pi, new ControllerSettings());

        Change(plan, Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Change, "its key: 0644 → 0600"));
        Change(plan, Layout.PfxPassword).Should().Be(new StepCheck(StepChange.Change, "0644 → 0600"));
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "0644 → 0600"));
        await ApplyAsync(pi, plan);

        pi.Mode(Layout.CaKey).Should().Be(Modes.PrivateFile);
        pi.Mode(Layout.PfxPassword).Should().Be(Modes.PrivateFile);
        pi.Mode(Layout.Pfx).Should().Be(Modes.PrivateFile);
        new[] { Layout.CaKey, Layout.CaCertificate, Layout.PfxPassword, Layout.Pfx }.Select(path => File.ReadAllBytes(pi.OnDisk(path))).Should().BeEquivalentTo(contents, options => options.WithStrictOrdering(), "only the modes change");

        File.SetUnixFileMode(pi.OnDisk(Layout.CaCertificate), Modes.PrivateFile);
        Change(await CheckAsync(pi, new ControllerSettings()), Layout.CaCertificate).Should().Be(new StepCheck(StepChange.Change, "0600 → 0644"));
    }

    [TestMethod]
    public async Task ASelfSignedCertificate_HasNoCa_AndReplacesOneFromTheCa()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates();
        var settings = new ControllerSettings { Connection = ConnectionMode.SelfSigned };

        var plan = await CheckAsync(pi, settings);

        plan.Steps.Select(step => step.Step.Target).Should().NotContain([Layout.Ca, Layout.CaCertificate]);
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "it is not self-signed: a self-signed one in its place"));
        plan.Steps.Single(step => step.Step.Target == Layout.Pfx).Step.Purpose.Should().Be("the controller's self-signed certificate, for roofpi");
        await ApplyAsync(pi, plan);

        using var certificate = Current(pi)!;
        ControllerCertificates.IsSelfSigned(certificate).Should().BeTrue();
        AuthorityAssessment.Date(certificate.NotAfter).Should().Be("2029-01-03", "825 days, as long as Apple's platforms accept");
        (await CheckAsync(pi, settings)).HasChanges.Should().BeFalse();
    }

    [TestMethod]
    public async Task FromSelfSigned_ToThePrivateCa_TheCaIsMade_AndTheCertificateIssuedFromIt()
    {
        using var pi = new FakeMachine().WithPi().WithCertificates(new ControllerSettings { Connection = ConnectionMode.SelfSigned });

        var plan = await CheckAsync(pi, new ControllerSettings());

        Change(plan, Layout.CaCertificate).Change.Should().Be(StepChange.Create);
        Change(plan, Layout.Pfx).Should().Be(new StepCheck(StepChange.Change, "issued again by the new CA"));
    }

    [TestMethod]
    public async Task YourOwnCertificate_IsKept_OrBlockedWhenThereIsNone()
    {
        var own = new ControllerSettings { Connection = ConnectionMode.OwnCertificate };
        using var fresh = new FakeMachine().WithPi();
        using var pi = new FakeMachine().WithPi().WithCertificates(own);

        var none = await CheckAsync(fresh, own);
        var kept = await CheckAsync(pi, own);

        none.IsBlocked.Should().BeTrue();
        Change(none, Layout.Pfx).Detail.Should().Be("there is none yet: give yours with hvo-roof-install cert import FILE, or choose private-ca");
        none.Steps.Select(step => step.Step.Target).Should().NotContain([Layout.CaCertificate, Layout.PfxPassword], "the password comes with the certificate it opens");
        Change(kept, Layout.Pfx).Should().Be(new StepCheck(StepChange.Unchanged, "yours, kept: roofpi, until 2029-01-03"));
        kept.Steps.Single(step => step.Step.Target == Layout.Pfx).Step.Purpose.Should().Be("your certificate for the controller");

        pi.Write(Layout.PfxPassword, "not-the-password-not-a-secret");
        Change(await CheckAsync(pi, own), Layout.Pfx).Should().Be(new StepCheck(
            StepChange.Blocked,
            "the password in /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password does not open it: give it again with hvo-roof-install cert import FILE"));
    }

    [TestMethod]
    public async Task OverHttp_ThereIsNoCertificate()
    {
        using var pi = new FakeMachine().WithPi();

        var plan = await CheckAsync(pi, new ControllerSettings { Connection = ConnectionMode.Http });

        plan.Steps.Select(step => step.Step.Kind).Should().OnlyContain(kind => kind == StepKind.Folder);
    }

    [TestMethod]
    public async Task TheInstallPlan_KeepsTheAdoptedController_WhileItsCertificateAndNamesAreAsTheyShouldBe()
    {
        using var pi = InstallerPlanTests.AdoptablePi();

        var plan = await BuildAsync(pi);

        plan.HasChanges.Should().BeTrue("the record is new");
        Change(plan, MachineSurveyor.ControllerContainer).Change.Should().Be(StepChange.Unchanged);
        plan.Steps.Where(step => step.Step is CertificateAuthorityStep or CertificatePasswordStep or CertificateStep)
            .Should().HaveCount(3).And.OnlyContain(step => step.Check.Change == StepChange.Unchanged);
        Change(plan, "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key").Should().Be(StepCheck.Unchanged("reuses roof-operator (RoofOperator)"));
    }

    [TestMethod]
    public async Task TheController_IsRedeployed_ToServeANewCertificate()
    {
        using var pi = InstallerPlanTests.AdoptablePi();

        var plan = await BuildAsync(pi, new FixedClock(FakeMachine.Today.AddDays(370)));

        Change(plan, Layout.Pfx).Change.Should().Be(StepChange.Change);
        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed with the new certificate"));
    }

    [TestMethod]
    public async Task TheController_IsRedeployed_WhenItServesAnotherCertificate()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.ServedCertificates[8443].Dispose();
        pi.ServedCertificates[8443] = ControllerCertificates.SelfSigned(CertificateNames.For(pi.Machine, new ControllerSettings()), FakeMachine.Today);

        var plan = await BuildAsync(pi);

        Change(plan, Layout.Pfx).Change.Should().Be(StepChange.Unchanged);
        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed: it serves another certificate than /etc/hvo-roof/https/roof-controller.pfx"));
    }

    [TestMethod]
    public async Task TheController_IsRedeployed_ToAnswerOnlyToItsNames()
    {
        using var any = InstallerPlanTests.AdoptablePi();
        any.Containers[MachineSurveyor.ControllerContainer] = any.Containers[MachineSurveyor.ControllerContainer] with { AllowedHosts = null };
        using var moved = InstallerPlanTests.AdoptablePi();
        moved.Addresses[0] = new NetworkAddress("eth0", IPAddress.Parse("192.168.1.60"));

        Change(await BuildAsync(any), MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed to answer only to its names (it answers to any)"));
        Change(await BuildAsync(moved), MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed to answer to its names as they are now"));
    }

    [TestMethod]
    public async Task AllowedHosts_AreComparedAsASet()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var hosts = CertificateNames.For(pi.Machine, new ControllerSettings()).AllowedHosts.Split(';').Reverse().Select(host => host.ToUpperInvariant());
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { AllowedHosts = string.Join(" ; ", hosts) };

        Change(await BuildAsync(pi), MachineSurveyor.ControllerContainer).Change.Should().Be(StepChange.Unchanged);
    }

    private static async Task<InstallContext> ContextAsync(FakeMachine machine, TimeProvider? time = null)
    {
        var session = await InstallerSession.StartAsync(machine.Machine, InstallLog.None, "4.0.0+0123456789abcdef", time ?? FakeMachine.Clock);
        return session.Context;
    }

    private static async Task<CheckedPlan> CheckAsync(FakeMachine machine, ControllerSettings settings, string? replacing = null, bool renew = false, TimeProvider? time = null)
        => await PlanBuilder.BuildCertificate(machine.Machine, settings, replacing, renew).CheckAsync(await ContextAsync(machine, time), CancellationToken.None);

    private static async Task<CheckedPlan> BuildAsync(FakeMachine machine, TimeProvider? time = null)
    {
        var session = await InstallerSession.StartAsync(machine.Machine, InstallLog.None, "4.0.0+0123456789abcdef", time ?? FakeMachine.Clock);
        session.Answers = new InstallAnswers { Roles = [InstallRole.Controller] }.Normalised();
        return await session.CheckAsync();
    }

    private static async Task ApplyAsync(FakeMachine machine, CheckedPlan plan, TimeProvider? time = null)
        => await plan.ApplyAsync(await ContextAsync(machine, time));

    private static StepCheck Change(CheckedPlan plan, string target) => plan.Steps.Single(step => step.Step.Target == target).Check;

    private static X509Certificate2? Current(FakeMachine machine)
        => ControllerCertificates.LoadPfx(File.ReadAllBytes(machine.OnDisk(Layout.Pfx)), machine.Read(Layout.PfxPassword));

    private static string Fingerprint(FakeMachine machine)
    {
        using var authority = X509Certificate2.CreateFromPem(machine.Read(Layout.CaCertificate));
        return ControllerCertificates.Fingerprint(authority);
    }

    private static void WriteAuthority(FakeMachine machine, X509Certificate2 authority)
    {
        using var key = authority.GetECDsaPrivateKey()!;
        machine.Machine.WriteAtomically(Layout.CaKey, key.ExportPkcs8PrivateKeyPem(), Modes.PrivateFile);
        machine.Machine.WriteAtomically(Layout.CaCertificate, authority.ExportCertificatePem() + "\n", Modes.File);
    }
}
