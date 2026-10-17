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
/// The controller container's resource limits (#129, #149; docs/deployment.md, "The container's privileges and
/// limits"): always a CPU, a memory and a process limit, RoofController's default of 1 vCPU, 2 GiB and 512 processes for
/// each one the answers or the command line do not give, refused below the RoofController floor of 1 vCPU and 1 GiB with
/// no exception, kept in the install record (one not set follows the default), given to the deploy script by every
/// deploy, upgrade and rollback, and refused before anything changes on a machine whose Docker cannot enforce them or
/// has fewer CPUs than the CPU limit (a memory limit above its memory only warns), with no override.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerResourceLimitsTests
{
    private const string NewVersion = "4.0.1";

    // What a stock Raspberry Pi OS (a Pi 5 on Debian 13, kernel 6.18 rpi-2712) reports of its Docker: the firmware adds
    // cgroup_disable=memory to the kernel's command line.
    private const string StockPiInfo = "false,true,true,false,4,8589934592,2,roofpi\nWARNING: No memory limit support\nWARNING: No swap limit support\nWARNING: bridge-nf-call-iptables is disabled";

    [TestMethod]
    public void TheDefaults_AreRoofControllers_OneCpu_TwoGiB_And512Processes()
    {
        var resources = new ContainerResources();

        resources.Should().Be(new ContainerResources { Cpus = null, Memory = null, PidsLimit = 512 }, "a limit not set is recorded as none, and follows the default");
        resources.Problems().Should().BeEmpty();
        resources.NanoCpus.Should().Be(1_000_000_000);
        resources.MemoryBytes.Should().Be(2L * 1024 * 1024 * 1024);
        resources.CpusText.Should().Be("1");
        resources.MemoryOrDefault.Should().Be("2g");
        resources.Describe().Should().Be("1 CPU, 2g of memory and no swap, at most 512 processes and threads");
    }

    [TestMethod]
    public void TheLimits_AreWrittenPlainly_AsTheDeployScriptWritesThem()
    {
        var resources = new ContainerResources { Cpus = 2.50m, Memory = "06144M", PidsLimit = 1024 }.Normalised();

        resources.CpusText.Should().Be("2.5");
        resources.Memory.Should().Be("6144m");
        resources.MemoryBytes.Should().Be(6144L * 1024 * 1024);
        resources.NanoCpus.Should().Be(2_500_000_000);
        resources.Describe().Should().Be("2.5 CPUs, 6144m of memory and no swap, at most 1024 processes and threads");
        new ContainerResources { Memory = "5G" }.Normalised().Memory.Should().Be("5g");
        new ContainerResources { Memory = " " }.Normalised().Memory.Should().BeNull("an empty memory limit is the default");
        new ContainerResources { Cpus = 2 }.Describe().Should().Be("2 CPUs, 2g of memory and no swap, at most 512 processes and threads");
        new ContainerResources { Memory = "4g" }.Describe().Should().Be("1 CPU, 4g of memory and no swap, at most 512 processes and threads");
    }

    [TestMethod]
    [DataRow("0", null, 512, "must be a number such as 2 or 2.5")]
    [DataRow("2.555", null, 512, "with at most two decimals, not 2.555")]
    [DataRow("1000", null, 512, "must be a number such as 2 or 2.5")]
    [DataRow(null, "4", 512, "must be a whole number of MiB or GiB, such as 1024m or 1g, not '4'")]
    [DataRow(null, "4.5g", 512, "not '4.5g'")]
    [DataRow(null, "4gb", 512, "not '4gb'")]
    [DataRow(null, null, 255, "must be from 256 to 32768, not 255")]
    [DataRow(null, null, 32769, "must be from 256 to 32768, not 32769")]
    [DataRow("0.99", null, 512, "The controller's CPU limit of 0.99 (controller.resources.cpus, or --cpus) is less than 1 vCPU, the minimum (RoofController's): give at least 1 (the default is 1).")]
    [DataRow("0.9", "2g", 512, "CPU limit of 0.9 (controller.resources.cpus, or --cpus) is less than 1 vCPU")]
    [DataRow("0.5", null, 512, "CPU limit of 0.5 (controller.resources.cpus, or --cpus) is less than 1 vCPU")]
    [DataRow(null, "1023m", 512, "The controller's memory limit of 1023m (controller.resources.memory, or --memory) is less than 1g, the minimum (RoofController's): give at least 1g (the default is 2g).")]
    [DataRow(null, "512m", 512, "memory limit of 512m (controller.resources.memory, or --memory) is less than 1g")]
    public void LimitsTheControllerIsNotGiven_AreProblems(string? cpus, string? memory, int pids, string problem)
    {
        new ContainerResources { Cpus = Cpus(cpus), Memory = memory, PidsLimit = pids }.Problems()
            .Should().ContainSingle().Which.Should().Contain(problem);
    }

    [TestMethod]
    public void TheMinimum_IsTaken_AndMoreThanTheDefault()
    {
        new ContainerResources { Cpus = 1, Memory = "1g" }.Problems().Should().BeEmpty();
        new ContainerResources { Cpus = 1, Memory = "1024m" }.Problems().Should().BeEmpty();
        new ContainerResources { Cpus = 1.01m }.Problems().Should().BeEmpty();
        new ContainerResources { Cpus = 4, Memory = "8g", PidsLimit = 4096 }.Problems().Should().BeEmpty();
    }

    [TestMethod]
    public void TheAnswersFile_TakesTheLimits_AndRefusesThemBelowTheMinimum_WithNoAcknowledgment()
    {
        var answers = InstallAnswers.Parse("""
            { "schema": 1, "roles": ["controller"],
              "controller": { "resources": { "cpus": 3.5, "memory": "6144M", "pidsLimit": 1024 } } }
            """);
        answers.Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3.5m, Memory = "6144m", PidsLimit = 1024 });

        var below = () => InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "memory": "1023m" } } }""");
        below.Should().Throw<InstallerUsageException>().WithMessage("*memory limit of 1023m*is less than 1g, the minimum*");

        // An earlier draft's acknowledgment is not a member the installer knows: the file is refused, not taken below the minimum.
        var acknowledged = () => InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "memory": "1023m", "allowBelowMinimum": true } } }""");
        acknowledged.Should().Throw<InstallerUsageException>();
        var acknowledgedAtTheMinimum = () => InstallAnswers.Parse("""{ "schema": 1, "roles": ["controller"], "controller": { "resources": { "allowBelowMinimum": false } } }""");
        acknowledgedAtTheMinimum.Should().Throw<InstallerUsageException>();

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
        record.ToAnswers().Controller!.Resources.Describe().Should().Be("1 CPU, 2g of memory and no swap, at most 512 processes and threads");
    }

    [TestMethod]
    [DataRow("1g")]
    [DataRow("1024m")]
    public async Task TheRoofControllerFloor_IsAccepted_InAnswersAndFlags(string memory)
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = "true,true,true,true,1,1073741824,2,docker-host";
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 1, Memory = memory }));
        var run = await pi.RunAsync("--answers", answers, "--cpus", "1", "--memory", memory);
        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("1", memory, "512"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 1, Memory = memory });
    }

    [TestMethod]
    public async Task ThePi_IsDeployedWithTheDefaultLimits_AndTheRecordKeepsOnlyTheProcessLimit()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("", "", "512"), "the deploy script resolves the ones not given, to the default here");
        pi.Deploys.Single().Should().NotContainKey("ALLOW_LIMITS_BELOW_MINIMUM");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.NanoCpus == 1_000_000_000 && running.Memory == 2L * 1024 * 1024 * 1024 && running.PidsLimit == 512);
        // The default is not recorded as a choice: an upgrade gives the installer's default then.
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources());
        pi.Read(InstallPaths.SystemRecord).Should().NotContain("\"cpus\"").And.NotContain("\"memory\"");
        pi.Read(InstallPaths.SystemLog).Should().Contain("enforces memory limits yes, CPU yes, processes yes, swap yes; reports 4 CPUs, 8192m of memory");
    }

    [TestMethod]
    [DataRow("false,true,true,true,4,8589934592,2,docker-host", "docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit true, so Docker would run the controller without its memory limit (2g, and no swap beyond it).", "")]
    [DataRow("true,true,true,false,4,8589934592,2,docker-host", "docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit false, so Docker would let the controller swap beyond its memory limit (2g).", "")]
    [DataRow(",true,true,true,4,8589934592,2,docker-host", "docker-memory-limit-unsupported: docker info reports MemoryLimit not reported and SwapLimit true", "")]
    [DataRow("true,false,true,true,4,8589934592,2,docker-host", "docker-cpu-quota-unsupported: docker info reports CPUCfsQuota false, so Docker would run the controller without its CPU limit (1).", "")]
    [DataRow("true,<no value>,true,true,4,8589934592,2,docker-host", "docker-cpu-quota-unsupported: docker info reports CPUCfsQuota not reported", "")]
    [DataRow("true,true,false,true,4,8589934592,2,docker-host", "docker-pids-limit-unsupported: docker info reports PidsLimit false, so Docker would run the controller without its process limit (512).", "")]
    [DataRow("false,false,false,false,4,8589934592,,docker-host", "docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit false", "docker-cpu-quota-unsupported: docker info reports CPUCfsQuota false|docker-pids-limit-unsupported: docker info reports PidsLimit false")]
    public async Task DockerThatCannotEnforceTheLimits_IsRefused_AndNothingChanges(string info, string finding, string more)
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = info;
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("Docker here cannot give the controller its limits (1 CPU, 2g of memory and no swap, at most 512 processes and threads), so the install is refused. Every finding:");
        foreach (var expected in new[] { finding }.Concat(more.Split('|', StringSplitOptions.RemoveEmptyEntries)))
        {
            run.Error.Should().Contain("  - " + expected);
        }

        foreach (var code in new[] { "docker-memory-limit-unsupported", "docker-swap-limit-unsupported", "docker-cpu-quota-unsupported", "docker-pids-limit-unsupported" })
        {
            if (!(finding + more).Contains(code, StringComparison.Ordinal))
            {
                run.Error.Should().NotContain(code);
            }
        }

        run.Error.Should().Contain(info.Contains(",,docker-host", StringComparison.Ordinal) ? "Docker's host: cgroup version not reported;" : "Docker's host: cgroup v2;");
        run.Error.Should().Contain("/sys/fs/cgroup/cgroup.controllers on Docker's machine lists the kernel's cgroup v2 controllers (Docker needs memory, cpu and pids)", "Docker names another machine: this one's file is not its host's");
        run.Error.Should().Contain(RoleGuards.ContainerLimitRemedy);
        run.Error.Should().Contain("Append \" cgroup_enable=memory\" to the end of the single line in /boot/firmware/cmdline.txt");
        run.Error.Should().NotContain("change cgroup_disable=memory").And.NotContain("no memory limit");
        pi.Deploys.Should().BeEmpty();
        pi.Exists(InstallPaths.SystemRecord).Should().BeFalse();
    }

    [TestMethod]
    public async Task AStockPi_IsRefused_QuotingItsCgroupControllersAndDockersWarnings()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = StockPiInfo;
        pi.Write(MachineSurveyor.CgroupControllersFile, "cpuset cpu io pids\n");
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        foreach (var plan in new[] { true, false })
        {
            var run = plan ? await pi.RunAsync("--plan", "--answers", answers) : await pi.RunAsync("--answers", answers);

            run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
            run.Error.Should().Contain("  - docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit false");
            run.Error.Should().Contain("  Docker's host: cgroup v2; /sys/fs/cgroup/cgroup.controllers: \"cpuset cpu io pids\"; Docker warns \"WARNING: No memory limit support\"; Docker warns \"WARNING: No swap limit support\".");
            run.Error.Should().NotContain("bridge-nf-call-iptables");
            run.Error.Should().Contain(RoleGuards.ContainerLimitRemedy + " Then run the installer again.");
        }

        pi.Deploys.Should().BeEmpty();
        pi.Exists(InstallPaths.SystemRecord).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("false,true,true,false,4,8589934592,2,docker-host", "memory")]
    [DataRow("true,true,true,false,4,8589934592,2,docker-host", "swap")]
    [DataRow("true,false,true,true,4,8589934592,2,docker-host", "cpu")]
    [DataRow("false,false,true,false,4,8589934592,2,docker-host", "memory cpu")]
    [DataRow("true,true,false,true,4,8589934592,2,docker-host", "pids")]
    [DataRow("false,false,false,false,4,8589934592,2,docker-host", "memory cpu pids")]
    public async Task OnDockerThatNoLongerEnforcesTheLimits_AnUpgradeIsRefused_AndARollbackGoesAheadWithALoudWarning(string info, string unenforced)
    {
        // Every deploy that gives the limits checks them, not the first install alone (SkyMonitor checks only at install).
        // A rollback is a recovery path (the owner's decision on #149): it goes ahead, and warns of each limit.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 2, Memory = "3g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);
        var record = pi.Read(InstallPaths.SystemRecord);
        pi.DockerInfo = info;

        var refused = await pi.RunAsync("upgrade");

        refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("Docker here cannot give the controller its limits (2 CPUs, 3g of memory and no swap, at most 512 processes and threads), so the install is refused.");
        pi.Deploys.Should().ContainSingle("the upgrade deployed nothing");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);

        pi.DockerInfo = "true,true,true,true,4,8589934592,2,docker-host";
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        pi.DockerInfo = info;

        var rollback = await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.Deploys[^1].Should().Contain(Limits("2", "3g", "512"), "the deploy script is given the recorded limits, and gives the ones Docker can enforce");
        Record(pi).Version.Should().Be("4.0.0", "the rollback went ahead");
        rollback.Error.Should().Contain("Warning: THE ROLLBACK GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE THESE LIMITS, so the version it brings back runs WITHOUT them. A rollback is a recovery path, so it is not refused (#149); an install, an upgrade or a deploy is refused here until this is fixed:");
        foreach (var (limit, code) in new[] { ("memory", "docker-memory-limit-unsupported"), ("swap", "docker-swap-limit-unsupported"), ("cpu", "docker-cpu-quota-unsupported"), ("pids", "docker-pids-limit-unsupported") })
        {
            if (unenforced.Split(' ').Contains(limit))
            {
                rollback.Error.Should().Contain($"  - {code}: docker info reports");
            }
            else
            {
                rollback.Error.Should().NotContain(code);
            }
        }

        rollback.Error.Should().Contain("so the version the rollback brings back ");
        rollback.Error.Should().Contain(RoleGuards.ContainerLimitRemedy + " Then upgrade again.");
        rollback.Error.Should().NotContain("is refused. Every finding");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Warning: THE ROLLBACK GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE THESE LIMITS");
    }

    [TestMethod]
    [DataRow("false,true,true,false,4,8589934592,2,docker-host", "docker-memory-limit-unsupported")]
    [DataRow("true,true,true,false,4,8589934592,2,docker-host", "docker-swap-limit-unsupported")]
    [DataRow("true,false,true,true,4,8589934592,2,docker-host", "docker-cpu-quota-unsupported")]
    [DataRow("true,true,false,true,4,8589934592,2,docker-host", "docker-pids-limit-unsupported")]
    public async Task AnUpgradeToAnotherRelease_OnDockerThatCannotEnforceTheLimits_IsRefusedBeforeTheHandOver_AndNothingChanges(string info, string finding)
    {
        // Review ROOF-151-R1 F4: refused before the hand-over's first change, its backup and the installer it puts in
        // place, not by the release's installer once they are done.
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.WithRelease(NewVersion, latest: true);
        var installer = File.ReadAllBytes(pi.OnDisk(InstallPaths.SystemInstaller));
        var record = pi.Read(InstallPaths.SystemRecord);
        var backups = Backups(pi);
        var deploys = pi.Deploys.Count;
        pi.DockerInfo = info;

        var upgrade = await pi.RunAsync("upgrade");

        upgrade.ExitCode.Should().Be((int)InstallerExitCode.Refused, upgrade.ToString());
        upgrade.Error.Should().Contain("so the install is refused. Every finding:").And.Contain($"  - {finding}: docker info reports")
            .And.Contain("Nothing was changed: the installer, the backups and the record are as they were.");
        upgrade.Output.Should().NotContain("Handing over").And.NotContain("backed up", "no backup was taken");
        pi.Launches.Should().BeEmpty("the release's installer was not handed over to");
        File.ReadAllBytes(pi.OnDisk(InstallPaths.SystemInstaller)).Should().Equal(installer, "the installer is the one installed");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
        Backups(pi).Should().Equal(backups, "no backup was taken");
        pi.Deploys.Should().HaveCount(deploys);

        // Its plan says the same, and changes nothing either.
        var plan = await pi.RunAsync("upgrade", "--plan");
        plan.ExitCode.Should().Be((int)InstallerExitCode.Refused, plan.ToString());
        File.ReadAllBytes(pi.OnDisk(InstallPaths.SystemInstaller)).Should().Equal(installer);
        Backups(pi).Should().Equal(backups);
    }

    [TestMethod]
    public async Task ASetting_PersistsThroughARollbackOnDockerThatCannotEnforceIt_AndTheUpgradeAfterIt()
    {
        // Review ROOF-151-R1 F2, as the installer runs it: the record keeps the operator's limits whether or not the host
        // could enforce them, and every deploy is given them.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 1.5m, Memory = "6g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;

        pi.DockerInfo = "false,true,true,false,4,8589934592,2,docker-host";
        var rollback = await pi.RunAsync("rollback");
        rollback.ExitCode.Should().Be(0, rollback.ToString());
        rollback.Error.Should().Contain("docker-memory-limit-unsupported");
        pi.Deploys[^1].Should().Contain(Limits("1.5", "6g", "512"), "the rollback is given the operator's limits, which the deploy script keeps as the desired ones");
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 1.5m, Memory = "6g" });

        // The host fixed: the next upgrade gives the operator's limits, not the default.
        pi.DockerInfo = "true,true,true,true,4,8589934592,2,docker-host";
        pi.InstallerVersion = "4.0.0";
        var upgrade = await pi.RunAsync("upgrade");
        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        pi.Deploys[^1].Should().Contain(Limits("1.5", "6g", "512"));
        upgrade.Error.Should().NotContain("Warning: The controller's", "the deploy script's record keeps the install record's");
    }

    [TestMethod]
    public async Task TheLimits_ResolveAsTheDeployScriptResolvesThem_GivenThenTheRecordThenTheRunningControllerThenTheDefault()
    {
        // Review ROOF-151-R2 F5: one store and one precedence everywhere. The installer gives the script only the limits
        // given, and says in its plan the ones the script will resolve.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources()));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.Deploys[^1].Should().Contain(Limits("", "", "512"), "the installer gives only the limits given");
        pi.NewestDesiredLimits!["io.hvo.roof.limits.cpus-source"].Should().Be("default");
        pi.NewestDesiredLimits!["io.hvo.roof.limits.memory-source"].Should().Be("default");
        pi.DesiredLimitsRecord.Should().Be("1,2g");

        // Someone ran the deploy script by hand with 1.5 CPUs and 6g: the record has them as set, and the answers give
        // none, so they are kept.
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 1_500_000_000, Memory = 6L << 30 });
        pi.SeedDesiredLimits("1.5", "6g", generation: 2);
        var plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().MatchRegex(@"unchanged +roof-controller ").And.NotContain("redeployed with its limits");
        plan.Error.Should().NotContain("Warning:", "a limit kept from the record is no warning");

        // Answers that give 2g: the one given comes first, with a warning that names the record and says which wins.
        var given = pi.WriteAnswers(Controller(new ContainerResources { Memory = "2g" }), "given.json");
        plan = await pi.RunAsync("--plan", "--answers", given);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("redeployed with its limits: 1.5 CPUs, 2g of memory and no swap, at most 512 processes and threads (the CPUs kept from the last deploy)");
        plan.Error.Should().Contain($"Warning: The controller's memory limit is given as 2g (controller.resources.memory, or --memory), and the deploy script's last record, roof-controller-limits-g2 (written by a deploy at 2026-10-17T00:00:00Z for container {pi.Containers[MachineSurveyor.ControllerContainer].Id[..12]}), keeps 6g: the one given comes first, and the deploy records it in its place.");
        var run = await pi.RunAsync("--answers", given);
        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys[^1].Should().Contain(Limits("", "2g", "512"));
        pi.Containers[MachineSurveyor.ControllerContainer].Memory.Should().Be(2L << 30);
        pi.Containers[MachineSurveyor.ControllerContainer].NanoCpus.Should().Be(1_500_000_000);
        pi.DesiredLimitsRecord.Should().Be("1.5,2g");
        pi.DesiredLimitRecords.Should().Equal("roof-controller-limits-g3");

        // A record that keeps the default gives the default, not the running controller's limit.
        pi.Volumes.Clear();
        answers = pi.WriteAnswers(Controller(new ContainerResources()), "none.json");
        pi.SeedDesiredLimits("1", "2g", 4, null, ("cpus-source", "default"), ("memory-source", "default"));
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 2_000_000_000, Memory = 4L << 30 });
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 2g of memory and no swap, at most 512 processes and threads");

        // No record (a controller deployed before them): the running controller's.
        pi.Volumes.Clear();
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().MatchRegex(@"unchanged +roof-controller ").And.NotContain("redeployed with its limits");
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine, CancellationToken.None);
        var resolved = ResolvedResources.Resolve(new ContainerResources(), survey);
        resolved.Describe().Should().Be("2 CPUs, 4g of memory and no swap, at most 512 processes and threads (the CPUs kept from roof-controller, the memory kept from roof-controller)");

        // Neither: the default.
        pi.Containers.Remove(MachineSurveyor.ControllerContainer);
        survey = await MachineSurveyor.SurveyAsync(pi.Machine, CancellationToken.None);
        resolved = ResolvedResources.Resolve(new ContainerResources(), survey);
        (resolved.CpusSource, resolved.MemorySource, resolved.Resources.Describe()).Should().Be(("the default", "the default", "1 CPU, 2g of memory and no swap, at most 512 processes and threads"));
    }

    [TestMethod]
    public async Task ARecordWrittenForAnotherContainer_StillComesFirst_WithAWarning()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.SeedDesiredLimits("1", "6g", 2, "abcdefabcdef");
        var running = pi.Containers[MachineSurveyor.ControllerContainer].Id[..12];

        var plan = await pi.RunAsync("--plan", "--answers", answers);

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 6g of memory and no swap, at most 512 processes and threads (the CPUs kept from the last deploy, the memory kept from the last deploy)");
        plan.Error.Should().Contain($"Warning: The desired limits' record, roof-controller-limits-g2, was written for container abcdefabcdef, not for the running roof-controller ({running}): a limit not given still takes the record's value, not the running controller's.");
    }

    [TestMethod]
    [DataRow("memory", "512m", "its memory '512m' is less than 1g, the minimum")]
    [DataRow("cpus", "0.5", "its CPUs '0.5' are less than 1 vCPU, the minimum")]
    [DataRow("cpus", "1.555", "its CPUs '1.555' are not a number of CPUs with at most two decimals")]
    [DataRow("memory", "2gb", "its memory '2gb' is not a whole number of MiB or GiB")]
    [DataRow("", "2", "its format (io.hvo.roof.limits) is '2', not 1")]
    [DataRow("generation", "7", "its generation label '7' is not the 2 of its name")]
    [DataRow("memory-source", "operator", "its sources 'set' and 'operator' are not set or default")]
    [DataRow("written-by", null, "it does not say which run wrote it, and for which container ('', ")]
    public async Task ARecordThatIsNotValid_IsNeverApplied_RefusedWhenALimitIsNeeded_AndWarnedWhenBothAreGiven(string label, string? value, string problem)
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.SeedDesiredLimits("1", "6g", 2, null, (label, value));
        pi.WithRelease(NewVersion, latest: true);
        var deploys = pi.Deploys.Count;
        var record = pi.Read(InstallPaths.SystemRecord);
        var installer = File.ReadAllBytes(pi.OnDisk(InstallPaths.SystemInstaller));

        foreach (var arguments in new[] { new[] { "--plan", "--answers", answers }, ["--answers", answers], ["upgrade"] })
        {
            var refused = await pi.RunAsync(arguments);
            refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
            refused.Error.Should().Contain($"The newest desired-limits record, roof-controller-limits-g2, is not valid: {problem}");
            refused.Error.Should().Contain("It is not applied, and nothing was changed: give controller.resources.cpus and controller.resources.memory (or --cpus and --memory; the deploy then records them in its place), or remove it (docker volume rm roof-controller-limits-g2).");
        }

        pi.Deploys.Should().HaveCount(deploys, "nothing was deployed");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
        File.ReadAllBytes(pi.OnDisk(InstallPaths.SystemInstaller)).Should().Equal(installer, "the upgrade did not hand over");
        Backups(pi).Should().BeEmpty();

        // Both given: a warning, and the deploy records them in its place.
        var both = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 2, Memory = "3g" }), "both.json");
        var run = await pi.RunAsync("--answers", both);
        run.ExitCode.Should().Be(0, run.ToString());
        run.Error.Should().Contain($"Warning: The newest desired-limits record, roof-controller-limits-g2, is not valid ({problem}");
        run.Error.Should().Contain("): it is not applied, and the deploy records the limits given in its place.");
        pi.DesiredLimitRecords.Should().Equal("roof-controller-limits-g3");
        pi.DesiredLimitsRecord.Should().Be("2,3g");
    }

    [TestMethod]
    public async Task RecordsThatCannotBeRead_RefuseBeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.VolumeListFails = true;
        var deploys = pi.Deploys.Count;

        var refused = await pi.RunAsync("--answers", answers);

        refused.ExitCode.Should().Be((int)InstallerExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("Could not read the desired limits' records from Docker (volumes roof-controller-limits-g<generation>; a limit not given keeps them): docker volume ls failed: Error response from daemon: volume ls failed. Nothing was changed; check Docker and run it again.");
        pi.Deploys.Should().HaveCount(deploys);
    }

    [TestMethod]
    public void TheRecords_AreReadAsTheDeployScriptReadsThem()
    {
        var names = new[] { "roof-controller-limits", "roof-controller-limits-g2", "roof-controller-limits-g10", "roof-controller-limits-gx", "roof-controller-limits-g01", "other-roof-controller-limits-g99", "" };
        var (volumes, newest, generation) = DesiredLimits.Records("roof-controller", names);
        volumes.Should().Equal("roof-controller-limits", "roof-controller-limits-g2", "roof-controller-limits-g10");
        (newest, generation).Should().Be(("roof-controller-limits-g10", 10));
        DesiredLimits.Records("roof-controller", []).Newest.Should().BeNull();

        var record = DesiredLimits.Parse("roof-controller-limits-g10", 10, "1|10|1.5|set|6g|default|rollback|2026-10-17T01:02:03Z|0123456789ab\n");
        record.IsValid.Should().BeTrue(record.Problem);
        (record.Cpus, record.CpusSource, record.Memory, record.MemorySource, record.WrittenBy, record.Controller)
            .Should().Be(("1.5", "set", "6g", "default", "rollback", "0123456789ab"));
        record.Written.Should().Be("written by a rollback at 2026-10-17T01:02:03Z for container 0123456789ab");
        DesiredLimits.Parse("roof-controller-limits-g1", 1, "|||||||||").Problem.Should().Be("its format (io.hvo.roof.limits) is '', not 1");
        DesiredLimits.Parse("roof-controller-limits-g1", 1, "<no value>|<no value>").Problem.Should().Be("its format (io.hvo.roof.limits) is '', not 1");
        DesiredLimits.Parse("roof-controller-limits-g1", 1, "1|1|1|set|1G|set|deploy|t|0123456789ab|extra").Problem
            .Should().Be("it does not say which run wrote it, and for which container ('deploy', '0123456789ab|extra')", "a field past the last goes with the last, as the script reads it");
        DesiredLimits.Parse("roof-controller-limits-g1", 1, "1|1|1|set|1G|set|deploy|t|0123456789ab").IsValid.Should().BeTrue("1G is 1 GiB, the minimum");
        DesiredLimits.Parse("roof-controller-limits-g1", 1, "1|1|1|set|1023m|set|deploy|t|0123456789ab").Problem.Should().Be("its memory '1023m' is less than 1g, the minimum");
    }

    [TestMethod]
    public async Task TheUninstallAndThePurge_RemoveTheRecords_WhetherOrNotTheContainerIsThere()
    {
        // Review ROOF-151-R2 F6.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Memory = "6g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.Volumes["roof-controller-limits"] = new() { ["io.hvo.roof.desired-memory"] = "6g" };
        pi.Volumes["other-data"] = [];

        // The controller removed by hand: the uninstall removes the records all the same, the earlier format's too.
        pi.Containers.Remove(MachineSurveyor.ControllerContainer);
        pi.Containers.Remove(ControllerRollbackStep.PreviousContainer);
        var plan = await pi.RunAsync("uninstall", "--plan");
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("removed: roof-controller-limits, roof-controller-limits-g1");
        pi.DesiredLimitRecords.Should().HaveCount(2, "a plan changes nothing");
        var uninstall = await pi.RunAsync("uninstall", "--yes");
        uninstall.ExitCode.Should().Be(0, uninstall.ToString());
        pi.DesiredLimitRecords.Should().BeEmpty();
        pi.Volumes.Keys.Should().Equal("other-data");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Removed the desired-limits record roof-controller-limits-g1.");

        // A reinstall starts from the default.
        var fresh = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }, "fresh.json");
        (await pi.RunAsync("--answers", fresh)).ExitCode.Should().Be(0);
        pi.Containers[MachineSurveyor.ControllerContainer].Memory.Should().Be(2L << 30);

        // A record left beside an uninstall (a deploy by hand after it): a purge later removes it.
        (await pi.RunAsync("uninstall", "--yes")).ExitCode.Should().Be(0);
        pi.SeedDesiredLimits("1", "6g", 5);
        var purge = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");
        purge.ExitCode.Should().Be(0, purge.ToString());
        pi.DesiredLimitRecords.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUninstallThatCannotRemoveARecord_Fails_AndRunningItAgainRemovesIt()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Memory = "6g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.VolumeRemoveFails.Add("roof-controller-limits-g1");

        var failed = await pi.RunAsync("uninstall", "--yes");

        failed.ExitCode.Should().NotBe(0, failed.ToString());
        failed.Error.Should().Contain("Could not remove the desired-limits record roof-controller-limits-g1");
        pi.Containers.Should().NotContainKey(MachineSurveyor.ControllerContainer, "the controller went before");
        Record(pi).UninstalledAt.Should().BeNull("the record does not say it is uninstalled until everything is");

        pi.VolumeRemoveFails.Clear();
        var again = await pi.RunAsync("uninstall", "--yes");
        again.ExitCode.Should().Be(0, again.ToString());
        pi.DesiredLimitRecords.Should().BeEmpty();
        Record(pi).UninstalledAt.Should().NotBeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ARollback_SaysTheDesiredLimitsWarnings_AndLogsThem_BeforeItChangesAnything(bool planOnly)
    {
        // Review ROOF-151-R2 F7: the record gives 2g, and the deploy script's record keeps 6g.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Memory = "2g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        pi.SeedDesiredLimits("1", "6g", 9);
        var deploys = pi.Deploys.Count;
        var warning = $"Warning: The controller's memory limit is given as 2g (controller.resources.memory, or --memory), and the deploy script's last record, roof-controller-limits-g9 (written by a deploy at 2026-10-17T00:00:00Z for container {pi.Containers[MachineSurveyor.ControllerContainer].Id[..12]}), keeps 6g: the one given comes first, and the deploy records it in its place.";

        var log = pi.Read(InstallPaths.SystemLog);

        var rollback = planOnly ? await pi.RunAsync("rollback", "--plan") : await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        rollback.Error.Should().Contain(warning);
        if (planOnly)
        {
            // A plan writes nothing, its log included (InstallLog.None): it says the warning.
            rollback.Output.Should().Contain("back to release 4.0.0");
            pi.Read(InstallPaths.SystemLog).Should().Be(log);
            pi.Deploys.Should().HaveCount(deploys, "a plan changes nothing");
            pi.DesiredLimitsRecord.Should().Be("1,6g");
        }
        else
        {
            log = pi.Read(InstallPaths.SystemLog);
            var logged = log.LastIndexOf(warning, StringComparison.Ordinal);
            logged.Should().BeGreaterThan(-1, "the rollback logs it");
            log.IndexOf("--rollback", logged, StringComparison.Ordinal).Should().BeGreaterThan(logged, "the warning comes before the rollback runs the deploy script");
            pi.DeployOptions[^1].Should().Be("--rollback");
            pi.Deploys[^1].Should().Contain(Limits("", "2g", "512"));
            pi.DesiredLimitsRecord.Should().Be("1,2g", "the one given comes first, and the rollback records it");
        }
    }

    [TestMethod]
    public async Task ALimitSetByHand_PersistsThroughADegradedRollback_AndTheUpgradeAfterIt()
    {
        // Review ROOF-151-R2 F5, with nothing in the install record: 6g given to the deploy script by hand, a rollback on
        // a host whose memory controller went away, then the host fixed and an upgrade that gives nothing applies 6g.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { Memory = 6L << 30 });
        pi.SeedDesiredLimits("1", "6g", 7);

        pi.DockerInfo = "false,true,true,false,4,8589934592,2,docker-host";
        var rollback = await pi.RunAsync("rollback");
        rollback.ExitCode.Should().Be(0, rollback.ToString());
        rollback.Error.Should().Contain("docker-memory-limit-unsupported");
        pi.DesiredLimitsRecord.Should().Be("1,6g");
        pi.NewestDesiredLimits!["io.hvo.roof.limits.written-by"].Should().Be("rollback");

        pi.DockerInfo = "true,true,true,true,4,8589934592,2,docker-host";
        pi.InstallerVersion = "4.0.0";
        var upgrade = await pi.RunAsync("upgrade");
        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        pi.Deploys[^1].Should().Contain(Limits("", "", "512"));
        pi.Containers[MachineSurveyor.ControllerContainer].Memory.Should().Be(6L << 30);
        upgrade.Error.Should().NotContain("Warning: The desired limits' record");
    }

    [TestMethod]
    public async Task ARollback_OnDockerThatCannotEnforceTheLimits_IsStillRefused_ForTheCpusItHasAndWhenDockerDoesNotAnswer()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 2, Memory = "3g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.WithRelease(NewVersion, latest: true);
        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.InstallerVersion = NewVersion;
        var record = pi.Read(InstallPaths.SystemRecord);

        // The recorded CPU limit beyond the CPUs Docker reports, with the memory limit unenforced: refused, as before.
        pi.DockerInfo = "false,true,true,false,1,8589934592,2,docker-host";
        var rollback = await pi.RunAsync("rollback");
        rollback.ExitCode.Should().Be((int)InstallerExitCode.Refused, rollback.ToString());
        rollback.Error.Should().Contain("so the rollback is refused. Every finding:");
        rollback.Error.Should().Contain("  - docker-host-cpus-insufficient: docker info reports 1 CPUs, fewer than the CPU limit of 2");
        rollback.Error.Should().NotContain("docker-memory-limit-unsupported", "that one is the rollback's warning, not a refusal");

        pi.DockerInfo = null;
        rollback = await pi.RunAsync("rollback");
        rollback.ExitCode.Should().Be((int)InstallerExitCode.Refused, rollback.ToString());
        rollback.Error.Should().Contain("Docker did not say which limits it enforces (docker info)");

        pi.DeployOptions.Should().NotContain("--rollback");
        pi.Read(InstallPaths.SystemRecord).Should().Be(record);
    }

    [TestMethod]
    public async Task TheAnswersLimits_GoToTheDeployScript_AndAChangeRedeploysTheController()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3.5m, Memory = "6144m", PidsLimit = 1024 }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("3.5", "6144m", "1024"));
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
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024"));
        Record(pi).Controller!.Resources.Memory.Should().Be("6g");

        // Answers that leave them out again keep the last deploy's (review ROOF-151-R2 F5: the desired-limits record comes
        // before the default); giving the default goes back to it.
        var none = pi.WriteAnswers(Controller(new ContainerResources { PidsLimit = 1024 }), "none.json");
        plan = await pi.RunAsync("--plan", "--answers", none);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().NotContain("redeployed with its limits");
        plan.Output.Should().MatchRegex(@"unchanged +roof-controller ");
        var defaults = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 1, Memory = "2g", PidsLimit = 1024 }), "defaults.json");
        plan = await pi.RunAsync("--plan", "--answers", defaults);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 2g of memory and no swap, at most 1024 processes and threads");
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
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 2g of memory and no swap, at most 512 processes and threads");

        // As #137 deployed it, with the process limit alone: the default CPU and memory limits are turned on.
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 0, Memory = 0, PidsLimit = 512, ReadOnly = true });
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 2g of memory and no swap, at most 512 processes and threads");

        // One that an earlier draft deployed with 2 CPUs and 4 GiB: redeployed with the default.
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 2_000_000_000, Memory = 4L * 1024 * 1024 * 1024, PidsLimit = 512, ReadOnly = true });
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with its limits: 1 CPU, 2g of memory and no swap, at most 512 processes and threads");

        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 1_000_000_000, Memory = 2L * 1024 * 1024 * 1024, PidsLimit = 512, ReadOnly = false });
        plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.Output.Should().Contain("redeployed with the container's privileges narrowed (a read-only root filesystem and only the supervisor's capabilities)");
    }

    [TestMethod]
    public async Task TheCommandLine_GivesTheLimits_InPlaceOfTheAnswers_AndNeverBelowTheMinimum()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 3 }));

        var run = await pi.RunAsync("--answers", answers, "--memory", "6G", "--pids-limit", "2048");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("3", "6g", "2048"));
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 2048 });

        foreach (var (option, value, problem) in new[]
        {
            ("--cpus", "0.9", "CPU limit of 0.9 (controller.resources.cpus, or --cpus) is less than 1 vCPU, the minimum"),
            ("--cpus", "0.5", "CPU limit of 0.5 (controller.resources.cpus, or --cpus) is less than 1 vCPU, the minimum"),
            ("--memory", "1023m", "memory limit of 1023m (controller.resources.memory, or --memory) is less than 1g, the minimum"),
            ("--memory", "512m", "memory limit of 512m (controller.resources.memory, or --memory) is less than 1g, the minimum")
        })
        {
            var below = await pi.RunAsync("--answers", answers, option, value);
            below.ExitCode.Should().Be((int)InstallerExitCode.Usage, below.ToString());
            below.Error.Should().Contain(problem);
            below.Error.Should().NotContain("allow-limits-below-minimum").And.NotContain("allowBelowMinimum");
        }

        // The acknowledgment an earlier draft had is no option.
        var acknowledged = await pi.RunAsync("--answers", answers, "--cpus", "1.5", "--allow-limits-below-minimum");
        acknowledged.ExitCode.Should().Be((int)InstallerExitCode.Usage, acknowledged.ToString());
        pi.Deploys.Should().ContainSingle("nothing is deployed with limits the installer refuses");
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 2048 });
    }

    [TestMethod]
    public async Task ARecordBelowTheMinimum_IsRefused_AndNothingChanges()
    {
        // As an earlier draft could record with its acknowledgment: an upgrade refuses it, with nothing changed.
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 2, Memory = "4g" }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        var record = pi.Read(InstallPaths.SystemRecord).Replace("\"memory\": \"4g\"", "\"memory\": \"1023m\"", StringComparison.Ordinal);
        record.Should().Contain("\"memory\": \"1023m\"");
        pi.Write(InstallPaths.SystemRecord, record);
        pi.WithRelease(NewVersion, latest: true);

        var upgrade = await pi.RunAsync("upgrade");

        upgrade.ExitCode.Should().Be((int)InstallerExitCode.Refused, upgrade.ToString());
        upgrade.Error.Should().Contain("memory limit of 1023m (controller.resources.memory, or --memory) is less than 1g, the minimum");
        pi.Deploys.Should().ContainSingle();
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
        run.Error.Should().Contain("--cpus, --memory and --pids-limit set the controller container's limits: they need the controller or a test rig among the roles");
    }

    [TestMethod]
    public async Task DockerThatSaysNothingOfItsLimits_IsRefused()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = null;
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("Docker did not say which limits it enforces (docker info), and the controller runs with a CPU, a memory and a process limit");
        pi.Deploys.Should().BeEmpty();

        // An answer that is not the template's (another Docker, or none): nothing it says is taken.
        pi.DockerInfo = "true,true,true,true,4,8589934592";
        run = await pi.RunAsync("--answers", answers);
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("Docker did not say which limits it enforces (docker info)");
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AMemoryLimitBeyondTheMachine_IsKept_WithAWarning()
    {
        // A Pi with 4 GB: a little less than 4 GiB is left for programs. The limit is a cap, not a reservation.
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = "true,true,true,true,4,4045848576,2,docker-host";
        var answers = pi.WriteAnswers(Controller(new ContainerResources { Cpus = 2, Memory = "4g" }));

        var plan = await pi.RunAsync("--plan", "--answers", answers);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Error.Should().Contain("Warning: The controller's memory limit of 4g is more than the 3858m Docker reports here. It is kept: a memory limit is a cap, not a reservation");

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Error.Should().Contain("Warning: The controller's memory limit of 4g is more than the 3858m Docker reports here.");
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("2", "4g", "512"));
        pi.Read(InstallPaths.SystemLog).Should().Contain("Warning: The controller's memory limit of 4g is more than the 3858m Docker reports here.");

        // The default on a machine with 1 GB: kept as well, and said to be the default.
        using var small = new FakeMachine().WithPi();
        small.DockerInfo = "true,true,true,true,4,948998144,2,docker-host";
        var defaults = small.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        plan = await small.RunAsync("--plan", "--answers", defaults);
        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Error.Should().Contain("Warning: The controller's memory limit of 2g (the default) is more than the 905m Docker reports here. It is kept");
    }

    [TestMethod]
    public async Task ACpuLimitBeyondTheMachine_IsRefused()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerInfo = "true,true,true,true,2,8589934592,2,docker-host";
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var cpus = await pi.RunAsync("--plan", "--answers", answers, "--cpus", "3");
        cpus.ExitCode.Should().Be((int)InstallerExitCode.Refused, cpus.ToString());
        cpus.Error.Should().Contain("  - docker-host-cpus-insufficient: docker info reports 2 CPUs, fewer than the CPU limit of 3: give controller.resources.cpus (or --cpus) from 1 to 2.");
        cpus.Error.Should().NotContain("cgroup_enable=memory", "the remedy is for the limits Docker cannot enforce");

        pi.DockerInfo = "true,true,true,true,1,8589934592,2,docker-host";
        cpus = await pi.RunAsync("--plan", "--answers", answers, "--cpus", "2");
        cpus.ExitCode.Should().Be((int)InstallerExitCode.Refused, cpus.ToString());
        cpus.Error.Should().Contain("  - docker-host-cpus-insufficient: docker info reports 1 CPUs, fewer than the CPU limit of 2: give controller.resources.cpus (or --cpus) from 1 to 1.");

        var none = await pi.RunAsync("--plan", "--answers", answers);
        none.ExitCode.Should().Be(0, "a machine with one CPU runs the controller with the default of one: " + none);
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARig_GetsTheLimitsToo()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var answers = bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] });

        var run = await bench.RunAsync("--answers", answers, "--cpus", "4");

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Deploys.Should().ContainSingle().Which.Should().Contain(Limits("4", "", "512"));
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.NanoCpus == 4_000_000_000 && running.Memory == 2L << 30);
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
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024"), "an override is not reset to the default");
        Record(pi).Controller!.Resources.Should().Be(new ContainerResources { Cpus = 3, Memory = "6g", PidsLimit = 1024 });

        pi.InstallerVersion = NewVersion;
        var rollback = await pi.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.Deploys[^1].Should().Contain(Limits("3", "6g", "1024"), "the deploy script gives the version it brings back the recorded limits");
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.NanoCpus == 3_000_000_000 && running.Memory == 6L * 1024 * 1024 * 1024);
        Record(pi).Controller!.Resources.Memory.Should().Be("6g");
    }

    [TestMethod]
    public async Task AnUpgradeAndARollback_WithTheDefaults_GiveTheDefaultLimits()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        // As a controller the installer deployed before the default was on (#137): the process limit alone.
        pi.WithContainer(MachineSurveyor.ControllerContainer, pi.Containers[MachineSurveyor.ControllerContainer] with { NanoCpus = 0, Memory = 0 });
        pi.WithRelease(NewVersion, latest: true);

        (await pi.RunAsync("upgrade")).ExitCode.Should().Be(0);
        pi.Deploys[^1].Should().Contain(Limits("", "", "512"));
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.NanoCpus == 1_000_000_000 && running.Memory == 2L * 1024 * 1024 * 1024);

        pi.InstallerVersion = NewVersion;
        (await pi.RunAsync("rollback")).ExitCode.Should().Be(0);
        pi.DeployOptions[^1].Should().Be("--rollback");
        pi.Deploys[^1].Should().Contain(Limits("", "", "512"));
        pi.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(running => running.Version == "4.0.0" && running.NanoCpus == 1_000_000_000 && running.Memory == 2L * 1024 * 1024 * 1024);
    }

    [TestMethod]
    public void TheLimitProblems_ListEveryFinding_WithTheHostAndTheRemedy()
    {
        var defaults = new ContainerResources();
        var supported = new DockerLimits(true, true, true, true, 4, 8L << 30);
        RoleGuards.ContainerLimitProblems(supported, defaults).Should().BeEmpty("a machine whose Docker enforces every limit passes");
        RoleGuards.ContainerLimitWarnings(supported, defaults).Should().BeEmpty();

        var limits = new ContainerResources { Cpus = 2, Memory = "4g" };
        RoleGuards.ContainerLimitProblems(new DockerLimits(true, true, true, true, 2, 4L << 30), limits).Should().BeEmpty("the limits may take all of the machine");
        RoleGuards.ContainerLimitProblems(new DockerLimits(true, true, true, true, 2, 1L << 30), limits).Should().BeEmpty("a memory limit above the machine's memory is a cap, not a reservation");
        RoleGuards.ContainerLimitWarnings(new DockerLimits(true, true, true, true, 2, 1L << 30), limits).Should().ContainSingle()
            .Which.Should().StartWith("The controller's memory limit of 4g is more than the 1024m Docker reports here. It is kept");

        var none = new DockerLimits(false, false, null, false, 1, 1L << 30)
        {
            CgroupVersion = "2",
            CgroupControllers = "cpuset io",
            Warnings = ["WARNING: No memory limit support", "WARNING: No cpu cfs quota support", "WARNING: bridge-nf-call-iptables is disabled"]
        };
        RoleGuards.ContainerLimitProblems(none, limits).Should().ContainSingle().Which.Should().Be(string.Join(Environment.NewLine,
            "Docker here cannot give the controller its limits (2 CPUs, 4g of memory and no swap, at most 512 processes and threads), so the install is refused. Every finding:",
            "  - docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit false, so Docker would run the controller without its memory limit (4g, and no swap beyond it).",
            "  - docker-cpu-quota-unsupported: docker info reports CPUCfsQuota false, so Docker would run the controller without its CPU limit (2).",
            "  - docker-pids-limit-unsupported: docker info reports PidsLimit not reported, so Docker would run the controller without its process limit (512).",
            "  - docker-host-cpus-insufficient: docker info reports 1 CPUs, fewer than the CPU limit of 2: give controller.resources.cpus (or --cpus) from 1 to 1.",
            "  Docker's host: cgroup v2; /sys/fs/cgroup/cgroup.controllers: \"cpuset io\"; Docker warns \"WARNING: No memory limit support\"; Docker warns \"WARNING: No cpu cfs quota support\".",
            "  " + RoleGuards.ContainerLimitRemedy + " Then run the installer again."));

        RoleGuards.ContainerLimitProblems(supported with { Cpus = null }, defaults).Should().ContainSingle()
            .Which.Should().Contain("  - docker-host-cpus-unreported: docker info does not report the CPUs (NCPU), so the CPU limit of 1 cannot be checked against them.")
            .And.NotContain("cgroup_enable=memory");

        // A rollback: the limits Docker cannot enforce are its warning, not a refusal; the CPUs count only for a CPU limit
        // Docker enforces.
        RoleGuards.ContainerLimitProblems(none, limits, rollback: true).Should().BeEmpty("the CPU limit is not enforced, so the CPUs do not count");
        RoleGuards.ContainerLimitProblems(none with { Cpu = true }, limits, rollback: true).Should().ContainSingle()
            .Which.Should().StartWith("Docker here cannot give the controller its limits (2 CPUs, 4g of memory and no swap, at most 512 processes and threads), so the rollback is refused. Every finding:")
            .And.Contain("docker-host-cpus-insufficient").And.NotContain("docker-memory-limit-unsupported");
        RoleGuards.ContainerLimitProblems(null, limits, rollback: true).Should().ContainSingle().Which.Should().StartWith("Docker did not say which limits it enforces");
        RoleGuards.ContainerLimitRollbackWarnings(supported, defaults).Should().BeEmpty();
        // Memory and swap are separate controls (review ROOF-151-R1 F3).
        var swapOnly = supported with { Swap = false };
        RoleGuards.ContainerLimitProblems(swapOnly, defaults).Should().ContainSingle().Which.Should()
            .Contain("  - docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit false, so Docker would let the controller swap beyond its memory limit (2g).")
            .And.NotContain("docker-memory-limit-unsupported");
        RoleGuards.ContainerLimitProblems(swapOnly, defaults, rollback: true).Should().BeEmpty();
        RoleGuards.ContainerLimitRollbackWarnings(swapOnly, defaults).Should().ContainSingle().Which.Should()
            .Contain("  - docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit false, so the version the rollback brings back keeps its memory limit (2g) but may swap beyond it.");
        RoleGuards.ContainerLimitRollbackWarnings(null, defaults).Should().BeEmpty();
        RoleGuards.ContainerLimitRollbackWarnings(none, limits).Should().ContainSingle().Which.Should().Be(string.Join(Environment.NewLine,
            "THE ROLLBACK GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE THESE LIMITS, so the version it brings back runs WITHOUT them. A rollback is a recovery path, so it is not refused (#149); an install, an upgrade or a deploy is refused here until this is fixed:",
            "  - docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit false, so the version the rollback brings back runs without its memory limit (4g, and no swap beyond it).",
            "  - docker-cpu-quota-unsupported: docker info reports CPUCfsQuota false, so the version the rollback brings back runs without its CPU limit (2).",
            "  - docker-pids-limit-unsupported: docker info reports PidsLimit not reported, so the version the rollback brings back runs without its process limit (512).",
            "  Docker's host: cgroup v2; /sys/fs/cgroup/cgroup.controllers: \"cpuset io\"; Docker warns \"WARNING: No memory limit support\"; Docker warns \"WARNING: No cpu cfs quota support\".",
            "  " + RoleGuards.ContainerLimitRemedy + " Then upgrade again."));
    }

    [TestMethod]
    public void DockerInfo_IsReadOnce_WithItsCgroupVersionHostAndWarnings()
    {
        DockerLimits.Format.Should().Be("{{.MemoryLimit}},{{.CPUCfsQuota}},{{.PidsLimit}},{{.SwapLimit}},{{.NCPU}},{{.MemTotal}},{{.CgroupVersion}},{{.Name}}\n{{range .Warnings}}{{println .}}{{end}}",
            "Docker 29's template field is CPUCfsQuota, not the JSON key CpuCfsQuota");
        var stock = DockerLimits.Parse(StockPiInfo + "\n");
        stock.Should().NotBeNull();
        stock!.Should().Match<DockerLimits>(limits => limits.Memory == false && limits.Cpu == true && limits.Pids == true && limits.Swap == false
            && limits.Cpus == 4 && limits.MemoryBytes == 8589934592 && limits.CgroupVersion == "2" && limits.HostName == "roofpi" && limits.CgroupControllers == null);
        stock.Warnings.Should().Equal("WARNING: No memory limit support", "WARNING: No swap limit support", "WARNING: bridge-nf-call-iptables is disabled");

        DockerLimits.Parse(",<no value>,,,,,<no value>,\n").Should().Match<DockerLimits>(limits =>
            limits.Memory == null && limits.Cpu == null && limits.Pids == null && limits.Swap == null && limits.Cpus == null
            && limits.CgroupVersion == null && limits.HostName == null && limits.Warnings.Count == 0);
        DockerLimits.Parse("true,true,true,true,4,8589934592").Should().BeNull("an answer without the cgroup version and the host is not this template's");
    }

    [TestMethod]
    public async Task TheCgroupControllers_AreRead_OnlyWhenDockerRunsHere()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(MachineSurveyor.CgroupControllersFile, "cpuset cpu io pids\n");
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        // Docker names another machine (a remote context, or Docker Desktop's VM): this machine's file is not its host's.
        pi.DockerInfo = StockPiInfo.Replace(",roofpi\n", ",docker-desktop\n", StringComparison.Ordinal);
        var run = await pi.RunAsync("--plan", "--answers", answers);
        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("/sys/fs/cgroup/cgroup.controllers on Docker's machine lists").And.NotContain("cpuset cpu io pids");

        pi.DockerInfo = StockPiInfo;
        run = await pi.RunAsync("--plan", "--answers", answers);
        run.Error.Should().Contain("/sys/fs/cgroup/cgroup.controllers: \"cpuset cpu io pids\"");
    }

    // The upgrade backups' files, sorted (none when the folder is not there).
    private static string[] Backups(FakeMachine pi)
        => Directory.Exists(pi.OnDisk(BackupCommands.DefaultFolder))
            ? [.. Directory.EnumerateFiles(pi.OnDisk(BackupCommands.DefaultFolder), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];

    private static decimal? Cpus(string? cpus) => cpus is null ? null : decimal.Parse(cpus, System.Globalization.CultureInfo.InvariantCulture);

    private static InstallAnswers Controller(ContainerResources resources)
        => new() { Roles = [InstallRole.Controller], Controller = new ControllerSettings { Resources = resources } };

    private static Dictionary<string, string> Limits(string cpus, string memory, string pids) => new()
    {
        ["CONTAINER_CPUS"] = cpus,
        ["CONTAINER_MEMORY"] = memory,
        ["CONTAINER_PIDS_LIMIT"] = pids
    };

    private static InstallRecord Record(FakeMachine machine) => InstallRecord.Parse(machine.Read(InstallPaths.SystemRecord));
}
