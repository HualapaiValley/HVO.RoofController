using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// <c>hvo-roof-install cert</c> and <c>cert import</c> redeploying the controller the installer deployed, to serve the new
/// certificate (#69): only while the roof is idle, only when the certificate is all that would change, and only once the
/// person agrees or gave <c>--redeploy</c>. Otherwise the certificate is in place and it says how to finish.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCertificateRedeployTests
{
    private const string Question = "Redeploy the controller now?";
    private const string Retry = "When the roof is idle, run sudo hvo-roof-install cert --redeploy.";
    private const string Moving = "{\"isMoving\":true,\"commandedMotion\":\"Opening\"}";

    // Not a secret: the password of the test's own certificate file.
    private const string TheirPassword = "their-pfx-password-not-a-secret";

    private static readonly ControllerLayout Layout = ControllerLayout.System;

    [TestMethod]
    public async Task Renew_WhenTheRoofIsIdle_AndThePersonAgrees_RedeploysTheControllerToServeIt()
    {
        using var pi = await InstalledAsync();
        ServesTheCertificateInItsFile(pi).Should().BeTrue("the install deployed it with its certificate");
        pi.Replies = question => question == Question;

        var run = await pi.RunAsync("cert", "--renew");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Questions.Should().Equal(Question);
        run.Output.Should().Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.")
            .And.Contain("To serve it, the controller must be deployed again (redeployed with the new certificate). The deploy script stops the roof with a verified Stop before it replaces the controller")
            .And.Contain("Changing container roof-controller: done.")
            .And.EndWith("The controller serves the new certificate.\n");
        pi.Deploys.Should().HaveCount(2);
        ServesTheCertificateInItsFile(pi).Should().BeTrue();
        pi.Read(InstallPaths.SystemLog).Should().Contain("Redeployed roof-controller to serve the new certificate.");

        var again = await pi.RunAsync("cert");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().EndWith("Nothing to change: the controller's certificate is in place as it should be.\n");
        pi.Questions.Should().ContainSingle("it serves the certificate in place, so there is nothing to ask");
        pi.Deploys.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Renew_WhileTheRoofMoves_PutsTheCertificateInPlace_AndRedeploysOnlyOnceItIsIdle()
    {
        using var pi = await InstalledAsync();
        var idle = pi.StatusJson;
        pi.StatusJson = Moving;
        pi.Replies = _ => true;

        var run = await pi.RunAsync("cert", "--renew");

        run.ExitCode.Should().Be(0, "the certificate is in place: only the redeploy waits");
        run.Output.Should().Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.")
            .And.Contain("The controller is not redeployed to serve the new certificate: the roof is moving (Opening), and the deploy script stops it before it replaces the controller")
            .And.EndWith("To serve it, run sudo hvo-roof-install cert --redeploy once the roof is idle.\n");
        pi.Questions.Should().BeEmpty("nothing is asked while the roof moves");
        pi.Deploys.Should().ContainSingle();
        pi.Ran.Should().NotContain(command => command.Arguments.Contains("stop"), "nothing is stopped");

        var insisted = await pi.RunAsync("cert", "--redeploy");

        insisted.ExitCode.Should().Be((int)InstallerExitCode.Refused, "it was asked to redeploy and could not");
        insisted.Output.Should().Contain("the roof is moving (Opening)");
        pi.Deploys.Should().ContainSingle();

        pi.StatusJson = idle;
        var redeployed = await pi.RunAsync("cert", "--redeploy");

        redeployed.ExitCode.Should().Be(0, redeployed.ToString());
        redeployed.Output.Should().Contain("Nothing to change: the controller's certificate is in place as it should be.")
            .And.Contain("To serve it, the controller must be deployed again (redeployed: it serves another certificate than /etc/hvo-roof/https/roof-controller.pfx).")
            .And.EndWith("The controller serves the new certificate.\n");
        pi.Questions.Should().BeEmpty("--redeploy does not ask");
        pi.Deploys.Should().HaveCount(2);
        ServesTheCertificateInItsFile(pi).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(null, null, "No one at a terminal could agree to it, so the controller is not redeployed. " + Retry, DisplayName = "No terminal")]
    [DataRow(false, null, "The controller is not redeployed: it serves the new certificate once it is. " + Retry, DisplayName = "Declined")]
    [DataRow(true, "--no-redeploy", "The controller is not redeployed (--no-redeploy): it serves the new certificate once it is. " + Retry, DisplayName = "--no-redeploy")]
    public async Task Renew_WithoutAgreement_PutsTheCertificateInPlace_AndLeavesTheControllerAsItIs(bool? reply, string? option, string said)
    {
        using var pi = await InstalledAsync();
        pi.Replies = _ => reply;

        var run = await pi.RunAsync(option is null ? ["cert", "--renew"] : ["cert", "--renew", option]);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.").And.EndWith(said + "\n");
        pi.Questions.Should().HaveCount(option is null ? 1 : 0);
        pi.Deploys.Should().ContainSingle();
        ServesTheCertificateInItsFile(pi).Should().BeFalse("it serves the old one until it is deployed again");
    }

    [TestMethod]
    public async Task Renew_WhenMoreThanTheCertificateWouldChange_LeavesTheRedeployToTheInstaller()
    {
        using var pi = await InstalledAsync();
        pi.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = FakeMachine.ReleaseJson(controllerDigest: "sha256:" + new string('9', 64));

        var run = await pi.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.")
            .And.Contain("The controller is not redeployed from here, because more than its certificate would change: roof-controller (redeployed as release 4.0.0 (it runs version 4.0.0)).")
            .And.EndWith("Run sudo hvo-roof-install to redeploy it, with the new certificate.\n");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task CertPlan_SaysTheControllerIsRedeployed_AndChangesNothing()
    {
        using var pi = await InstalledAsync();
        var before = pi.Snapshot();

        var run = await pi.RunAsync("cert", "--renew", "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("  change     /etc/hvo-roof/https/roof-controller.pfx")
            .And.EndWith(
                "Then the controller is redeployed with the new certificate, once the roof is idle and you agree. The deploy script stops the roof with a verified Stop "
                + "before it replaces the controller, and puts the old one back if the new one fails a check; nothing here moves the roof.\n");
        pi.Snapshot().Should().Equal(before);
        pi.Deploys.Should().ContainSingle();
        pi.Questions.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Import_WithRedeploy_ServesTheirCertificate()
    {
        using var pi = await InstalledAsync();
        var (their, theirs) = InstallerCertificateCommandTests.TheirCertificate();
        using (their)
        using (theirs)
        {
            InstallerCertificateCommandTests.WriteBytes(pi, "/root/their.pfx", theirs.Export(X509ContentType.Pkcs12, TheirPassword));
            pi.Write("/root/password.txt", TheirPassword + "\n");

            var run = await pi.RunAsync("cert", "import", "/root/their.pfx", "--password-file", "/root/password.txt", "--redeploy");

            run.ExitCode.Should().Be(0, run.ToString());
            run.Output.Should().Contain("To serve it, the controller must be deployed again (redeployed with the new certificate).")
                .And.EndWith("The controller serves the new certificate.\n");
            pi.Questions.Should().BeEmpty();
            pi.Deploys.Should().HaveCount(2);
            pi.ServedCertificates[ControllerSettings.DefaultHttpsPort].RawData.Should().Equal(theirs.RawData);
            pi.Deploys[^1]["REMOTE_CA_CERT"].Should().NotBe(Layout.CaCertificate, "the deploy script checks their certificate, which this machine's CA did not issue");
            File.Delete(pi.OnDisk("/root/password.txt"));
            pi.AllText().Should().NotContain(TheirPassword, "the password is never kept, logged or printed");
            (run.Output + run.Error).Should().NotContain(TheirPassword);
        }
    }

    [TestMethod]
    public async Task Renew_OnARig_RedeploysTheControllerAgainstItsEmulator_AndLeavesTheEmulatorAsItIs()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var install = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers
        {
            Roles = [InstallRole.Rig],
            Controller = new ControllerSettings { Rig = new RigSettings { TimeScale = 10, CameraFramesPerSecond = 2 } }
        }));
        install.ExitCode.Should().Be(0, install.ToString());
        var emulator = bench.Containers[MachineSurveyor.HatEmulatorContainer];

        var run = await bench.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().EndWith("The controller serves the new certificate.\n");
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Should().BeSameAs(emulator, "only the controller is redeployed");
        bench.Deploys.Should().HaveCount(2);
        bench.Deploys[^1].Should().Contain(new Dictionary<string, string> { ["HAT_EMULATOR_ENDPOINT"] = "hat-emulator:5291", ["PUBLISH_ADDRESS"] = "127.0.0.1" });
        ServesTheCertificateInItsFile(bench).Should().BeTrue();
    }

    [TestMethod]
    public async Task Renew_ForAStoppedController_SaysItServesItWhenItStarts()
    {
        using var pi = await InstalledAsync();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };

        var run = await pi.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().EndWith("The controller is not running (exited): it serves this certificate when it starts again.\n");
        pi.Deploys.Should().ContainSingle("a stopped controller is not started");
    }

    [TestMethod]
    public async Task RedeployAndNoRedeploy_AreAUsageError_AndRedeploy_NeedsARecordedController()
    {
        using var pi = new FakeMachine().WithPi();
        var before = pi.Snapshot();

        var both = await pi.RunAsync("cert", "--redeploy", "--no-redeploy");
        var unrecorded = await pi.RunAsync("cert", "--redeploy");

        both.ExitCode.Should().Be((int)InstallerExitCode.Usage, both.ToString());
        both.Error.Should().Contain("Give --redeploy or --no-redeploy, not both.");
        unrecorded.ExitCode.Should().Be((int)InstallerExitCode.Refused, unrecorded.ToString());
        unrecorded.Error.Should().Contain("Nothing here is recorded as running the controller, so the installer does not redeploy it (--redeploy).");
        pi.Exists(Layout.Pfx).Should().BeFalse("nothing was changed");
    }

    private static async Task<FakeMachine> InstalledAsync()
    {
        var pi = new FakeMachine().WithPi();
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));
        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle();
        return pi;
    }

    private static bool ServesTheCertificateInItsFile(FakeMachine pi)
    {
        using var inFile = ControllerCertificates.LoadPfx(File.ReadAllBytes(pi.OnDisk(Layout.Pfx)), pi.Read(Layout.PfxPassword))!;
        return pi.ServedCertificates.TryGetValue(ControllerSettings.DefaultHttpsPort, out var served) && served.RawData.AsSpan().SequenceEqual(inFile.RawData);
    }
}
