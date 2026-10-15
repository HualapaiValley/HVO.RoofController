using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Optional splash answers and display choices, entirely on the fake machine.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerSplashTests
{
    [TestMethod]
    public async Task Uninstall_RestoresOwnedSplash_WhenLaterAnswersOmittedIt()
    {
        using var pi = new FakeMachine().WithPi().WithDisplay();
        var record = new InstallRecord
        {
            Scope = InstallScope.System,
            Roles = [InstallRole.Kiosk],
            Version = "4.0.0",
            InstallerVersion = "4.0.0",
            Kiosk = new KioskSettings()
        };
        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);
        PlanBuilder.BuildUninstall(pi.Machine, survey, record, purge: false).Steps
            .Should().NotContain(step => step is KioskSplashStep);

        pi.Write("/etc/hvo-roof-kiosk/splash/state.json", "{}");
        var steps = PlanBuilder.BuildUninstall(pi.Machine, survey, record, purge: false).Steps.ToList();
        steps.OfType<KioskSplashStep>().Should().ContainSingle();
        steps.FindIndex(step => step is KioskSplashStep).Should()
            .BeLessThan(steps.FindIndex(step => step is KioskServiceRemovalStep));
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NonPi_RefusesRealHatAndRotation_EvenIfDeviceNamesExist()
    {
        using var vm = new FakeMachine().WithDisplay();
        vm.Write(HatDevices.I2c, "").Write(HatDevices.GpioMemory, "").Write(HatDevices.ThermalSensor, "40000");
        vm.Write(HatDevices.PiModel, "Unrelated ARM board");
        var survey = await MachineSurveyor.SurveyAsync(vm.Machine);
        survey.IsPi.Should().BeFalse();
        survey.HasHat.Should().BeFalse();
        HVO.RoofControllerV4.Installer.Roles.RoleGuards.Option(InstallRole.Controller, survey).Available.Should().BeFalse();
        var answers = new InstallAnswers { Roles = [InstallRole.Rig, InstallRole.Kiosk], Kiosk = new KioskSettings { Rotation = 90 } };
        HVO.RoofControllerV4.Installer.Roles.RoleGuards.Check(survey, answers, planOnly: true).Should().Contain(p => p.Contains("Pi-only"));
        answers = answers with { Kiosk = new KioskSettings { Rotation = 0 } };
        HVO.RoofControllerV4.Installer.Roles.RoleGuards.Check(survey, answers, planOnly: true).Should().NotContain(p => p.Contains("Pi-only"));
    }

    [TestMethod]
    public void OptionalChoices_RoundTrip_AndDoNotBecomeDefaults()
    {
        new KioskSettings().Splash.Should().BeNull();
        var answers = new InstallAnswers
        {
            Roles = [InstallRole.Rig, InstallRole.Kiosk],
            Kiosk = new KioskSettings { Card = "", Rotation = 270, Splash = new KioskSplashSettings { Image = "/operator/scene.png" } }
        };
        InstallAnswers.Parse(answers.ToJson()).Kiosk.Should().Be(answers.Kiosk);
        answers.Kiosk.Problems().Should().BeEmpty();
    }

    [TestMethod]
    public void NumberedCardsAndUnsupportedRotations_AreRefused()
    {
        new KioskSettings { Card = "/dev/dri/card1" }.Problems().Should().NotBeEmpty();
        new KioskSettings { Rotation = 45 }.Problems().Should().NotBeEmpty();
        new KioskSettings { Card = "/dev/dri/by-path/platform-display-card" }.Problems().Should().BeEmpty();
        new KioskSettings { Splash = new KioskSplashSettings() }.Problems().Should().NotBeEmpty();
        new KioskSettings { Splash = new KioskSplashSettings { Enabled = false } }.Problems().Should().BeEmpty();
    }

    [TestMethod]
    public void HelperResources_AreCarriedByTheCanonicalInstaller()
    {
        KioskSteps.Resource("splash/setup.py").Should().Contain("update-initramfs").And.NotContain("systemctl reboot");
        KioskSteps.Resource("splash/theme.script").Should().Contain("Plymouth.GetMode()").And.Contain("@ROTATION@");
        KioskSteps.Resource("splash/display-gate.py").Should().Contain("/sys/class/drm").And.NotContain("platform-1f00118000");
    }

    [TestMethod]
    public async Task ExplicitDisplayChoices_AreApplied_WithoutEnablingSplash()
    {
        using var pi = new FakeMachine().WithPi().WithDisplay().Write("/boot/firmware/cmdline.txt", "console=tty1 rootwait\n");
        var answers = new InstallAnswers
        {
            Roles = [InstallRole.Controller, InstallRole.Kiosk],
            Kiosk = new KioskSettings { Card = "", Rotation = 270 }
        };
        var result = await pi.RunAsync("--setup-prerequisites", "--answers", pi.WriteAnswers(answers));
        result.ExitCode.Should().Be(0, result.ToString());
        var settings = JsonNode.Parse(pi.Read(KioskSteps.SettingsFile))!["Kiosk"]!;
        settings["Rotation"]!.GetValue<int>().Should().Be(270);
        settings["Card"].Should().BeNull();
        pi.Unexpected.Should().BeEmpty();
        pi.Exists("/etc/hvo-roof-kiosk/splash/state.json").Should().BeFalse();
    }
}
