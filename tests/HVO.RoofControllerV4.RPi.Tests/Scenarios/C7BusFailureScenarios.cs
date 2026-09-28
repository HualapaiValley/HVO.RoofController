using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C7: I2C failures between the controller and the HAT, injected in the emulated register client in place of an
/// interrupted bus: a whole outage while moving, an interruption shorter than <c>MaxConsecutiveInputReadFailures</c>
/// (3 in production), and relay register reads that fail while the roof is idle.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C7BusFailureScenarios
{
    [TestMethod]
    [CommissioningCheck("C7", "1")]
    [CommissioningCheck("C7", "2")]
    [CommissioningCheck("C7", "3")]
    [CommissioningCheck("C7", "4")]
    public async Task ABusOutage_WhileMoving_LatchesTheStop_TheHardwiredLimitStopsTheDrive_AndTheRelaysGoOffWhenTheBusReturns()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        var bus = rig.Session.Client;
        SetOutage(rig, down: true);

        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && s.CommandedMotion == RoofMotionDirection.None, "the outage to stop the open and latch");
        latched.LastStopReason.Should().BeOneOf(RoofControllerStopReason.InputReadFailure, RoofControllerStopReason.RelayVerificationFailed);
        latched.LatchedFaultReason.Should().Be(latched.LastStopReason);
        latched.InputsHealthy.Should().BeFalse();
        (await rig.WaitForControllerAsync(s => !s.RelayRegisterReadsHealthy, "the relay reads to fail")).RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        (await ReadinessAsync(rig)).Should().Be(HttpStatusCode.ServiceUnavailable);

        // C7 step 3: the stop cannot reach the relays, and says so.
        (await client.RefusedAsync("Stop")).Should().Be((HttpStatusCode.ServiceUnavailable, RoofControllerErrorCode.RelayStateUnverified));
        rig.Plant.RelayRegister.Should().Be(0b1001, "the HAT keeps the relays it was last given");
        var atTheLimit = await rig.WaitForPlantAsync(p => p.OpenLimitActuated && p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0,
            "the open limit's normally closed contact to stop the drive");
        atTheLimit.RelayContacts.Should().Equal([true, false, false, true], "only the hardwired limit stopped the drive");

        // C7 step 4: supervision turns the relays off once the bus is back; the latch waits for ClearFault.
        SetOutage(rig, down: false);
        var restored = await rig.WaitForControllerAsync(
            s => s.RelayRegisterState == RoofRelayRegisterState.Verified && s.RelayRegisterReadsHealthy && s.InputsHealthy,
            TimeSpan.FromSeconds(10), "supervision to verify the relays off");
        restored.ShouldBeDeenergized(rig);
        restored.IsFaultLatched.Should().BeTrue();
        rig.Logs.Entries.Should().Contain(e => e.Message.Contains("Relay register all-off re-verified by supervision", StringComparison.Ordinal));
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        (await ReadinessAsync(rig)).Should().Be(HttpStatusCode.OK);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        bus.InjectedFailures.Should().BeGreaterThan(0);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C7", "5")]
    public async Task AnInterruptionShorterThanTheThreshold_DoesNotStopTheMove()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        rig.Session.Client.FailNextInputReads = 2;

        var open = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, "the roof to open");
        open.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached, "two failed reads are below the production threshold of three");
        open.IsFaultLatched.Should().BeFalse();
        open.InputsHealthy.Should().BeTrue();
        rig.Session.Client.InjectedFailures.Should().Be(2);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C7", "6")]
    public async Task RelayRegisterReadsFailing_WhileIdle_ReportUnhealthy_ThenLatchRelayVerificationFailed()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        rig.Session.Client.FailWhen = access => access.IsRead && access.Covers(SmI010Board.RelayValueRegister);

        var first = await rig.WaitForControllerAsync(s => s.ConsecutiveRelayReadFailures >= 1, TimeSpan.FromSeconds(5), "a supervision read to fail");
        first.ConsecutiveRelayReadFailures.Should().Be(1);
        first.RelayRegisterReadsHealthy.Should().BeFalse();
        first.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified, "the last verified result is kept");
        first.IsFaultLatched.Should().BeFalse();
        (await ReadinessAsync(rig)).Should().Be(HttpStatusCode.ServiceUnavailable);

        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched, TimeSpan.FromSeconds(5), "the second failure to latch");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        latched.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified, "the all-off sequence could not read the register back");
        rig.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Critical && e.Message.Contains("could not be read 2 consecutive times", StringComparison.Ordinal));
        rig.Plant.RelayRegister.Should().Be(0);

        rig.Session.Client.FailWhen = null;
        (await rig.WaitForControllerAsync(s => s.RelayRegisterState == RoofRelayRegisterState.Verified && s.RelayRegisterReadsHealthy,
            TimeSpan.FromSeconds(5), "supervision to verify the relays")).IsFaultLatched.Should().BeTrue();
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        (await ReadinessAsync(rig)).Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    [CommissioningCheck("C7", "6")]
    public async Task TwoFailedRelayRegisterReads_WhileIdle_RerunTheAllOffSequence_AndItVerifiesWithoutALatch()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        var remaining = 2;
        rig.Session.Client.FailWhen = access => access.IsRead && access.Covers(SmI010Board.RelayValueRegister) && remaining-- > 0;

        await rig.WaitForControllerAsync(s => s.ConsecutiveRelayReadFailures >= 1, TimeSpan.FromSeconds(5), "a supervision read to fail");
        await rig.WaitForControllerAsync(s => s.RelayRegisterReadsHealthy, TimeSpan.FromSeconds(5), "the all-off sequence to read the register again");
        rig.Logs.Entries.Should().Contain(e => e.Message.Contains("Relay register all-off verified after 2 failed supervision reads", StringComparison.Ordinal));
        var status = await client.StatusAsync();
        status.IsFaultLatched.Should().BeFalse();
        status.ShouldBeDeenergized(rig);
        (await ReadinessAsync(rig)).Should().Be(HttpStatusCode.OK);
    }

    private static void SetOutage(EmulatedRoofRig rig, bool down)
    {
        var bus = rig.Session.Client;
        lock (rig.Session.Plant.SyncRoot)
        {
            bus.FailReads = down;
            bus.FailWrites = down;
        }
    }

    private static async Task<HttpStatusCode> ReadinessAsync(EmulatedRoofRig rig)
    {
        using var anonymous = rig.CreateApiClient();
        using var response = await anonymous.GetAsync("/health/ready");
        return response.StatusCode;
    }
}
