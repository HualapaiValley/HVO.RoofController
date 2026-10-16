using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The controller container's resource limits (#129; docs/deployment.md, "The container's privileges and limits"): 2
/// CPUs, 4 GiB and 512 processes unless the answers or the command line give others, refused below the HVO install
/// contract's minimum without the acknowledgment, checked against what Docker enforces and what the machine has before
/// anything changes, kept in the install record, and given to the deploy script by every deploy, upgrade and rollback.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerResourceLimitsTests
{
    private const string NewVersion = "4.0.1";

    [TestMethod]
    public void TheDefaults_AreTheContractsMinimum_AndNeedNoAcknowledgment()
    {
        var resources = new ContainerResources();

        resources.Should().Be(new ContainerResources { Cpus = 2, Memory = "4g", PidsLimit = 512, AllowBelowMinimum = false });
        resources.Problems().Should().BeEmpty();
        resources.BelowMinimum.Should().BeFalse();
        resources.NanoCpus.Should().Be(2_000_000_000);
        resources.MemoryBytes.Should().Be(4L * 1024 * 1024 * 1024);
        resources.Describe().Should().Be("2 CPUs, 4g of memory and no swap, at most 512 processes and threads");
    }

    [TestMethod]
    public void TheLimits_AreWrittenPlainly_AsTheDeployScriptWritesThem()
    {
        var resources = new ContainerResources { Cpus = 2.50m, Memory = "06144M", PidsLimit = 1024 }.Normalised();

        resources.CpusText.Should().Be("2.5");
        resources.Memory.Should().Be("6144m");
        resources.MemoryBytes.Should().Be(6144L * 1024 * 1024);
        resources.NanoCpus.Should().Be(2_500_000_000);
        new ContainerResources { Memory = "5G" }.Normalised().Memory.Should().Be("5g");
    }

    [TestMethod]
    [DataRow(0, "4g", 512, "must be a number such as 2 or 1.5")]
    [DataRow(2.555, "4g", 512, "with at most two decimals, not 2.555")]
    [DataRow(1000, "4g", 512, "must be a number such as 2 or 1.5")]
    [DataRow(0.99, "4g", 512, "never given less than 1 CPU (controller.resources.cpus is 0.99")]
    [DataRow(2, "4", 512, "must be a whole number of MiB or GiB, such as 4096m or 4g, not '4'")]
    [DataRow(2, "4.5g", 512, "not '4.5g'")]
    [DataRow(2, "4gb", 512, "not '4gb'")]
    [DataRow(2, "1023m", 512, "never given less than 1g of memory (controller.resources.memory is 1023m")]
    [DataRow(2, "4g", 255, "must be from 256 to 32768, not 255")]
    [DataRow(2, "4g", 32769, "must be from 256 to 32768, not 32769")]
    [DataRow(1.5, "4g", 512, "The controller's limits (1.5 CPUs, 4g) are below the minimum of 2 CPUs and 4g")]
    [DataRow(2, "3g", 512, "The controller's limits (2 CPUs, 3g) are below the minimum of 2 CPUs and 4g")]
    public void LimitsTheControllerIsNotGiven_AreProblems(double cpus, string memory, int pids, string problem)
    {
        new ContainerResources { Cpus = (decimal)cpus, Memory = memory, PidsLimit = pids }.Problems()
            .Should().ContainSingle().Which.Should().Contain(problem);
    }

    [TestMethod]
    public void LimitsBelowTheMinimum_AreTakenWithTheAcknowledgment_ButNeverBelowTheFloor()
    {
        new ContainerResources { Cpus = 1, Memory = "1g", AllowBelowMinimum = true }.Problems().Should().BeEmpty();
        new ContainerResources { Cpus = 1, Memory = "1g", AllowBelowMinimum = true }.BelowMinimum.Should().BeTrue();
        new ContainerResources { Cpus = 0.5m, AllowBelowMinimum = true }.Problems().Should().ContainSingle().Which.Should().Contain("never given less than 1 CPU");
        new ContainerResources { Memory = "512m", AllowBelowMinimum = true }.Problems().Should().ContainSingle().Which.Should().Contain("never given less than 1g");
    }

    [TestMethod]
    public void TheAnswersFile_TakesTheLimits_AndRefusesThemBelowTheMinimum()
    {
        var answers = InstallAnswers.Parse("""
            { "schema": 1, "roles": ["controller"],
              "controller": { "resources": { "cpus": 3.5, "memory": "6144M", "pidsLimit": 1024 } } }
            """);
        answers.Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3.5m, Memory = "6144m", PidsLimit = 1024 });

        var below = () => InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "memory": "2g" } } }""");
        below.Should().Throw<InstallerUsageException>().WithMessage("*below the minimum of 2 CPUs and 4g*allowBelowMinimum*");

        InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "memory": "2g", "allowBelowMinimum": true } } }""")
            .Controller!.Resources.Memory.Should().Be("2g");

        var misspelt = () => InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "memoryLimit": "4g" } } }""");
        misspelt.Should().Throw<InstallerUsageException>("a member the installer does not know is an error");
    }

    [TestMethod]
    public void ARecordFromBeforeTheLimits_GetsTheDefaults()
    {
        var record = InstallRecord.Parse("""
            { "schema": 1, "scope": "system", "roles": ["controller"], "hat": "real", "version": "4.0.0", "installerVersion": "4.0.0",
              "controller": { "connection": "private-ca" } }
            """);

        record.ToAnswers().Controller!.Resources.Should().Be(new ContainerResources());
    }

    [TestMethod]
    public async Task ThePi_IsDeployedWithTheDefaultLimits_AndTheRecordKeepsThem()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("2", "4g", "512", "false"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources());
        pi.Read(InstallPaths.SystemLog).Should().Contain("enforces memory limits yes, CPU yes, processes yes, swap yes; reports 4 CPUs, 8192m of memory");
    }

    [TestMethod]
    public async Task TheAnswersLimits_GoToTheDeployScript_AndAChangeRedeploysTheController()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3.5m, Memory = "6144m", PidsLimit = 1024 }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("3.5", "6144m", "1024", "false"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3.5m, Memory = "6144m", PidsLimit = 1024 });

        var same = await pi.RunAsync("--answers", answers);
        same.ExitCode.Should().Be(0, same.ToString());
        same.Output.Should().Contain("Nothing to change");
        pi.Deploys.Should().ContainSingle();

        var changed = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 1024 }), "changed.json");
        var plan = await pi.RunAsync("--plan", "--answers", changed);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("redeployed with its limits: 3 CPUs, 6g of memory and no swap, at most 1024 processes and threads");
        pi.Deploys.Should().ContainSingle("a plan changes nothing");

        var rerun = await pi.RunAsync("--answers", changed);
        rerun.ExitCode.Should().Be(0, rerun.ToString());
        pi.Deploys.Should().HaveCount(2);
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024", "false"));
        Record(pi).Controller!.Resources.Memory.Should().Be("6g");
    }

    [TestMethod]
    public async Task AControllerDeployedBeforeTheLimits_IsRedeployedWithThem()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);

        // The same release, as a deploy script from before the limits ran it.
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 0, Memory = 0, PidsLimit = null, ReadOnly = false });
        var plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with its limits: 2 CPUs, 4g of memory and no swap, at most 512 processes and threads");

        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 2_000_000_000, Memory = 4L * 1024 * 1024 * 1024, PidsLimit = 512, ReadOnly = false });
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with the container's privileges narrowed (a read-only root filesystem and only the supervisor's capabilities)");
    }

    [TestMethod]
    public async Task TheCommandLine_GivesTheLimits_InPlaceOfTheAnswers()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3 }));

        var run = await pi.RunAsync("--answers", answers, "--memory", "6G", "--pids-limit", "2048");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("3", "6g", "2048", "false"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 2048 });

        var below = await pi.RunAsync("--answers", answers, "--cpus", "1.5");
        below.ExitCode.Should().Be((int)InstallerExitCode.Usage, below.ToString());
        below.Error.Should().Contain("below the minimum of 2 CPUs and 4g").And.Contain("--allow-limits-below-minimum");
        pi.Deploys.Should().ContainSingle("nothing is deployed with limits the installer refuses");

        var acknowledged = await pi.RunAsync("--answers", answers, "--cpus", "1.5", "--allow-limits-below-minimum");
        acknowledged.ExitCode.Should().Be(0, acknowledged.ToString());
        pi.Deploys[^1].Should().Contain(Limits("1.5", "4g", "512", "true"));
        Record(pi).Controller!.Resources.AllowBelowMinimum.Should().BeTrue("the acknowledgment is recorded, so an upgrade keeps the limits");

        var floor = await pi.RunAsync("--answers", answers, "--memory", "512m", "--allow-limits-below-minimum");
        floor.ExitCode.Should().Be((int)InstallerExitCode.Usage, floor.ToString());
        floor.Error.Should().Contain("never given less than 1g of memory");
    }

    [TestMethod]
    public async Task TheLimitFlags_NeedTheController()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "olga");
        var answers = laptop.WriteAnswers(new InstallAnswers
        {
            Roles = [InstallRole.Cli],
            Client = new ClientSettings { Controller = "https://roof.local:8443", CaSha256 = new string('a', 64) }
        });

        var run = await laptop.RunAsync("--answers", answers, "--cpus", "3");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("they need the controller or a test rig among the roles");
    }

    [TestMethod]
    [DataRow("false,true,true,true,4,8589934592", "does not enforce memory limits (docker info: MemoryLimit false, CPUCfsQuota true, PidsLimit true)")]
    [DataRow("true,false,true,true,4,8589934592", "does not enforce CPU limits")]
    [DataRow("true,true,false,true,4,8589934592", "does not enforce process limits")]
    [DataRow(",true,true,true,4,8589934592", "does not enforce memory limits (docker info: MemoryLimit not reported")]
    public async Task DockerThatCannotEnforceTheLimits_IsRefused_AndNothingChanges(string info, string problem)
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = info;
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain($"Docker here {problem}").And.Contain("add cgroup_enable=memory to the single line in /boot/firmware/cmdline.txt and reboot");
        pi.Deploys.Should().BeEmpty();
        pi.Exists(InstallPaths.SystemRecord).Should().BeFalse();
    }

    [TestMethod]
    public async Task DockerThatSaysNothingOfItsLimits_IsRefused()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = null;
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("Docker did not say which limits it enforces (docker info)");
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task LimitsBeyondTheMachine_AreRefused_AndSmallerOnesNeedTheAcknowledgment()
    {
        // A Pi with 4 GB: a little less than 4 GiB is left for programs.
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = "true,true,true,true,4,4045848576";
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("The controller's memory limit of 4g is more than the 3858m Docker reports here: give controller.resources.memory (or --memory) at most that (less than 4g needs controller.resources.allowBelowMinimum).");
        pi.Deploys.Should().BeEmpty();

        var smaller = await pi.RunAsync("--answers", answers, "--memory", "3g", "--allow-limits-below-minimum");
        smaller.ExitCode.Should().Be(0, smaller.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("2", "3g", "512", "true"));

        pi.DockerInfo = "true,true,true,true,2,8589934592";
        var cpus = await pi.RunAsync("--plan", "--answers", answers, "--cpus", "3");
        cpus.ExitCode.Should().Be((int)InstallerExitCode.Refused, cpus.ToString());
        cpus.Error.Should().Contain("The controller's limit of 3 CPUs is more than the 2 Docker reports here");
    }

    [TestMethod]
    public async Task ARig_GetsTheLimitsToo()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var answers = bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] });

        var run = await bench.RunAsync("--answers", answers, "--cpus", "4");

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("4", "4g", "512", "false"));
    }

    [TestMethod]
    public async Task AnUpgradeAndARollback_KeepTheRecordedLimits()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 1024 }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);

        var upgrade = await pi.RunAsync("upgrade");

        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        pi.Deploys.Should().HaveCount(2);
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024", "false"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 1024 });

        pi.InstallerVersion = NewVersion;
        var rollback = await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024", "false"), "the deploy script gives the version it brings back the recorded limits");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.NanoCpus == 3_000_000_000 && running.Memory == 6L * 1024 * 1024 * 1024);
        Record(pi).Controller!.Resources.Memory.Should().Be("6g");
    }

    [TestMethod]
    public void TheLimitProblems_NameWhatDockerSaid()
    {
        var limits = new ContainerResources();
        RoleGuards.ContainerLimitProblems(new DockerLimits(true, true, true, false, 4, 8L << 30), limits).Should().BeEmpty("no swap limit only warns: the deploy script says so");
        RoleGuards.ContainerLimitProblems(new DockerLimits(true, true, true, true, 2, 4L << 30), limits).Should().BeEmpty("the limits may take all of the machine");
        RoleGuards.ContainerLimitProblems(new DockerLimits(false, false, null, true, 1, 1L << 30), limits).Should().SatisfyRespectively(
            problem => problem.Should().StartWith("Docker here does not enforce memory, CPU, process limits (docker info: MemoryLimit false, CPUCfsQuota false, PidsLimit not reported)"),
            problem => problem.Should().StartWith("The controller's limit of 2 CPUs is more than the 1 Docker reports here"),
            problem => problem.Should().StartWith("The controller's memory limit of 4g is more than the 1024m Docker reports here"));
    }

    private static InstallAnswers Controller(ContainerResources resources)
        => new() { Roles = [InstallRole.Controller], Controller = new ControllerSettings { Resources = resources } };

    private static Dictionary<string, string> Limits(string cpus, string memory, string pids, string belowMinimum) => new()
    {
        ["CONTAINER_CPUS"] = cpus,
        ["CONTAINER_MEMORY"] = memory,
        ["CONTAINER_PIDS_LIMIT"] = pids,
        ["ALLOW_LIMITS_BELOW_MINIMUM"] = belowMinimum
    };

    private static InstallRecord Record(FakeMachine machine) => InstallRecord.Parse(machine.Read(InstallPaths.SystemRecord));
}
