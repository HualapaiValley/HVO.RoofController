using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerPlantFaultTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ASuccessfulDeployWithAPlantFault_IsShownAndLogged_AndKeepsTheController(bool upgrade)
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        if (upgrade)
        {
            var installed = await pi.RunAsync("--answers", answers);
            installed.ExitCode.Should().Be(0, installed.ToString());
            pi.WithRelease("4.0.2", latest: true);
        }
        pi.DeployWarning = "[verify] WARNING: PLANT FAULT DriveFault: keeping the controller available with motion refused. Repair the cause, then explicitly ClearFault.";

        var run = upgrade ? await pi.RunAsync("upgrade") : await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(pi.DeployWarning);
        pi.Read(InstallPaths.SystemLog).Should().Contain(pi.DeployWarning);
        pi.Containers[MachineSurveyor.ControllerContainer].State.Should().Be("running");
    }
}
