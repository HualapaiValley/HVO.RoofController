using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The installer's guards (#67): the real HAT only from a Pi with its three devices; never a test rig where the real HAT
/// is driven, and a typed confirmation for a rig on a machine with the HAT's I2C bus; the machine's roles as root and the
/// person's never.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerGuardTests
{
    private static readonly InstallAnswers Controller = new() { Roles = [InstallRole.Controller] };
    private static readonly InstallAnswers Rig = new() { Roles = [InstallRole.Rig] };

    [TestMethod]
    public async Task TheController_OnAPiWithTheHatsDevices_AsRoot_IsAllowed()
    {
        using var pi = new FakeMachine().WithPi();

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        survey.HasHat.Should().BeTrue();
        RoleGuards.Option(InstallRole.Controller, survey).Available.Should().BeTrue();
        RoleGuards.Check(survey, Controller).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false, true, true, HatDevices.I2c)]
    [DataRow(true, false, true, HatDevices.GpioMemory)]
    [DataRow(true, true, false, HatDevices.ThermalSensor)]
    public async Task TheController_WithoutEachOfTheHatsDevices_IsRefused_NamingIt(bool i2c, bool gpio, bool thermal, string missing)
    {
        using var pi = new FakeMachine().WithPi(i2c, gpio, thermal);

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        var option = RoleGuards.Option(InstallRole.Controller, survey);
        option.Available.Should().BeFalse();
        option.Reason.Should().Contain(missing).And.Contain("raspi-config nonint do_i2c 0");
        RoleGuards.Check(survey, Controller).Should().ContainSingle().Which.Should().Contain(missing);
    }

    [TestMethod]
    [DataRow(InstallerOs.Linux, Architecture.X64)]
    [DataRow(InstallerOs.MacOS, Architecture.Arm64)]
    public async Task TheController_AnywhereButA64BitLinuxPi_IsRefused_PointingAtARig(InstallerOs os, Architecture architecture)
    {
        using var machine = new FakeMachine(os, architecture, root: os == InstallerOs.Linux, hostName: "bench");

        var survey = await MachineSurveyor.SurveyAsync(machine.Machine);

        RoleGuards.Option(InstallRole.Controller, survey).Reason.Should().Contain("only from the observatory's Raspberry Pi").And.Contain("test rig");
    }

    [TestMethod]
    public async Task TheController_WithoutSudo_IsRefused_ButItsPlanIsNot()
    {
        using var pi = new FakeMachine(root: false).WithPi();

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        RoleGuards.Check(survey, Controller).Should().ContainSingle().Which.Should().Contain("run the installer with sudo");
        RoleGuards.Check(survey, Controller, planOnly: true).Should().BeEmpty("--plan changes nothing, so it needs no sudo");
    }

    [TestMethod]
    public async Task TheCli_AsRoot_IsRefused()
    {
        using var machine = new FakeMachine(architecture: Architecture.X64, root: true, hostName: "bench");

        var survey = await MachineSurveyor.SurveyAsync(machine.Machine);

        RoleGuards.Check(survey, new InstallAnswers { Roles = [InstallRole.Cli] }).Should().ContainSingle()
            .Which.Should().Contain("never installed as root");
    }

    [TestMethod]
    public async Task TheMachinesRolesAndThePersons_InOneRun_AreRefused()
    {
        using var machine = new FakeMachine(architecture: Architecture.X64, root: true, hostName: "bench");

        var survey = await MachineSurveyor.SurveyAsync(machine.Machine);

        RoleGuards.Check(survey, new InstallAnswers { Roles = [InstallRole.Rig, InstallRole.Cli] }).Should().ContainSingle()
            .Which.Should().Contain("Install them in separate runs");
    }

    [TestMethod]
    public async Task ARigOnAMac_IsThePersons_AndRefusedAsRoot()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        using var rootMac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: true, hostName: "studio");

        RoleGuards.Check(await MachineSurveyor.SurveyAsync(mac.Machine), Rig).Should().BeEmpty("a rig on a Mac runs in the person's Docker Desktop");
        RoleGuards.Check(await MachineSurveyor.SurveyAsync(mac.Machine), new InstallAnswers { Roles = [InstallRole.Rig, InstallRole.Cli, InstallRole.MacApp] })
            .Should().Equal("hvo-roof and the Mac app are set up against a running controller: install the rig first, then run the installer again for them.");
        RoleGuards.Check(await MachineSurveyor.SurveyAsync(rootMac.Machine), Rig).Should().ContainSingle().Which.Should().Contain("without sudo");
    }

    [TestMethod]
    public async Task TheKeychain_IsAMacs_AndRefusedOnLinux()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        var answers = new InstallAnswers { Roles = [InstallRole.Cli], Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } };

        RoleGuards.Check(await MachineSurveyor.SurveyAsync(laptop.Machine), answers).Should()
            .Equal("The controller's CA is trusted in the keychain on a Mac only: docs/install.md says how to add it to a browser on Linux.");
        RoleGuards.Check(await MachineSurveyor.SurveyAsync(mac.Machine), answers).Should().BeEmpty();
    }

    [TestMethod]
    public async Task OneClient_MovedToAnotherController_IsRefused_WhileTheOtherStaysOnTheRecordedOne()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        var recorded = FakeMachine.ClientAnswers;
        mac.Write("/Users/roy/.config/hvo-roof/install.json", (Record(InstallRole.Cli, null) with
        {
            Scope = InstallScope.User,
            Roles = [InstallRole.Cli, InstallRole.MacApp],
            Client = recorded
        }).ToJson());
        var survey = await MachineSurveyor.SurveyAsync(mac.Machine);
        var elsewhere = new ClientSettings { Controller = "https://spare-pi.local:8443", CaSha256 = recorded.CaSha256 };
        IReadOnlyList<string> Check(ClientSettings client, params InstallRole[] roles) =>
            RoleGuards.Check(survey, new InstallAnswers { Roles = roles, Client = client });

        Check(elsewhere, InstallRole.Cli).Should().Equal(
            $"hvo-roof and the Mac app connect to the same controller, and the Mac app connects to {recorded.Controller}: give the same controller and trust, or choose both to move them together.");
        Check(recorded with { CaSha256 = null, CertificateSha256 = recorded.CaSha256 }, InstallRole.MacApp).Should().ContainSingle()
            .Which.Should().StartWith("The Mac app and hvo-roof").And.Contain(", trusting it another way:");
        Check(recorded, InstallRole.Cli).Should().BeEmpty();
        Check(recorded with { Controller = recorded.Controller + "/ ", CaSha256 = recorded.CaSha256!.Replace(":", string.Empty).ToLowerInvariant() }, InstallRole.Cli)
            .Should().BeEmpty("the same address and fingerprint, written another way");
        Check(recorded with { TrustInKeychain = true }, InstallRole.MacApp).Should().BeEmpty("the keychain is the run's own");
        Check(elsewhere, InstallRole.Cli, InstallRole.MacApp).Should().BeEmpty("both move together");
    }

    [TestMethod]
    public async Task TheControllerAndARig_OnOneMachine_AreRefused()
    {
        using var pi = new FakeMachine().WithPi();

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        RoleGuards.Check(survey, new InstallAnswers { Roles = [InstallRole.Controller, InstallRole.Rig], RigConfirmation = "roofpi" })
            .Should().Contain(problem => problem.Contains("cannot share a machine"));
    }

    [TestMethod]
    public async Task ARig_WhereTheRecordSaysTheRealHat_IsRefused_EvenWithTheConfirmation()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(InstallPaths.SystemRecord, Record(InstallRole.Controller, HatMode.Real).ToJson());

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        var option = RoleGuards.Option(InstallRole.Rig, survey);
        option.Available.Should().BeFalse();
        option.Reason.Should().Contain(InstallPaths.SystemRecord).And.Contain("drives the real HAT");
        RoleGuards.Check(survey, Rig with { RigConfirmation = "roofpi" }).Should().Contain(option.Reason!);
    }

    [TestMethod]
    [DataRow("{\"schema\": 2, \"scope\": \"system\"}", "written by a newer installer (schema 2)", DisplayName = "a newer installer's")]
    [DataRow("{ not json", "is not a valid install record", DisplayName = "a damaged one")]
    public async Task ARig_WhereTheRecordCannotBeRead_IsRefused_UntilItIsFixed(string record, string problem)
    {
        // No controller container either: the record is all that could say this is the observatory's Pi.
        using var pi = new FakeMachine().WithPi();
        pi.Write(InstallPaths.SystemRecord, record);

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);

        var option = RoleGuards.Option(InstallRole.Rig, survey);
        option.Available.Should().BeFalse("an unreadable record may say the machine drives the real HAT");
        option.Reason.Should().Contain(InstallPaths.SystemRecord).And.Contain(problem).And.Contain("fixed or removed");
        RoleGuards.Check(survey, Rig with { RigConfirmation = "roofpi" }).Should().Contain(option.Reason!);
        RoleGuards.Option(InstallRole.Controller, survey).Available.Should().BeTrue("the controller is the real HAT's role");
    }

    [TestMethod]
    public async Task ARig_OverAControllerDrivingTheRealHat_IsRefused()
    {
        using var machine = new FakeMachine(architecture: Architecture.X64, hostName: "bench")
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Emulated = false });

        var survey = await MachineSurveyor.SurveyAsync(machine.Machine);

        RoleGuards.Option(InstallRole.Rig, survey).Reason.Should().Contain("container on this machine drives the real HAT");
    }

    [TestMethod]
    public async Task ARig_OnAMachineWithTheI2cBus_NeedsItsHostNameTyped()
    {
        using var sparePi = new FakeMachine(hostName: "spare-pi").WithPi();

        var survey = await MachineSurveyor.SurveyAsync(sparePi.Machine);

        RoleGuards.NeedsRigConfirmation(survey, Rig.Roles).Should().BeTrue();
        RoleGuards.RigConfirmationPrompt(survey).Should().Contain("spare-pi").And.Contain(HatDevices.I2c);
        RoleGuards.Check(survey, Rig).Should().ContainSingle().Which.Should().Contain("Type its host name (spare-pi)");
        RoleGuards.Check(survey, Rig with { RigConfirmation = "roofpi" }).Should().ContainSingle().Which.Should().Contain("does not match");
        RoleGuards.Check(survey, Rig with { RigConfirmation = "Spare-Pi" }).Should().ContainSingle("the host name is typed exactly");
        RoleGuards.Check(survey, Rig with { RigConfirmation = " spare-pi " }).Should().BeEmpty();
        RoleGuards.Check(survey, Rig, planOnly: true).Should().BeEmpty("--plan changes nothing, so it needs no confirmation");
    }

    [TestMethod]
    public async Task ARig_AlreadyRecordedOnAMachineWithTheI2cBus_IsNotConfirmedAgain()
    {
        using var sparePi = new FakeMachine(hostName: "spare-pi").WithPi();
        sparePi.Write(InstallPaths.SystemRecord, Record(InstallRole.Rig, HatMode.Emulated).ToJson());

        var survey = await MachineSurveyor.SurveyAsync(sparePi.Machine);

        RoleGuards.NeedsRigConfirmation(survey, Rig.Roles).Should().BeFalse();
        RoleGuards.Check(survey, Rig).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARig_WithoutTheI2cBus_NeedsNoConfirmation()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");

        var survey = await MachineSurveyor.SurveyAsync(bench.Machine);

        RoleGuards.NeedsRigConfirmation(survey, Rig.Roles).Should().BeFalse();
        RoleGuards.Check(survey, Rig).Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheKiosk_NeedsTheControllerOnItsPi()
    {
        using var pi = new FakeMachine().WithPi().WithDisplay();
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var kiosk = new InstallAnswers { Roles = [InstallRole.Kiosk] };

        RoleGuards.Check(await MachineSurveyor.SurveyAsync(pi.Machine), kiosk).Should().ContainSingle().Which.Should().Contain("needs the controller");
        RoleGuards.Check(await MachineSurveyor.SurveyAsync(pi.Machine), kiosk with { Roles = [InstallRole.Controller, InstallRole.Kiosk] }).Should().BeEmpty();
        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(bench.Machine)).Reason.Should().Contain("Raspberry Pi");

        pi.Write(InstallPaths.SystemRecord, Record(InstallRole.Controller, HatMode.Real).ToJson());
        RoleGuards.Check(await MachineSurveyor.SurveyAsync(pi.Machine), kiosk).Should().BeEmpty("the controller is recorded as installed");
    }

    [TestMethod]
    public async Task TheKiosk_NeedsAScreen_AndNoDesktop()
    {
        using var bare = new FakeMachine().WithPi();
        using var dark = new FakeMachine().WithPi().WithDisplay(connected: false);
        using var desktop = new FakeMachine().WithPi().WithDisplay();
        desktop.DisplayManagerActive = true;
        using var ready = new FakeMachine().WithPi().WithDisplay();

        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(bare.Machine)).Reason.Should().Contain("screen");
        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(dark.Machine)).Reason.Should().Contain("screen");
        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(desktop.Machine)).Reason.Should().Contain("display-manager.service");
        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(ready.Machine)).Available.Should().BeTrue();
    }

    [TestMethod]
    public async Task TheKiosk_IsRefused_WhenADesktopStartsAtBoot_AndNotWhenThePiBootsToTheConsole()
    {
        using var stopped = new FakeMachine().WithPi().WithDisplay();
        stopped.DisplayManagerEnabled = true;
        using var console = new FakeMachine().WithPi().WithDisplay();
        console.DisplayManagerEnabled = true;
        console.DefaultTarget = "multi-user.target";

        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(stopped.Machine)).Reason.Should()
            .Contain("takes the screen at boot").And.Contain("sudo systemctl set-default multi-user.target");
        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(console.Machine)).Available.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnInstalledKiosk_IsRefused_WhileADesktopRuns_WithNoDisplayOutputsListed()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(InstallPaths.SystemRecord, (Record(InstallRole.Controller, HatMode.Real) with { Roles = [InstallRole.Controller, InstallRole.Kiosk] }).ToJson());
        pi.DisplayManagerActive = true;

        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(pi.Machine)).Reason.Should().Contain("display-manager.service is running");
    }

    [TestMethod]
    public async Task TheKiosk_OnceInstalled_IsKept_WithItsScreenOff()
    {
        using var pi = new FakeMachine().WithPi().WithDisplay(connected: false);
        pi.Write(InstallPaths.SystemRecord, (Record(InstallRole.Controller, HatMode.Real) with { Roles = [InstallRole.Controller, InstallRole.Kiosk] }).ToJson());

        RoleGuards.Option(InstallRole.Kiosk, await MachineSurveyor.SurveyAsync(pi.Machine)).Available.Should().BeTrue("a screen that is off, or unplugged for now, does not take the kiosk away");
    }

    [TestMethod]
    public async Task TheMacApp_OnlyOnAnAppleSiliconMac()
    {
        using var linux = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "bench");
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio");

        RoleGuards.Option(InstallRole.MacApp, await MachineSurveyor.SurveyAsync(linux.Machine)).Reason.Should().Contain("Apple silicon");
        RoleGuards.Option(InstallRole.MacApp, await MachineSurveyor.SurveyAsync(mac.Machine)).Available.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(InstallerOs.MacOS, Architecture.X64, "osx-x64")]
    [DataRow(InstallerOs.Linux, Architecture.Arm, "linux-arm")]
    public async Task AnUnsupportedPlatform_RefusesEveryRole(InstallerOs os, Architecture architecture, string platform)
    {
        using var machine = new FakeMachine(os, architecture, root: false);

        var survey = await MachineSurveyor.SurveyAsync(machine.Machine);

        RoleGuards.PlatformProblem(survey).Should().Contain(platform).And.Contain("linux-arm64, linux-x64, osx-arm64");
        RoleGuards.Options(survey).Should().OnlyContain(option => !option.Available);
        RoleGuards.Check(survey, new InstallAnswers { Roles = [InstallRole.Cli] }).Should().ContainSingle().Which.Should().Contain(platform);
    }

    [TestMethod]
    public async Task TheController_WithoutDocker_IsRefused_SayingWhy()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DockerVersion = null;

        var missing = RoleGuards.Check(await MachineSurveyor.SurveyAsync(pi.Machine), Controller);
        pi.DockerVersion = "27.3.1";
        pi.DockerError = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?";
        var stopped = RoleGuards.Check(await MachineSurveyor.SurveyAsync(pi.Machine), Controller);

        missing.Should().ContainSingle().Which.Should().Contain("run in Docker").And.Contain("Docker is not installed");
        stopped.Should().ContainSingle().Which.Should().Contain("it is not running");
    }

    [TestMethod]
    public async Task NoRoles_AreRefused()
    {
        using var machine = new FakeMachine(architecture: Architecture.X64, root: false);

        RoleGuards.Check(await MachineSurveyor.SurveyAsync(machine.Machine), new InstallAnswers()).Should().Equal("Choose at least one role.");
    }

    internal static InstallRecord Record(InstallRole role, HatMode? hat, string version = "4.0.0") => new()
    {
        Scope = InstallRoles.ScopeOf(role, InstallerOs.Linux),
        Roles = [role],
        Hat = hat,
        Version = version,
        InstallerVersion = version,
        InstalledAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        Controller = InstallRoles.RunsController([role]) ? new ControllerSettings() : null,
        Cli = role == InstallRole.Cli ? new CliSettings() : null
    };
}
