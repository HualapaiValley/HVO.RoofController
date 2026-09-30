using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
/// The plan for each role on a fake machine (#67): every folder, file, container, service and port, what each would do
/// there, and a second run that finds nothing to change.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerPlanTests
{
    /// <summary>The installer's keys on a machine that had none: its operator key, its admin key and the web UI's Stop key.</summary>
    internal static readonly string[] NewKeys =
    [
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Key",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Key"
    ];

    private static readonly string[] ControllerFolders =
    [
        "/etc/hvo-roof", "/etc/hvo-roof/secrets", "/etc/hvo-roof/https", "/etc/hvo-roof/ca", "/etc/hvo-roof/config",
        "/var/lib/hvo-roof", "/var/lib/hvo-roof/identity", "/var/lib/hvo-roof/settings-secrets"
    ];

    [TestMethod]
    public async Task TheController_OnAFreshPi_MakesTheDeployScriptsFolders_TheContainer_AndTheRecord()
    {
        using var pi = new FakeMachine().WithPi();

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Steps(plan, StepKind.Folder).Should().Equal(ControllerFolders);
        plan.Steps.Where(step => step.Step.Kind == StepKind.Folder).Select(step => step.Check.Detail).Should().Equal(
            "0755", "0700", "0700", "0700", "0755", "0755", "0700", "0700");
        Steps(plan, StepKind.Container).Should().Equal(MachineSurveyor.ControllerContainer);
        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Create, "release 4.0.0 by digest, through the deploy script"));
        Change(plan, "bash, curl, setsid or perl, jq or python3").Should().Be(new StepCheck(StepChange.Info, "found bash, curl, setsid, jq"));
        Steps(plan, StepKind.Port).Should().Equal("8443", "8088");
        Change(plan, "8443").Detail.Should().Be("free; roof-controller will listen on it");
        Steps(plan, StepKind.File).Should().Equal(
            ["/etc/hvo-roof/ca.crt", "/etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password", "/etc/hvo-roof/https/roof-controller.pfx", .. NewKeys, InstallPaths.SystemRecord]);
        Change(plan, NewKeys[0]).Should().Be(new StepCheck(StepChange.Create, "installer-operator (RoofOperator): a new random key, never shown"));
        Change(plan, InstallPaths.SystemRecord).Should().Be(new StepCheck(StepChange.Create, "0644"));
        plan.Steps[^1].Step.Should().BeOfType<RecordStep>("the record says the roles are installed once they are");
        PlanText.Summary(plan).Should().Be("16 to create, 0 to change, 0 unchanged.");
    }

    [TestMethod]
    public async Task ThePlansText_GroupsTheStepsByWhatTheyMake()
    {
        using var pi = new FakeMachine().WithPi();

        var lines = PlanText.Lines(await CheckAsync(pi, InstallRole.Controller));

        lines.Where(line => !line.StartsWith(' ') && line.Length > 0).Should().Equal(
            "Folders", "Files", "Packages", "Containers", "Ports", "16 to create, 0 to change, 0 unchanged.");
        lines.Should().Contain(line => line.StartsWith("  create     /etc/hvo-roof/secrets ", StringComparison.Ordinal) && line.EndsWith("secrets the controller reads, one file per setting (0700)", StringComparison.Ordinal));
        lines.Should().Contain(line => line.StartsWith("  info       8443 ", StringComparison.Ordinal) && line.Contains("the controller's API (HTTPS)", StringComparison.Ordinal));
        lines.Should().Contain(line => line.StartsWith("  create     roof-controller ", StringComparison.Ordinal) && line.Contains("the controller, driving the real HAT (release 4.0.0 by digest, through the deploy script)", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheController_OverHttp_PublishesItsApiOnTheHttpPort()
    {
        using var pi = new FakeMachine().WithPi();

        var plan = await CheckAsync(pi, new InstallAnswers
        {
            Roles = [InstallRole.Controller],
            Controller = new ControllerSettings { Connection = ConnectionMode.Http, HttpPort = 8090, WebPort = 8099 }
        });

        Steps(plan, StepKind.Port).Should().Equal("8090", "8099");
        plan.Steps.Single(step => step.Step.Target == "8090").Step.Purpose.Should().Be("the controller's API (HTTP)");
    }

    [TestMethod]
    public async Task TheDeployScriptsContainer_IsAdopted_AndItsPortsAreItsOwn()
    {
        using var pi = InstalledPi();

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(StepCheck.Unchanged("adopted: running, version 4.0.0"));
        Change(plan, "8443").Should().Be(new StepCheck(StepChange.Info, "roof-controller listens on it"));
        plan.Steps.Where(step => step.Check.MakesChange).Select(step => step.Step.Target).Should().Equal([InstallPaths.SystemRecord], "it already runs the release, with the installer's keys");
    }

    [TestMethod]
    public async Task AnOlderRelease_IsRedeployed_WithTheKeyItKnows()
    {
        using var pi = InstalledPi();
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { Version = "3.9.1", Digest = "sha256:" + new string('b', 64) };

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed as release 4.0.0 (it runs version 3.9.1)"));
        plan.IsBlocked.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheDeployScriptsContainer_WithoutTheInstallersKeys_IsRedeployedToReadThem()
    {
        using var pi = AdoptablePi();

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key").Should().Be(StepCheck.Unchanged("reuses roof-operator (RoofOperator)"));
        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed so the web UI's Stop has a key of its own"));
    }

    [TestMethod]
    public async Task AnAdoptedContainer_OnOtherPorts_IsRedeployed_AndTheAnswersPortsAreChecked()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { ApiPort = 7151 })
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole);
        pi.PortsInUse.UnionWith([7151, 8088]);

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed on ports 8443 and 8088 (it publishes 7151 and 8088)"));
        Change(plan, "8443").Should().Be(new StepCheck(StepChange.Info, "free; roof-controller will listen on it"), "the container does not publish 8443, so 8443 is checked");
        Change(plan, "8088").Should().Be(new StepCheck(StepChange.Info, "roof-controller listens on it"));

        pi.PortsInUse.Add(8443);
        Change(await CheckAsync(pi, InstallRole.Controller), "8443").Change.Should().Be(StepChange.Blocked, "something else has the port the answers give");

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, "something else has 8443: {0}", run);
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnAdoptedContainer_ServingHttp_IsRedeployed_ForHttps()
    {
        using var pi = Installed(new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Https = false })
            .WithCertificates(new ControllerSettings { Connection = ConnectionMode.Http }));

        Change(await CheckAsync(pi, InstallRole.Controller), MachineSurveyor.ControllerContainer)
            .Should().Be(new StepCheck(StepChange.Change, "redeployed to serve HTTPS"));

        var overHttp = await CheckAsync(pi, new InstallAnswers
        {
            Roles = [InstallRole.Controller],
            Controller = new ControllerSettings { Connection = ConnectionMode.Http }
        });
        Change(overHttp, MachineSurveyor.ControllerContainer).Should().Be(StepCheck.Unchanged("adopted: running, version 4.0.0"));
        Change(overHttp, "8080").Should().Be(new StepCheck(StepChange.Info, "roof-controller listens on it"));
    }

    [TestMethod]
    public async Task ARecordFromANewerInstaller_IsNeverReplaced()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer());
        pi.Write(InstallPaths.SystemRecord, "{\"schema\": 2, \"scope\": \"system\", \"roles\": [\"controller\"]}");

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, InstallPaths.SystemRecord).Should().Be(new StepCheck(
            StepChange.Blocked,
            "a newer installer wrote it (schema 2), and this one does not replace it: install with that installer, or a newer one"));
        plan.IsBlocked.Should().BeTrue();

        // Read as the record itself is, with its comments skipped.
        pi.Write(InstallPaths.SystemRecord, "{ /* kept by hand */ \"schema\": 2, \"scope\": \"system\", \"roles\": [\"controller\"]}");
        Change(await CheckAsync(pi, InstallRole.Controller), InstallPaths.SystemRecord).Change.Should().Be(StepChange.Blocked, "a comment does not hide the schema");

        pi.Write(InstallPaths.SystemRecord, "{ damaged");
        Change(await CheckAsync(pi, InstallRole.Controller), InstallPaths.SystemRecord)
            .Should().Be(new StepCheck(StepChange.Change, "replaces the record that could not be read"), "a damaged record is replaced");
    }

    [TestMethod]
    public async Task AComposeContainer_IsExplained_AndBlocksTheInstall()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { ComposeProject = "hvo-roofcontroller-rpi" });
        pi.PortsInUse.UnionWith([8443, 8088]);

        var plan = await CheckAsync(pi, InstallRole.Controller);

        var check = Change(plan, MachineSurveyor.ControllerContainer);
        check.Change.Should().Be(StepChange.Blocked);
        check.Detail.Should().Contain("Docker Compose made it (project hvo-roofcontroller-rpi)").And.Contain("does not replace it").And.Contain("docs/deployment.md");
        plan.IsBlocked.Should().BeTrue();
        PlanText.Lines(plan).Should().Contain($"             {check.Detail}", "a blocked step says why on a line of its own");
        PlanText.Summary(plan).Should().StartWith("Blocked: 3 steps cannot go ahead", "Compose's container is not the installer's, so its ports are taken too");
    }

    [TestMethod]
    public async Task APortSomethingElseListensOn_BlocksTheInstall()
    {
        using var pi = new FakeMachine().WithPi();
        pi.PortsInUse.Add(8088);

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, "8088").Should().Be(new StepCheck(StepChange.Blocked, "something else listens on port 8088: stop it, or choose another port"));
        PlanText.Summary(plan).Should().Be("Blocked: 1 step cannot go ahead, so nothing will be installed.");
    }

    [TestMethod]
    public async Task AFileWhereAFolderGoes_BlocksTheInstall()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write("/etc/hvo-roof/https", "not a folder");

        var plan = await CheckAsync(pi, InstallRole.Controller);

        Change(plan, "/etc/hvo-roof/https").Should().Be(new StepCheck(StepChange.Blocked, "/etc/hvo-roof/https is a file, not a folder"));
    }

    [TestMethod]
    public async Task ARigOnLinux_RunsTheHatEmulator_AndTheControllerAgainstIt()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");

        var plan = await CheckAsync(bench, InstallRole.Rig);

        Steps(plan, StepKind.Folder).Should().Equal(ControllerFolders);
        Steps(plan, StepKind.Container).Should().Equal(MachineSurveyor.HatEmulatorContainer, MachineSurveyor.ControllerContainer);
        plan.Steps.Single(step => step.Step.Target == MachineSurveyor.ControllerContainer).Step.Purpose.Should().Be("the controller, against the HAT emulator");
        Steps(plan, StepKind.File).Should().Equal(
            ["/etc/hvo-roof/ca.crt", "/etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password", "/etc/hvo-roof/https/roof-controller.pfx", .. NewKeys, InstallPaths.SystemRecord]);
    }

    [TestMethod]
    public async Task ARigThatDrivesTheRealHat_IsRedeployedAgainstTheEmulator()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench")
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = false })
            .WithContainer(MachineSurveyor.HatEmulatorContainer, new FakeContainer { Network = HatEmulatorStep.Network, PublishAddress = HatEmulatorStep.ControlAddress })
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole);
        bench.Write(InstallPaths.SystemRecord, InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated).ToJson());

        var plan = await CheckAsync(bench, InstallRole.Rig);

        Change(plan, MachineSurveyor.ControllerContainer).Should().Be(new StepCheck(StepChange.Change, "redeployed against the HAT emulator"));
        Change(plan, MachineSurveyor.HatEmulatorContainer).Should().Be(StepCheck.Unchanged("adopted: running, version 4.0.0"));
    }

    [TestMethod]
    public async Task ARigOnAMac_KeepsEverythingInThePersonsApplicationSupport()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        const string root = "/Users/roy/Library/Application Support/HVO Roof Rig";

        var plan = await CheckAsync(mac, InstallRole.Rig);

        Steps(plan, StepKind.Folder).Should().Equal(
            root, $"{root}/secrets", $"{root}/https", $"{root}/ca", $"{root}/config", $"{root}/identity", $"{root}/settings-secrets");
        Steps(plan, StepKind.File).Should().Equal(
            $"{root}/ca.crt", $"{root}/secrets/Kestrel__Certificates__Default__Password", $"{root}/https/roof-controller.pfx",
            $"{root}/secrets/RoofControllerSecurity__ApiKeys__0__Key", $"{root}/secrets/RoofControllerSecurity__ApiKeys__1__Key", $"{root}/secrets/RoofControllerSecurity__ApiKeys__2__Key",
            "/Users/roy/.config/hvo-roof/install.json");
        Change(plan, "/Users/roy/.config/hvo-roof/install.json").Detail.Should().Be("0600", "the person's record is theirs alone");
    }

    [TestMethod]
    public async Task TheKiosk_IsPlannedWithTheController()
    {
        using var pi = new FakeMachine().WithPi();

        var plan = await CheckAsync(pi, InstallRole.Controller, InstallRole.Kiosk);

        plan.Steps.Select(step => step.Step.Target).Should().ContainInOrder(
            "/opt/hvo-roof-kiosk", "/etc/hvo-roof-kiosk", MachineSurveyor.KioskUnit, InstallPaths.SystemRecord);
        Steps(plan, StepKind.Service).Should().Equal(MachineSurveyor.KioskUnit);
    }

    [TestMethod]
    public async Task HvoRoof_GoesInThePersonsFolder_OrTheSharedOne()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");

        var own = await CheckAsync(laptop, InstallRole.Cli);
        var shared = await CheckAsync(laptop, new InstallAnswers { Roles = [InstallRole.Cli], Cli = new CliSettings { Folder = CliSettings.SharedFolder } });

        Steps(own, StepKind.File).Should().Equal("/home/roy/.local/bin/hvo-roof", "/home/roy/.config/hvo-roof/install.json");
        Steps(shared, StepKind.File).Should().Equal("/usr/local/bin/hvo-roof", "/home/roy/.config/hvo-roof/install.json");
    }

    [TestMethod]
    public async Task HvoRoof_AlreadyThere_IsUnchanged()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy")
            .WithCli("/home/roy/.local/bin/hvo-roof");

        var plan = await CheckAsync(laptop, InstallRole.Cli);

        Change(plan, "/home/roy/.local/bin/hvo-roof").Should().Be(StepCheck.Unchanged("already there"));
    }

    [TestMethod]
    public async Task TheMacApp_GoesInApplications()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");

        var plan = await CheckAsync(mac, InstallRole.MacApp);
        var own = await CheckAsync(mac, new InstallAnswers { Roles = [InstallRole.MacApp], MacApp = new MacAppSettings { Folder = MacAppSettings.HomeFolder } });

        Steps(plan, StepKind.File).Should().Equal("/Applications/HVO Roof.app", "/Users/roy/.config/hvo-roof/install.json");
        Steps(own, StepKind.File)[0].Should().Be("/Users/roy/Applications/HVO Roof.app");
    }

    [TestMethod]
    public async Task ThePersonsRecord_FollowsXdgConfigHome()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");
        laptop.Environment["XDG_CONFIG_HOME"] = "/home/roy/cfg";

        var plan = await CheckAsync(laptop, InstallRole.Cli);

        Steps(plan, StepKind.File)[^1].Should().Be("/home/roy/cfg/hvo-roof/install.json");
    }

    [TestMethod]
    public async Task Applying_MakesTheFoldersWithTheirModes_AndTheRecord_ThenASecondRunChangesNothing()
    {
        using var pi = AdoptablePi();
        var session = await StartAsync(pi, InstallRole.Controller);

        await session.ApplyAsync(await session.CheckAsync());

        foreach (var folder in ControllerFolders)
        {
            pi.Mode(folder).Should().Be(folder.EndsWith("secrets", StringComparison.Ordinal) || folder.EndsWith("https", StringComparison.Ordinal) || folder.EndsWith("identity", StringComparison.Ordinal) || folder.EndsWith("/ca", StringComparison.Ordinal)
                ? Modes.PrivateFolder
                : Modes.Folder, folder);
        }

        var record = InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));
        record.Roles.Should().Equal(InstallRole.Controller);
        record.Hat.Should().Be(HatMode.Real);
        record.Version.Should().Be("4.0.0");
        record.Scope.Should().Be(InstallScope.System);
        pi.Mode(InstallPaths.SystemRecord).Should().Be(Modes.File);

        var before = pi.Snapshot();
        var again = await StartAsync(pi, InstallRole.Controller);
        var second = await again.CheckAsync();
        second.HasChanges.Should().BeFalse();
        PlanText.Summary(second).Should().Be("Nothing to change: this machine is already as the answers describe.");
        await again.ApplyAsync(second);
        pi.Snapshot().Should().Equal(before, "a second run changes nothing");
    }

    [TestMethod]
    public async Task AFoldersModeThatDiffers_IsSet_AndItsContentsAreLeftAlone()
    {
        using var pi = AdoptablePi();
        pi.Write("/etc/hvo-roof/secrets/RoofController__Example", "kept");
        File.SetUnixFileMode(pi.OnDisk("/etc/hvo-roof/secrets"), Modes.Folder);
        var session = await StartAsync(pi, InstallRole.Controller);

        var plan = await session.CheckAsync();
        Change(plan, "/etc/hvo-roof/secrets").Should().Be(new StepCheck(StepChange.Change, "0755 → 0700"));
        await session.ApplyAsync(plan);

        pi.Mode("/etc/hvo-roof/secrets").Should().Be(Modes.PrivateFolder);
        pi.Read("/etc/hvo-roof/secrets/RoofController__Example").Should().Be("kept");
    }

    [TestMethod]
    public async Task AStepThisInstallerCannotCarryOutYet_RefusesTheInstallBeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        var session = await StartAsync(pi, InstallRole.Controller, InstallRole.Kiosk);
        var before = pi.Snapshot();

        var install = async () => await session.ApplyAsync(await session.CheckAsync());

        (await install.Should().ThrowAsync<InstallerRefusedException>()).Which.Message.Should()
            .Be("This installer cannot install /opt/hvo-roof-kiosk, /etc/hvo-roof-kiosk, hvo-roof-kiosk.service yet, so nothing was installed. The plan (--plan) shows what an install will do.");
        pi.Snapshot().Should().Equal(before);
    }

    [TestMethod]
    public async Task ABlockedPlan_IsNeverApplied()
    {
        using var pi = AdoptablePi();
        pi.PortsInUse.Add(8088);
        pi.Containers[MachineSurveyor.ControllerContainer] = pi.Containers[MachineSurveyor.ControllerContainer] with { State = "exited" };
        var session = await StartAsync(pi, InstallRole.Controller);
        var before = pi.Snapshot();

        var install = async () => await session.ApplyAsync(await session.CheckAsync());

        await install.Should().ThrowAsync<InstallerRefusedException>();
        pi.Snapshot().Should().Equal(before);
    }

    [TestMethod]
    public async Task AStepBlockedSinceThePlanWasChecked_StopsTheInstall_AsAFailure()
    {
        using var pi = AdoptablePi();
        var session = await StartAsync(pi, InstallRole.Controller);
        var plan = await session.CheckAsync();
        Change(plan, InstallPaths.SystemRecord).Change.Should().Be(StepChange.Create);

        // A newer installer writes its record while the review page is open.
        const string Newer = "{\"schema\": 2, \"scope\": \"system\", \"roles\": [\"controller\"]}";
        pi.Write(InstallPaths.SystemRecord, Newer);
        var install = async () => await session.ApplyAsync(plan);

        var failure = (await install.Should().ThrowAsync<InstallerException>()).Which;
        failure.Should().NotBeOfType<InstallerRefusedException>("the folders before it were made: this is a failure, not a refusal");
        failure.Message.Should().Be($"Creating file {InstallPaths.SystemRecord}: a newer installer wrote it (schema 2), and this one does not replace it: install with that installer, or a newer one");
        pi.Read(InstallPaths.SystemRecord).Should().Be(Newer, "the newer record is kept");
    }

    [TestMethod]
    public async Task TheControllersRecord_ReplacesARig_AndKeepsTheKiosk()
    {
        using var pi = AdoptablePi();
        pi.Write(InstallPaths.SystemRecord, (InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated) with { Roles = [InstallRole.Rig, InstallRole.Kiosk] }).ToJson());
        var session = await StartAsync(pi, InstallRole.Controller);
        var context = session.Context;
        var (existing, _) = InstallRecord.Load(pi.Machine, InstallPaths.SystemRecord);

        var record = PlanBuilder.BuildRecord(context, InstallScope.System, [InstallRole.Controller], context.Answers, existing);

        record.Roles.Should().Equal(InstallRole.Controller, InstallRole.Kiosk);
        record.Hat.Should().Be(HatMode.Real);
        record.InstalledAt.Should().Be(existing!.InstalledAt, "the first install's time is kept");
    }

    [TestMethod]
    public async Task TheInstaller_OnlyReadsWithCommands_AndNeverMovesTheRoof()
    {
        using var pi = AdoptablePi();
        var session = await StartAsync(pi, InstallRole.Controller);

        await session.ApplyAsync(await session.CheckAsync());

        pi.Unexpected.Should().BeEmpty();
        pi.Deploys.Should().ContainSingle("the controller is replaced through the deploy script, which stops the roof first");
        var ran = pi.Ran.ToArray();
        ran.Where(command => command.Program != "docker").Should().OnlyContain(
            command => command.Program == "bash" && command.Arguments.Count == 1 && command.Arguments[0].EndsWith("/deploy-roofcontroller-rpi.sh", StringComparison.Ordinal));
        ran.Where(command => command.Program == "docker").Select(command => command.Arguments[0]).Distinct().Should()
            .BeSubsetOf(["version", "compose", "container", "context", "exec"], "it asks Docker, and leaves the controller's container to the deploy script");
        ran.Where(command => command.Arguments.FirstOrDefault() == "exec").Should().OnlyContain(
            command => command.Arguments.Last() == $"http://localhost:8080/{ControllerProbe.StatusPath}", "the only request it makes of the controller is its Status");
        ran.SelectMany(command => command.Arguments).Should().NotContain(
            argument => argument.Contains("/Open", StringComparison.OrdinalIgnoreCase) || argument.Contains("/Close", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A Pi with the HAT, where the deploy script already runs the controller: the installer adopts it.</summary>
    internal static FakeMachine AdoptablePi()
    {
        var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = false }).WithCertificates()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole);
        pi.PortsInUse.UnionWith([8443, 8088]);
        return pi;
    }

    /// <summary>
    /// <see cref="AdoptablePi"/> once the installer has installed it: its operator, admin and web UI Stop keys, which the
    /// running controller knows, its web UI reading its Stop key, and no telemetry.
    /// </summary>
    internal static FakeMachine InstalledPi() => Installed(AdoptablePi());

    /// <summary>
    /// The installer's admin and web UI Stop keys beside the operator key at entry 0, and the controller's settings and
    /// data folders, as an install leaves them.
    /// </summary>
    internal static FakeMachine Installed(FakeMachine pi)
    {
        var layout = ControllerLayout.For(pi.Machine);
        pi.Machine.CreateDirectory(layout.Settings, Modes.Folder);
        pi.Machine.CreateDirectory(layout.Data, Modes.Folder);
        pi.Machine.CreateDirectory(layout.Identity, Modes.PrivateFolder);
        pi.Machine.CreateDirectory(layout.SettingsSecrets, Modes.PrivateFolder);
        pi.WithApiKey(1, ApiKeyFiles.AdminName, RoofControllerApiContract.AdminRole).WithApiKey(2, ApiKeyFiles.WebStopName, RoofControllerApiContract.ViewerRole);
        if (!File.Exists(pi.OnDisk(ApiKeyFiles.KeyFile(layout.Secrets, 0))))
        {
            pi.WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole);
        }

        var controller = pi.Containers[MachineSurveyor.ControllerContainer];
        pi.Containers[MachineSurveyor.ControllerContainer] = controller with
        {
            Settings = new Dictionary<string, string>(controller.Settings)
            {
                [MachineSurveyor.WebStopKeyFileSetting] = ApiKeyFiles.ContainerKeyFile(2),
                [MachineSurveyor.TelemetryEndpointSetting] = string.Empty
            }
        };
        return pi;
    }

    internal static async Task<InstallerSession> StartAsync(FakeMachine machine, params InstallRole[] roles)
    {
        var session = await InstallerSession.StartAsync(machine.Machine, InstallLog.None, "4.0.0+0123456789abcdef", FakeMachine.Clock);
        session.Answers = new InstallAnswers { Roles = roles }.Normalised();
        return session;
    }

    private static Task<CheckedPlan> CheckAsync(FakeMachine machine, params InstallRole[] roles)
        => CheckAsync(machine, new InstallAnswers { Roles = roles });

    private static async Task<CheckedPlan> CheckAsync(FakeMachine machine, InstallAnswers answers)
    {
        var session = await InstallerSession.StartAsync(machine.Machine, InstallLog.None, "4.0.0+0123456789abcdef", FakeMachine.Clock);
        session.Answers = answers.Normalised();
        return await session.CheckAsync();
    }

    private static string[] Steps(CheckedPlan plan, StepKind kind)
        => plan.Steps.Where(step => step.Step.Kind == kind).Select(step => step.Step.Target).ToArray();

    private static StepCheck Change(CheckedPlan plan, string target) => plan.Steps.Single(step => step.Step.Target == target).Check;
}
