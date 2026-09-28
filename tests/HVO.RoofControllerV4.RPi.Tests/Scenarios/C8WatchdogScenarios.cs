using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C8: the maximum-run watchdog, shortened to its 5 s minimum through the configuration API on a roof with 1 m of
/// travel (about 11 s from limit to limit), so the watchdog ends the move before the open limit does.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C8WatchdogScenarios
{
    [TestMethod]
    [CommissioningCheck("C8", "2")]
    [CommissioningCheck("C8", "3")]
    [CommissioningCheck("C8", "4")]
    public async Task TheWatchdog_StopsTheMoveAtItsTime_FromTheFirstOpen_AndRepeatedOpensDoNotExtendIt()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        var production = (await rig.ConfigureAsync(r => r)).SafetyWatchdogTimeoutSeconds;
        production.Should().Be(150, "appsettings.json sets 2 min 30 s");
        (await rig.ConfigureAsync(r => r with { SafetyWatchdogTimeoutSeconds = 5 })).SafetyWatchdogTimeoutSeconds.Should().Be(5);

        await client.AcceptedAsync("Open");
        var repeats = 0;
        while ((await client.StatusAsync()).IsMoving)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            using var again = await client.PostAsync($"{Scenario.RoofApi}/Open", content: null);
            if (again.IsSuccessStatusCode)
            {
                repeats++;
            }
        }

        repeats.Should().BeGreaterThanOrEqualTo(3);
        var stopped = await rig.WaitForRestAsync("the watchdog stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        stopped.ShouldBeDeenergized(rig);
        var forward = rig.ContactChanges(1);
        forward.Count(c => c.Closed).Should().Be(1, "a repeated Open renews nothing and restarts nothing");
        (forward.Last().At - forward.First().At).TotalSeconds.Should().BeApproximately(5, 0.3, "the watchdog runs from the first Open");
        rig.Plant.OpenLimitActuated.Should().BeFalse();

        // C8 step 4: the production value back, then the roof home.
        (await rig.ConfigureAsync(r => r with { SafetyWatchdogTimeoutSeconds = production })).SafetyWatchdogTimeoutSeconds.Should().Be(150);
        (await client.RefusedAsync("Close")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched));
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
