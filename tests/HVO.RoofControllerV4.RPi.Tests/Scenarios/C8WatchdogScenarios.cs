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
/// C8: the full travel time in each direction, which the travel time metric measures (step 1), and the maximum-run
/// watchdog, shortened to its 5 s minimum through the configuration API on a roof with 1 m of travel (about 11 s from
/// limit to limit), so the watchdog ends the move before the open limit does.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C8WatchdogScenarios
{
    // The controller times each move from its command and each stop from its decision, as its watchdog and drive stop
    // window do, and an input change from when it processes it (docs/telemetry.md, Motion timing). So each value is
    // the plant's own time plus the controller's overhead: up to one input poll (25 ms), and its HAT reads and writes,
    // about 0.1 s against the emulator. Overhead bounds that; Jitter allows for the two clocks' resolution.
    private const double Overhead = 0.5;
    private const double Jitter = 0.01;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [CommissioningCheck("C8", "1")]
    public async Task TheTravelTimeMetric_MeasuresTheFullTravelInEachDirection_AndTheDriveTimings()
    {
        using var timing = MotionTimingRecorder.ForTheProcess();
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);

        // A move that is not a full travel: stopped once the closed limit has released, then closed from there.
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => !p.ClosedLimitActuated && p.VelocityMetersPerSecond > 0, "the roof to leave the closed limit");
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await client.AcceptedAsync("Stop");
        await rig.WaitForRestAsync("the Stop");
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        rig.Session.Plant.Violations.Should().BeEmpty();

        // The plant's moves: each from its direction contact closing, to the destination limit or the contact opening.
        var open = rig.ContactChanges(1);
        var close = rig.ContactChanges(2);
        open.Select(c => c.Closed).Should().Equal([true, false, true, false], "two opens");
        close.Select(c => c.Closed).Should().Equal([true, false, true, false], "two closes");
        TimeSpan First(string input, TimeSpan after) => rig.EventTimes(PlantEventKind.Input, input).First(t => t > after);
        var fullOpen = First("IN1 HIGH", open[0].At) - open[0].At;
        var fullClose = First("IN2 HIGH", close[0].At) - close[0].At;

        var travel = timing.Of("roof.controller.travel.duration");
        TestContext.WriteLine($"Full travel (plant): opening {fullOpen.TotalSeconds:F3} s, closing {fullClose.TotalSeconds:F3} s");
        TestContext.WriteLine($"travel.duration: {string.Join("; ", travel)}");
        travel.Select(t => (t.Direction, t.Reason, t.FromLimit)).Should().Equal(
            ("opening", "LimitSwitchReached", true),
            ("closing", "LimitSwitchReached", true),
            ("opening", "NormalStop", true),
            ("closing", "LimitSwitchReached", false));
        var full = travel.Where(t => t.Reason == "LimitSwitchReached" && t.FromLimit == true).ToDictionary(t => t.Direction!, t => t.Seconds);
        full["opening"].Should().BeInRange(fullOpen.TotalSeconds - Jitter, fullOpen.TotalSeconds + Overhead, "the open's full travel");
        full["closing"].Should().BeInRange(fullClose.TotalSeconds - Jitter, fullClose.TotalSeconds + Overhead, "the close's full travel");
        // The partial open's two ends are both relay writes, so the overheads of its start and its stop offset.
        travel[2].Seconds.Should().BeApproximately((open[3].At - open[2].At).TotalSeconds, Overhead);
        var partialClose = First("IN2 HIGH", close[2].At) - close[2].At;
        travel[3].Seconds.Should().BeInRange(partialClose.TotalSeconds - Jitter, partialClose.TotalSeconds + Overhead);

        // The drive's start: from each direction contact closing to the drive's run output (IN4) reaching the HAT.
        var startDelays = timing.Of("roof.controller.drive.start_delay");
        TestContext.WriteLine($"drive.start_delay: {string.Join("; ", startDelays)}");
        startDelays.Select(t => t.Direction).Should().Equal("opening", "closing", "opening", "closing");
        var starts = new[] { open[0].At, close[0].At, open[2].At, close[2].At };
        for (var i = 0; i < starts.Length; i++)
        {
            var plant = (First("IN4 HIGH", starts[i]) - starts[i]).TotalSeconds;
            startDelays[i].Seconds.Should().BeInRange(plant - Jitter, plant + Overhead, "start {0}", i + 1);
        }

        // The drive's stop: at a limit stop IN4 drops just before the limit reports, so at most the controller's own
        // stop sequence; after the Stop, the drive's run-down (a coast stop: milliseconds) from its contact opening.
        var stopDelays = timing.Of("roof.controller.drive.stop_delay");
        TestContext.WriteLine($"drive.stop_delay: {string.Join("; ", stopDelays)}");
        stopDelays.Select(t => (t.Direction, t.Reason)).Should().Equal(
            ("opening", "LimitSwitchReached"), ("closing", "LimitSwitchReached"), ("opening", "NormalStop"), ("closing", "LimitSwitchReached"));
        stopDelays.Where(t => t.Reason == "LimitSwitchReached").Should().OnlyContain(t => t.Seconds >= 0 && t.Seconds <= Overhead);
        var runDown = (First("IN4 LOW", open[3].At - TimeSpan.FromMilliseconds(1)) - open[3].At).TotalSeconds;
        stopDelays[2].Seconds.Should().BeInRange(runDown - Jitter, runDown + Overhead, "the drive's run-down after the Stop");

        // The start limit's release: the closed limit on each open, the open limit on the full close.
        var releases = timing.Of("roof.controller.departure.release");
        TestContext.WriteLine($"departure.release: {string.Join("; ", releases)}");
        releases.Select(t => t.Direction).Should().Equal("opening", "closing", "opening");
        var departures = new[] { ("IN2 LOW", open[0].At), ("IN1 LOW", close[0].At), ("IN2 LOW", open[2].At) };
        for (var i = 0; i < departures.Length; i++)
        {
            var (input, start) = departures[i];
            var plant = (First(input, start) - start).TotalSeconds;
            releases[i].Seconds.Should().BeInRange(plant - Jitter, plant + Overhead, "release {0}", i + 1);
        }
    }

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
