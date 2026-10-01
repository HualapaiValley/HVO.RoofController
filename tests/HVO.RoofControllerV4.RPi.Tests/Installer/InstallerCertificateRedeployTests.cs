using System.Net;
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
            .And.EndWith("The certificate is in place. Once that is dealt with, run sudo hvo-roof-install cert --redeploy to serve it.\n");
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
    public async Task Renew_WhenAnInstallThatStoppedLeftNewCameraSettings_LeavesTheRedeployToTheInstaller()
    {
        using var pi = await InstalledAsync(new ControllerSettings { Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81" } });

        // As a run that wrote the camera's files and stopped before it redeployed the controller leaves them.
        File.SetLastWriteTimeUtc(pi.OnDisk($"{Layout.Secrets}/{CameraSteps.BaseUrlSetting}"), DateTime.UtcNow.AddMinutes(1));
        var run = await pi.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("The controller is not redeployed from here, because more than its certificate would change: roof-controller (redeployed to read the camera's new settings).");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Renew_WhenTheControllerDoesNotKnowTheInstallersKeysYet_LeavesTheRedeployToTheInstaller()
    {
        using var pi = await InstalledAsync();

        // As an install that wrote a key and stopped before it redeployed the controller leaves it: only the deploy key is known.
        var operatorKey = ControllerProbe.ReadKey(pi.Machine, ApiKeyFiles.KeyFile(Layout.Secrets, 0))!;
        pi.ControllerKeys.IntersectWith([operatorKey]);
        pi.ApiKeyValues().Should().HaveCountGreaterThan(1);

        var run = await pi.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain(
            "more than its certificate would change: roof-controller (redeployed with the new certificate, and to read the installer's new API keys (a controller reads its keys when it starts)).");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Renew_WhenMoreChangesWhileThePersonDecides_IsRefused()
    {
        using var pi = await InstalledAsync(new ControllerSettings { Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81" } });
        pi.Replies = _ =>
        {
            // Another run writes the camera's settings while the question is open.
            File.SetLastWriteTimeUtc(pi.OnDisk($"{Layout.Secrets}/{CameraSteps.BaseUrlSetting}"), DateTime.UtcNow.AddMinutes(1));
            return true;
        };

        var run = await pi.RunAsync("cert", "--renew");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        pi.Questions.Should().Equal(Question);
        run.Error.Should().Contain("The controller was not redeployed:")
            .And.Contain("more than its certificate would change now: redeployed to read the camera's new settings")
            .And.Contain("The certificate is in place; the log is /var/log/hvo-roof-install.log. To serve it, run sudo hvo-roof-install once the roof is idle.")
            .And.NotContain("--redeploy", "cert --redeploy would refuse again: only the installer shows all that changed");
        pi.Deploys.Should().ContainSingle("what the person agreed to was the certificate alone");
    }

    [TestMethod]
    public async Task Renew_WhenMoreChangesWhileThePersonDecides_WithAReleaseFolder_SaysToInstallFromIt()
    {
        using var pi = await InstalledAsync(new ControllerSettings { Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81" } });
        pi.Write("/root/release files/release.json", FakeMachine.ReleaseJson());
        pi.Replies = _ =>
        {
            File.SetLastWriteTimeUtc(pi.OnDisk($"{Layout.Secrets}/{CameraSteps.BaseUrlSetting}"), DateTime.UtcNow.AddMinutes(1));
            return true;
        };

        var run = await pi.RunAsync("cert", "--renew", "--release", "/root/release files");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("To serve it, run sudo hvo-roof-install --release '/root/release files' once the roof is idle.");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Renew_WhenTheRoofStartsMovingWhileThePersonDecides_IsRefused()
    {
        using var pi = await InstalledAsync();
        pi.Replies = _ =>
        {
            pi.StatusJson = Moving;
            return true;
        };

        var run = await pi.RunAsync("cert", "--renew");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        pi.Questions.Should().Equal(Question);
        run.Error.Should().Contain("The controller was not redeployed:")
            .And.Contain("the roof is moving (Opening)")
            .And.Contain("To serve it, run sudo hvo-roof-install cert --redeploy once the roof is idle.");
        pi.Deploys.Should().ContainSingle("the check just before the deploy script refuses a roof that moves");
    }

    [TestMethod]
    public async Task Renew_ForAControllerComposeMade_SaysTheBlock_WithoutWaitingForTheRoof()
    {
        using var pi = await InstalledAsync();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { ComposeProject = "hvo" };

        var run = await pi.RunAsync("cert", "--renew");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("The controller is not redeployed to serve the new certificate: Docker Compose made it (project hvo)")
            .And.EndWith("The certificate is in place. Once that is dealt with, run sudo hvo-roof-install cert --redeploy to serve it.\n")
            .And.NotContain("once the roof is idle");
    }

    [TestMethod]
    public async Task CtrlC_DuringTheRedeploy_SaysWhatRuns_AndHowToFinish()
    {
        using var pi = await InstalledAsync();
        using var interrupt = new CancellationTokenSource();
        pi.Replies = _ =>
        {
            interrupt.Cancel();
            return true;
        };

        var run = await pi.RunAsync(interrupt.Token, "cert", "--renew");

        run.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, run.ToString());
        run.Error.Should().Contain("Stopped before the controller was redeployed. roof-controller runs now, version 4.0.0.")
            .And.Contain("The certificate is in place; the log is /var/log/hvo-roof-install.log. To serve it, run sudo hvo-roof-install cert --redeploy once the roof is idle.")
            .And.NotContain("Run the installer again");
        pi.Deploys.Should().ContainSingle();
        pi.Read(InstallPaths.SystemLog).Should().Contain("Stopped before the controller was redeployed.");
    }

    [TestMethod]
    public async Task CtrlC_WhileTheDeployScriptRuns_SaysItStopsOnceTheScriptEnds_AndWhatRuns()
    {
        using var pi = await InstalledAsync();
        using var interrupt = new CancellationTokenSource();
        pi.Replies = _ => true;
        pi.DuringDeploy = interrupt.Cancel;
        pi.DeployFailure = "[deploy] Interrupted: the old controller was put back.";
        pi.DeployFailureExitCode = 130;

        var run = await pi.RunAsync(interrupt.Token, "cert", "--renew");

        run.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, run.ToString());
        pi.DeploysCancellable.Should().AllBeEquivalentTo(false, "killing the script part-way through the switch would leave the old controller stopped");
        run.Output.Should().Contain(ControllerStep.StoppingMessage, run.ToString());
        run.Error.Should().Contain("The controller was not redeployed: The deploy script was stopped (exit 130): [deploy] Interrupted: the old controller was put back.")
            .And.Contain("roof-controller runs now, version 4.0.0.")
            .And.Contain("To serve it, run sudo hvo-roof-install cert --redeploy once the roof is idle.");
    }

    [TestMethod]
    public async Task CtrlC_AfterTheScriptVerifiedTheController_ServesTheCertificate()
    {
        using var pi = await InstalledAsync();
        using var interrupt = new CancellationTokenSource();
        pi.Replies = _ => true;

        // The script ignores the Ctrl-C once the new controller passed its checks, and runs to its end.
        pi.DuringDeploy = interrupt.Cancel;

        var run = await pi.RunAsync(interrupt.Token, "cert", "--renew");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(ControllerStep.StoppingMessage)
            .And.EndWith("The controller serves the new certificate.\n");
        pi.Deploys.Should().HaveCount(2);
        ServesTheCertificateInItsFile(pi).Should().BeTrue();
    }

    [TestMethod]
    public async Task Redeploy_WhenTheControllerIsNotThere_SaysSo_AndIsRefused()
    {
        using var pi = await InstalledAsync();
        pi.Containers.Remove(MachineSurveyor.ControllerContainer);

        var run = await pi.RunAsync("cert", "--redeploy");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().EndWith("The controller is not deployed yet: hvo-roof-install deploys it, to serve this certificate.\n");
    }

    [TestMethod]
    public async Task Redeploy_WhenTheControllerIsStopped_SaysItServesTheCertificateWhenItStarts()
    {
        using var pi = await InstalledAsync();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };

        var run = await pi.RunAsync("cert", "--redeploy");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().EndWith("The controller is not running (exited): it serves this certificate when it starts again.\n");
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
    public async Task Renew_WhenTheReleaseCannotBeRead_SaysTheCertificateIsInPlace_AndHowToFinish()
    {
        using var pi = await InstalledAsync();
        pi.Downloads.Clear();

        var run = await pi.RunAsync("cert", "--renew", "--redeploy");

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Output.Should().Contain("Changing file /etc/hvo-roof/https/roof-controller.pfx: done.");
        run.Error.Should().Contain("The controller was not redeployed: The installer could not get release.json for 4.0.0")
            .And.Contain("give their folder: --release DIR.")
            .And.Contain("The certificate is in place; the log is /var/log/hvo-roof-install.log. To serve it, run sudo hvo-roof-install cert --redeploy once the roof is idle.");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Renew_WithAReleaseFolder_RedeploysFromIt_AndSaysToUseItAgain()
    {
        using var pi = await InstalledAsync();
        pi.Downloads.Clear();
        pi.Downloaded.Clear();
        pi.Write("/root/release files/release.json", FakeMachine.ReleaseJson());
        var idle = pi.StatusJson;
        pi.StatusJson = Moving;

        var moving = await pi.RunAsync("cert", "--renew", "--redeploy", "--release", "/root/release files");

        moving.ExitCode.Should().Be((int)InstallerExitCode.Refused, moving.ToString());
        moving.Output.Should().EndWith("Once that is dealt with, run sudo hvo-roof-install cert --redeploy --release '/root/release files' to serve it.\n");

        pi.StatusJson = idle;
        var run = await pi.RunAsync("cert", "--redeploy", "--release", "/root/release files");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().EndWith("The controller serves the new certificate.\n");
        pi.Deploys.Should().HaveCount(2);
        pi.Downloaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AReleaseFolderThatIsNotThere_IsAUsageError()
    {
        using var pi = await InstalledAsync();
        var before = pi.Snapshot();

        var run = await pi.RunAsync("cert", "import", "/root/their.pfx", "--release", "/root/nowhere");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("There is no folder /root/nowhere for --release.");
        pi.Snapshot().Should().Equal(before);
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

    [TestMethod]
    public async Task Cert_WhenTheMachinesAddressChanged_IssuesItAgain_AndRedeploysTheControllerToAnswerToIt()
    {
        using var pi = await InstalledAsync();
        pi.Addresses[0] = new NetworkAddress("eth0", IPAddress.Parse("192.168.1.60"));

        var run = await pi.RunAsync("cert", "--redeploy");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("the names changed (adds 192.168.1.60; drops 192.168.1.50): issued again")
            .And.Contain("To serve it, the controller must be deployed again (redeployed with the new certificate, and to answer to its names as they are now).")
            .And.EndWith("The controller serves the new certificate.\n");
        pi.Deploys.Should().HaveCount(2);
        pi.Deploys[^1]["ALLOWED_HOSTS"].Split(';').Should().Contain("192.168.1.60").And.NotContain("192.168.1.50");
        ServesTheCertificateInItsFile(pi).Should().BeTrue();

        var again = await pi.RunAsync("cert");

        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().EndWith("Nothing to change: the controller's certificate is in place as it should be.\n");
        pi.Deploys.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Redeploy_AfterTheAddressChanged_AndTheCertificateWasNotRedeployed_AnswersToTheNewNames()
    {
        using var pi = await InstalledAsync();
        pi.Addresses[0] = new NetworkAddress("eth0", IPAddress.Parse("192.168.1.60"));
        (await pi.RunAsync("cert", "--no-redeploy")).ExitCode.Should().Be(0);

        var run = await pi.RunAsync("cert", "--redeploy");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("To serve it, the controller must be deployed again (redeployed: it serves another certificate than /etc/hvo-roof/https/roof-controller.pfx, and answers to its names as they were).")
            .And.EndWith("The controller serves the new certificate.\n");
        pi.Deploys[^1]["ALLOWED_HOSTS"].Split(';').Should().Contain("192.168.1.60").And.NotContain("192.168.1.50");
    }

    private static async Task<FakeMachine> InstalledAsync(ControllerSettings? settings = null)
    {
        var pi = new FakeMachine().WithPi();
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller], Controller = settings }));
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
