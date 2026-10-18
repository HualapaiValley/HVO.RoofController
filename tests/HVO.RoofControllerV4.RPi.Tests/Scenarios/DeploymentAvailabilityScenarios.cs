using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Drive;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class DeploymentAvailabilityScenarios
{
    [TestMethod]
    [CommissioningCheck("C12", "12")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnUnpoweredDrive_AtFirstStartOrUpgrade_KeepsSoftwareAvailable_UntilExplicitRecovery(bool upgrade)
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production() with
        {
            InitialStatus = null,
            BeforeTheControllerStarts = upgrade ? null : session => session.Plant.SetDrivePower(false)
        });
        if (upgrade)
        {
            rig.Session.Plant.SetDrivePower(false);
            await rig.WaitForControllerAsync(s => s.IsFaultLatched, "drive power loss to latch before upgrade");
            await rig.RestartControllerAsync(crash: false);
        }
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        using var viewer = rig.CreateApiClient(TestApiKeys.Viewer);
        using var anonymous = rig.CreateApiClient();

        var fault = await rig.WaitForControllerAsync(s => s.IsFaultLatched && s.InputsHealthy && s.RelayRegisterReadsHealthy, "fresh I/O and a drive fault");
        fault.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        fault.ShouldBeDeenergized(rig);
        (await anonymous.GetAsync("/health/available")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await viewer.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await anonymous.GetAsync(Scenario.RoofApi + "/Status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.RefusedAsync("Open")).Code.Should().Be(RoofControllerErrorCode.FaultLatched);
        (await client.RefusedAsync("Close")).Code.Should().Be(RoofControllerErrorCode.FaultLatched);
        (await client.RefusedAsync("ClearFault")).Code.Should().Be(RoofControllerErrorCode.InterlockActive);
        (await client.AcceptedAsync("Stop")).IsFaultLatched.Should().BeTrue();

        rig.Session.Plant.SetDrivePower(true);
        await rig.WaitForControllerAsync(s => s.IsDriveFaultActive == false, "drive power recovery");
        (await client.StatusAsync()).IsFaultLatched.Should().BeTrue();
        (await client.RefusedAsync("Open")).Code.Should().Be(RoofControllerErrorCode.FaultLatched);
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        (await anonymous.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ATripAlsoStaysAvailable_AndAHealthyMoveDoesNotFailDockerHealth()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        (await client.AcceptedAsync("Open")).IsMoving.Should().BeTrue();
        (await client.GetAsync("/health/available")).StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Session.Plant.TripDrive(SmVectorTrip.External);
        var fault = await rig.WaitForControllerAsync(s => s.IsFaultLatched, "drive trip");
        fault.ShouldBeDeenergized(rig);
        (await client.GetAsync("/health/available")).StatusCode.Should().Be(HttpStatusCode.OK);

        rig.Server.Outage = true;
        await rig.WaitForControllerAsync(s => !s.InputsHealthy && !s.RelayRegisterReadsHealthy, "the HAT link to fail");
        (await client.GetAsync("/health/available")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        rig.Server.Outage = false;
    }
}
