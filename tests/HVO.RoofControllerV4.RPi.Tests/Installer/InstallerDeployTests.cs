using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The controller deployed through the deploy script (#69): by digest, with its settings in the script's environment and
/// a key only as a file's path; the running controller stopped only with a key it knows while the roof is idle; and a
/// test rig's HAT emulator on its own network, with the controller against it.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerDeployTests
{
    private const string Secrets = "/etc/hvo-roof/secrets";

    [TestMethod]
    public async Task ThePi_IsDeployedByDigest_ForTheRealHat_WithOnlyAKeyFilesPath()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["PI_HOST"] = "roofpi",
            ["REMOTE_CONNECT_TO"] = "::127.0.0.1:",
            ["DOCKER_CONTEXT"] = "default",
            ["IMAGE_REF"] = $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{FakeMachine.ControllerDigest}",
            ["BUILD_PLATFORM"] = "linux/arm64",
            ["CONTAINER_NAME"] = MachineSurveyor.ControllerContainer,
            ["HTTPS_HOST_PORT"] = "8443",
            ["WEB_HOST_PORT"] = "8088",
            ["PUBLISH_ADDRESS"] = string.Empty,
            ["EXTRA_DOCKER_ARGS"] = $"--env RoofWeb__StopKeyFile=/run/secrets/RoofControllerSecurity__ApiKeys__2__Key",
            ["HAT_EMULATOR_ENDPOINT"] = string.Empty,
            ["ALLOW_EMULATED_HAT"] = "false",
            ["HVO_FORCE_RASPBERRY_PI"] = "true",
            ["IGNORE_PHYSICAL_LIMIT_SWITCHES"] = "false",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            ["SECRETS_DIR"] = Secrets,
            ["HTTPS_CERT_DIR"] = "/etc/hvo-roof/https",
            ["HTTPS_CERT_FILE"] = "roof-controller.pfx",
            ["ALLOW_INSECURE_HTTP"] = "false",
            ["REMOTE_CA_CERT"] = "/etc/hvo-roof/ca.crt",
            ["SKIP_REMOTE_CHECK"] = "false",
            ["REQUIRE_IDLE_ROOF"] = "true",
            ["OPERATOR_KEY_FILE"] = $"{Secrets}/RoofControllerSecurity__ApiKeys__0__Key",
            ["ROOF_OPERATOR_API_KEY"] = string.Empty
        });
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(controller => !controller.Emulated && controller.Digest == FakeMachine.ControllerDigest);
        var script = pi.Ran.Should().Contain(command => command.Program == "bash", "the deploy script runs").Which;
        script.Environment.Should().NotBeNull();
        script.InheritEnvironment.Should().BeFalse("a variable the person happened to set must not change what the script does");
        Directory.GetDirectories(pi.OnDisk("/tmp")).Should().BeEmpty("the work folder with the script is deleted");

        var log = pi.Read(InstallPaths.SystemLog);
        log.Should().Contain("Running the deploy script with PI_HOST=roofpi ").And.Contain("Deployed roof-controller: release 4.0.0, verified by the deploy script.");
        foreach (var key in pi.ApiKeyValues())
        {
            log.Should().NotContain(key);
            run.ToString().Should().NotContain(key);
        }

        var second = await pi.RunAsync("--answers", answers);
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        pi.Deploys.Should().ContainSingle("a second run changes nothing");
    }

    [TestMethod]
    public async Task ARig_RunsTheEmulatorOnItsOwnNetwork_AndTheControllerAgainstIt_OnLoopback()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var answers = bench.WriteAnswers(new InstallAnswers
        {
            Roles = [InstallRole.Rig],
            Controller = new ControllerSettings { Rig = new RigSettings { TimeScale = 10, CameraFramesPerSecond = 2 } }
        });

        var run = await bench.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        var emulatorImage = $"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{FakeMachine.EmulatorDigest}";
        bench.Ran.Should().Contain(command => command.Program == "docker" && command.Arguments.SequenceEqual(new[] { "pull", "--platform", "linux/amd64", emulatorImage }));
        bench.Networks.Should().Contain(HatEmulatorStep.Network);
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Should().BeEquivalentTo(new FakeContainer
        {
            Network = HatEmulatorStep.Network,
            PublishAddress = "127.0.0.1",
            Digest = FakeMachine.EmulatorDigest,
            Settings = new Dictionary<string, string> { [MachineSurveyor.EmulatorTimeScaleSetting] = "10", [MachineSurveyor.EmulatorCameraFramesSetting] = "2" }
        });
        bench.Deploys.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["BUILD_PLATFORM"] = "linux/amd64",
            ["HAT_EMULATOR_ENDPOINT"] = "hat-emulator:5291",
            ["ALLOW_EMULATED_HAT"] = "true",
            ["PUBLISH_ADDRESS"] = "127.0.0.1",
            ["EXTRA_DOCKER_ARGS"] = "--env RoofWeb__StopKeyFile=/run/secrets/RoofControllerSecurity__ApiKeys__2__Key --network hvo-emulator"
        });
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(controller => controller.Emulated && controller.Network == HatEmulatorStep.Network);
        InstallRecord.Parse(bench.Read(InstallPaths.SystemRecord)).Hat.Should().Be(HatMode.Emulated);
        run.Output.Should().Contain("https://localhost:8443/").And.Contain("nothing here moves a roof");

        var second = await bench.RunAsync("--answers", answers);
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        bench.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ARigOpenToTheNetwork_PublishesOnEveryAddress()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers
        {
            Roles = [InstallRole.Rig],
            Controller = new ControllerSettings { Rig = new RigSettings { OpenToLan = true } }
        }));

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Deploys.Should().ContainSingle().Which["PUBLISH_ADDRESS"].Should().BeEmpty();
        bench.Containers[MachineSurveyor.HatEmulatorContainer].PublishAddress.Should().Be("127.0.0.1", "the emulator's control API stays on loopback");
    }

    [TestMethod]
    public async Task ReplacingARigsEmulator_StopsTheControllerThatDrivesIt_First()
    {
        using var bench = RigWithAnOldEmulator();

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }));

        run.ExitCode.Should().Be(0, run.ToString());
        var docker = bench.Ran.Where(command => command.Program is "docker" or "bash").Select(command => string.Join(' ', command.Arguments.Take(2))).ToList();
        docker.IndexOf("stop roof-controller").Should().BeGreaterThan(-1).And.BeLessThan(docker.IndexOf("stop hat-emulator"), "the controller stops before its emulator goes");
        bench.Read(InstallPaths.SystemLog).Should().Contain("Stopped roof-controller");
        bench.Deploys.Should().ContainSingle("the controller is started again against the new emulator");
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Digest.Should().Be(FakeMachine.EmulatorDigest);
    }

    [TestMethod]
    public async Task AnEmulatorThatDoesNotStart_SaysTheControllerItStopped_StaysStopped()
    {
        using var bench = RigWithAnOldEmulator();
        bench.EmulatorHealth = "unhealthy";

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain("hat-emulator did not become healthy (it is unhealthy): see docker logs hat-emulator. "
            + "roof-controller, which drives the emulated roof, was stopped to replace the emulator and stays stopped: run the installer again to start both.");
        bench.Containers[MachineSurveyor.ControllerContainer].State.Should().Be("exited");
        bench.Deploys.Should().BeEmpty();
    }

    private static FakeMachine RigWithAnOldEmulator()
    {
        var bench = InstallerPlanTests.Installed(new FakeMachine(architecture: Architecture.X64, hostName: "bench")
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = true, Network = HatEmulatorStep.Network, PublishAddress = "127.0.0.1" })
            .WithContainer(MachineSurveyor.HatEmulatorContainer, new FakeContainer { Network = HatEmulatorStep.Network, PublishAddress = HatEmulatorStep.ControlAddress, Digest = "sha256:" + new string('d', 64) })
            .WithCertificates());
        bench.Write(InstallPaths.SystemRecord, InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated).ToJson());
        return bench;
    }

    [TestMethod]
    public async Task TheRunningController_IsStoppedWithAKeyItKnows()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer()).WithCertificates()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole, known: false)
            .WithApiKey(1, "roof-admin", RoofControllerApiContract.AdminRole);
        pi.PortsInUse.UnionWith([8443, 8088]);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which["OPERATOR_KEY_FILE"].Should().Be($"{Secrets}/RoofControllerSecurity__ApiKeys__1__Key", "the controller knows the admin key, not the operator key added after it started");
        pi.Read(InstallPaths.SystemLog).Should().Contain("The deploy script will stop roof-controller with roof-admin (RoofAdmin), a key it knows.");
    }

    [TestMethod]
    public async Task AnOperatorKeyFile_WhoseKeyTheControllerHasByItsHash_StopsIt()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer()).WithCertificates()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole, key: false);
        pi.PortsInUse.UnionWith([8443, 8088]);
        var key = ApiKeyFiles.NewKey();
        pi.ControllerKeys.Add(key);
        pi.Machine.WriteAtomically($"{Secrets}/{ApiKeyFiles.FileName(0, "KeySha256")}", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))), Modes.PrivateFile);
        pi.Environment["SUDO_USER"] = "pi";
        pi.Machine.CreateDirectory("/home/pi/.config/hvo-roof", Modes.PrivateFolder);
        pi.Machine.WriteAtomically("/home/pi/.config/hvo-roof/operator.key", key + "\n", Modes.PrivateFile);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which["OPERATOR_KEY_FILE"].Should().Be("/home/pi/.config/hvo-roof/operator.key");
        run.ToString().Should().NotContain(key);
        pi.Read(InstallPaths.SystemLog).Should().NotContain(key);
    }

    [TestMethod]
    public async Task AControllerThatKnowsNoKeyHere_BlocksTheInstall_BeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer()).WithCertificates()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole, known: false);
        pi.PortsInUse.UnionWith([8443, 8088]);

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        var check = plan.Steps.Single(step => step.Step.Target == MachineSurveyor.ControllerContainer).Check;
        check.Change.Should().Be(StepChange.Blocked);
        check.Detail.Should().StartWith("redeployed so the web UI's Stop has a key of its own, but it runs, and it knows no operator or admin key in /etc/hvo-roof/secrets")
            .And.Contain("Stop the controller yourself once the roof is idle (docker stop roof-controller), then run the installer again. ")
            .And.EndWith("Or, if it has an operator key, put that key in /etc/hvo-roof/secrets (\"API keys\" in docs/deployment.md), and the deploy script stops it; a controller from before API keys has none.");
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("{\"isMoving\":true,\"commandedMotion\":\"Opening\"}", "the roof is moving (Opening), and the deploy script stops it before it replaces the controller: run the installer again once the roof is idle", DisplayName = "Moving")]
    [DataRow(null, "it runs but does not answer an authenticated Status (", DisplayName = "No answer")]
    [DataRow("[]", "it runs but does not answer an authenticated Status (its Status answered with something that is not the controller's)", DisplayName = "An array")]
    [DataRow("\"ok\"", "it runs but does not answer an authenticated Status (its Status answered with something that is not the controller's)", DisplayName = "A string")]
    public async Task ARoofThatMoves_OrAControllerThatDoesNotAnswer_BlocksTheRedeploy(string? status, string expected)
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.StatusJson = status;

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        plan.Steps.Single(step => step.Step.Target == MachineSurveyor.ControllerContainer).Check
            .Should().Match<StepCheck>(check => check.Change == StepChange.Blocked && check.Detail!.StartsWith(expected, StringComparison.Ordinal));
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        pi.Deploys.Should().BeEmpty();
        pi.Ran.Should().NotContain(command => command.Arguments.Contains("stop"), "nothing is stopped");
    }

    [TestMethod]
    [DataRow("paused", "it is paused, and the deploy script's verified Stop needs it to answer: unpause it (docker unpause roof-controller)")]
    [DataRow("restarting", "it is restarting, and the deploy script's verified Stop needs it to answer: wait until it runs")]
    public async Task APausedOrRestartingController_BlocksTheRedeploy(string state, string expected)
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = state };

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        plan.Steps.Single(step => step.Step.Target == MachineSurveyor.ControllerContainer).Check
            .Should().Match<StepCheck>(check => check.Change == StepChange.Blocked && check.Detail!.StartsWith(expected, StringComparison.Ordinal));
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task WithoutRoot_ThePlanSaysOnlyRootCanCheckTheKeys()
    {
        using var pi = new FakeMachine(root: false).WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer()).WithCertificates();
        InstallerPlanTests.Installed(pi);
        File.SetUnixFileMode(pi.OnDisk(Secrets), UnixFileMode.None);
        try
        {
            var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

            plan.Steps.Single(step => step.Step.Target == MachineSurveyor.ControllerContainer).Check.Should().Be(
                new StepCheck(StepChange.Info, "adopted: running, version 4.0.0; only root can read its keys: run with sudo to check it knows them"));
            pi.Ran.Should().NotContain(command => command.Arguments.FirstOrDefault() == "exec");
        }
        finally
        {
            File.SetUnixFileMode(pi.OnDisk(Secrets), Modes.PrivateFolder);
        }
    }

    [TestMethod]
    public async Task AScriptThatFails_StopsTheInstall_WithItsLastWords()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.DeployFailure = "[deploy] ERROR: the pre-flight check failed: the new image did not start. Nothing was changed.";

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain("The deploy script stopped (exit 1): [deploy] ERROR: the pre-flight check failed: the new image did not start. Nothing was changed.")
            .And.Contain("roof-controller runs now, version 4.0.0.");
        pi.Exists(InstallPaths.SystemRecord).Should().BeFalse("the record says the roles are installed only once they are");
    }

    [TestMethod]
    public async Task AnInterruptOnlyTheInstallerGets_WhileTheScriptRuns_LetsItFinish()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        using var interrupt = new CancellationTokenSource();
        pi.DuringDeploy = interrupt.Cancel;

        var run = await pi.RunAsync(interrupt.Token, "--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        pi.DeploysCancellable.Should().Equal([false], "killing the script part-way through the switch would leave the old controller stopped");
        run.Output.Should().Contain(ControllerStep.StoppingMessage, run.ToString());
        pi.Containers[MachineSurveyor.ControllerContainer].Settings.Should().ContainKey(MachineSurveyor.WebStopKeyFileSetting, "the script finished the switch");
        run.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, run.ToString());
    }

    [TestMethod]
    public async Task ACtrlC_ThatStopsTheScriptToo_SaysWhatRuns_AndExits130()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        using var interrupt = new CancellationTokenSource();
        pi.DuringDeploy = interrupt.Cancel;
        pi.DeployFailure = "[deploy] Interrupted: roof-controller was put back.";
        pi.DeployFailureExitCode = 130;

        var run = await pi.RunAsync(interrupt.Token, "--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, run.ToString());
        run.Output.Should().Contain(ControllerStep.StoppingMessage, run.ToString());
        run.Error.Should().Contain("The deploy script was stopped (exit 130): [deploy] Interrupted: roof-controller was put back.", run.ToString())
            .And.Contain("roof-controller runs now, version 4.0.0.")
            .And.Contain("Run the installer again to carry on");
    }

    [TestMethod]
    public async Task AScriptStoppedBySignal_WithoutTheInstallersCtrlC_StillExits130()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.DeployFailure = "[deploy] Terminated.";
        pi.DeployFailureExitCode = 143;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Cancelled, run.ToString());
        run.Error.Should().Contain("The deploy script was stopped (exit 143)", run.ToString());
    }

    [TestMethod]
    public async Task TheDeployToolsMissing_BlockTheInstall()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Programs.Remove("setsid");
        pi.Programs.Remove("jq");

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        plan.Steps.Single(step => step.Step is DeployToolsStep).Check.Should().Match<StepCheck>(check =>
            check.Change == StepChange.Blocked && check.Detail!.StartsWith("missing setsid or perl, jq or python3", StringComparison.Ordinal)
            && check.Detail.Contains("sudo apt-get install util-linux jq", StringComparison.Ordinal));
        plan.IsBlocked.Should().BeTrue();
    }
}
