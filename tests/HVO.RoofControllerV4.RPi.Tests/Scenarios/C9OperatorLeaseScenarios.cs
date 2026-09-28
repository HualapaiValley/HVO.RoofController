using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C9: the operator lease, set to 5 s through the configuration API on the documented 2 m roof (about 21 s from limit
/// to limit), so only the lease can end the move inside the test. The browser's part (a closed tab, a lost network,
/// a reconnect inside the lease) is in the console's browser tests.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C9OperatorLeaseScenarios
{
    private const double Lease = 5;

    [TestMethod]
    [CommissioningCheck("C9", "1")]
    [CommissioningCheck("C9", "2")]
    public async Task RenewingKeepsTheRoofMoving_AndStoppingRenewals_StopsItWithOperatorLeaseExpired()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 2.0));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        (await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = Lease })).OperatorLeaseTimeoutSeconds.Should().Be(Lease);

        (await client.AcceptedAsync("Open")).LeaseSecondsRemaining.Should().BeApproximately(Lease, 0.5);
        for (var renewal = 1; renewal <= 4; renewal++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var renewed = await client.AcceptedAsync("Lease");
            renewed.IsMoving.Should().BeTrue("renewal {0} keeps the move going", renewal);
            renewed.LeaseSecondsRemaining.Should().BeApproximately(Lease, 0.5);
        }

        var lastRenewal = DateTimeOffset.UtcNow;
        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, "the lease to run out");
        var elapsed = (DateTimeOffset.UtcNow - lastRenewal).TotalSeconds;
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        stopped.IsFaultLatched.Should().BeFalse("an expired lease is an operator event, not a fault");
        stopped.ShouldBeDeenergized(rig);
        elapsed.Should().BeInRange(Lease - 0.5, Lease + 1.2, "the lease time plus at most one verification interval");
        rig.Plant.OpenLimitActuated.Should().BeFalse();
        await rig.WaitForRestAsync("the lease stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C9", "3")]
    public async Task ALeaseRenewal_WhileIdle_IsRefused_AndStartsNothing()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = Lease });

        (await client.RefusedAsync("Lease")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.LeaseNotActive));

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        (await client.StatusAsync()).ShouldBeDeenergized(rig);
        rig.Session.Plant.History.Should().NotContain(e => e.Kind == PlantEventKind.Relay, "no relay moved");
    }
}
