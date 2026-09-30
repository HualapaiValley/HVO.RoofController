using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>What the installer finds on a machine before it asks anything (#67): it only reads and asks.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerSurveyTests
{
    [TestMethod]
    public async Task APi_WithTheHat_IsFound()
    {
        using var pi = new FakeMachine().WithPi();

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.RuntimeIdentifier.Should().Be("linux-arm64");
        survey.OsName.Should().Be("Debian GNU/Linux 12 (bookworm)");
        survey.PiModel.Should().Be("Raspberry Pi 5 Model B Rev 1.0", "the device tree's model ends in a NUL");
        survey.HasHat.Should().BeTrue();
        survey.MissingHatDevices().Should().BeEmpty();
        survey.Docker.Should().Be(new DockerSurvey { Version = "27.3.1", ComposeVersion = "2.29.7" });
        survey.SystemRecord.Should().BeNull();
        survey.HasSystemConfiguration.Should().BeFalse();
        survey.Controller.Should().BeNull();
        survey.Kiosk.Should().BeNull();
        survey.Cli.Should().BeNull();
        survey.MacApp.Should().BeNull();
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APiWithoutI2c_LacksTheHat()
    {
        using var pi = new FakeMachine().WithPi(i2c: false);

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.IsPi.Should().BeTrue();
        survey.HasHat.Should().BeFalse();
        survey.MissingHatDevices().Should().Equal(HatDevices.I2c);
    }

    [TestMethod]
    public async Task AContainerTheDeployScriptMade_IsAdoptable_AndSaysWhichHatItDrives()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench")
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = true })
            .WithContainer(MachineSurveyor.HatEmulatorContainer, new FakeContainer { State = "exited", Version = null });

        var survey = await MachineSurveyor.SurveyAsync(bench.Machine);

        survey.Controller.Should().BeEquivalentTo(new ContainerSurvey
        {
            Name = MachineSurveyor.ControllerContainer,
            Image = $"ghcr.io/hualapaivalley/roof-controller@sha256:{new string('a', 64)}",
            State = "running",
            Origin = ContainerOrigin.DeployScript,
            HatEmulator = "hat-emulator:5555",
            Version = "4.0.0",
            PublishedPorts = [8088, 8443],
            ServesHttps = true
        });
        survey.HatEmulator!.IsRunning.Should().BeFalse();
        survey.HatEmulator.Version.Should().BeNull();
    }

    [TestMethod]
    public async Task AContainerComposeMade_SaysWhichProject()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { ComposeProject = "hvo-roofcontroller-rpi" });

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Controller!.Origin.Should().Be(ContainerOrigin.Compose);
        survey.Controller.ComposeProject.Should().Be("hvo-roofcontroller-rpi");
        survey.Controller.HatEmulator.Should().BeNull("it drives the real HAT");
        InstallerSession.DescribeSurvey(survey).Should().Contain(
            "Container:   roof-controller: running, 4.0.0, the real HAT (made by Docker Compose project hvo-roofcontroller-rpi)");
    }

    [TestMethod]
    [DataRow(null, null, "Docker is not installed.", DisplayName = "Not installed")]
    [DataRow("27.3.1", "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock", "Docker is installed, but pi may not use it. Run the installer with sudo, or add pi to the docker group.", DisplayName = "Not allowed")]
    [DataRow("27.3.1", "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", "Docker is installed, but it is not running. Start it (sudo systemctl start docker, or open Docker Desktop).", DisplayName = "Not running")]
    [DataRow("27.3.1", "error during connect: something else\nmore", "Docker did not answer: error during connect: something else", DisplayName = "Something else")]
    public async Task DockerThatCannotBeUsed_SaysWhy_AndNoContainerIsAskedFor(string? version, string? error, string expected)
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop") { DockerVersion = version, DockerError = error };

        var survey = await MachineSurveyor.SurveyAsync(laptop.Machine);

        survey.Docker.IsUsable.Should().BeFalse();
        survey.Docker.Problem.Should().Be(expected);
        laptop.Ran.Should().NotContain(command => command.Arguments.Contains("inspect"));
    }

    [TestMethod]
    public async Task DockerWithoutCompose_IsUsable()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop") { ComposeVersion = null };

        var survey = await MachineSurveyor.SurveyAsync(laptop.Machine);

        survey.Docker.IsUsable.Should().BeTrue();
        survey.Docker.ComposeVersion.Should().BeNull();
        InstallerSession.DescribeSurvey(survey).Should().Contain("Docker:      27.3.1, no Compose v2");
    }

    [TestMethod]
    public async Task TheKiosksService_IsFound_WhenItsUnitIsInstalled()
    {
        using var pi = new FakeMachine().WithPi().Write(MachineSurveyor.KioskUnitFile, "[Unit]\n");
        pi.Kiosk = (true, false);

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.Kiosk.Should().Be(new ServiceSurvey(MachineSurveyor.KioskUnit, Enabled: true, Active: false));
        InstallerSession.DescribeSurvey(survey).Should().Contain("Kiosk:       hvo-roof-kiosk.service: enabled, stopped");
    }

    [TestMethod]
    public async Task HvoRoof_IsFoundOnThePath_OrInItsFolders()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy").WithCli("/opt/tools/hvo-roof");
        using var other = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "other", userName: "roy").Write("/home/roy/.local/bin/hvo-roof", "#!/bin/sh\n");

        (await MachineSurveyor.SurveyAsync(laptop.Machine)).Cli.Should().Be(new ProgramSurvey("/opt/tools/hvo-roof", "4.0.0+0123456789abcdef"));
        (await MachineSurveyor.SurveyAsync(other.Machine)).Cli.Should().Be(new ProgramSurvey("/home/roy/.local/bin/hvo-roof", null), "one not on the PATH is found, though it cannot be asked its version here");
    }

    [TestMethod]
    public async Task TheMacApp_IsFound_WithItsVersion()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy")
            .Write("/Users/roy/Applications/HVO Roof.app/Contents/Info.plist", "<dict>\n  <key>CFBundleShortVersionString</key>\n  <string>4.0.0</string>\n</dict>\n");

        var survey = await MachineSurveyor.SurveyAsync(mac.Machine);

        survey.OsName.Should().Be("macOS 15.6.1");
        survey.MacApp.Should().Be(new ProgramSurvey("/Users/roy/Applications/HVO Roof.app", "4.0.0"));
        survey.HasHat.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheRecords_AreRead_TheMachinesAlways_AndThePersonsWhenNotRoot()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy")
            .Write(InstallPaths.SystemRecord, InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated).ToJson())
            .Write("/home/roy/.config/hvo-roof/install.json", InstallerGuardTests.Record(InstallRole.Cli, null).ToJson());

        var survey = await MachineSurveyor.SurveyAsync(laptop.Machine);

        survey.SystemRecord!.Roles.Should().Equal(InstallRole.Rig);
        survey.UserRecord!.Roles.Should().Equal(InstallRole.Cli);
        survey.HasSystemConfiguration.Should().BeTrue();
        InstallerSession.RecordedAnswers(survey, includeSystem: false)!.Roles.Should().Equal(InstallRole.Cli);
        InstallerSession.DescribeSurvey(survey).Should().Contain("Recorded:    a test rig 4.0.0 in the machine's record; hvo-roof 4.0.0 in your record");
    }

    [TestMethod]
    public async Task AControllerSetUpByHand_IsNoticed()
    {
        using var pi = new FakeMachine().WithPi().Folder("/etc/hvo-roof/secrets");

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        InstallerSession.DescribeSurvey(survey).Should().Contain("Found:       /etc/hvo-roof, set up without the installer");
    }

    [TestMethod]
    public async Task AnInspectAnswerTheInstallerCannotRead_IsAnError_ThatNeverQuotesIt()
    {
        using var pi = new FakeMachine().WithPi();
        var garbled = new GarbledInspect(pi);
        var machine = pi.Machine.WithCommands(garbled);

        var survey = async () => await MachineSurveyor.SurveyAsync(machine);

        var error = (await survey.Should().ThrowAsync<InstallerException>()).Which;
        error.Message.Should().Be("docker container inspect roof-controller gave an answer the installer cannot read.");
        error.Message.Should().NotContain(GarbledInspect.Secret);
    }

    /// <summary>Docker that answers <c>inspect</c> with something that is not its JSON (and holds a secret).</summary>
    private sealed class GarbledInspect(ICommandRunner inner) : ICommandRunner
    {
        public const string Secret = "not-a-real-secret-garbled";

        public string? Find(string program) => inner.Find(program);

        public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
            => command.Arguments is ["container", "inspect", _]
                ? Task.FromResult(new CommandResult(0, $"[{{\"Name\": \"x\", \"Env\": [\"KEY={Secret}\"]", string.Empty))
                : inner.RunAsync(command, cancellationToken);
    }
}
