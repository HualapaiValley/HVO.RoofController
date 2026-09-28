using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C13 (RV-5): the controller exporting to an OTLP collector that is reachable (the baseline), one that never answers
/// (an address that times out) and one whose port is closed (an immediate refusal). Each run times Stop idle and
/// during moves, a limit stop and a watchdog stop, on the host's own Kestrel port; the outages must add no latency.
/// The hour-long outage (step 4) is the nightly soak's, which exports to a collector that never answers.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C13TelemetryOutageScenarios
{
    // Enough idle Stops that about one in ten meets a supervision read of the HAT (once a second, holding the controller's
    // lock), so the 99th percentile measures that wait in every run rather than whether one Stop happened to meet it.
    private const int IdleStops = 100;
    private const int MovingStops = 50;
    private const int WarmUpStops = 5;
    private const double WatchdogSeconds = 5;
    private const double ProductionWatchdogSeconds = 150;
    private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(1);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [CommissioningCheck("C13", "1")]
    [CommissioningCheck("C13", "2")]
    [CommissioningCheck("C13", "3")]
    public async Task ACollectorThatTimesOutOrRefuses_AddsNoLatencyToStop_LimitStops_OrWatchdogStops()
    {
        var baseline = await MeasureAsync(CollectorBehavior.Reachable);
        var blackHole = await MeasureAsync(CollectorBehavior.BlackHole);
        var refused = await MeasureAsync(CollectorBehavior.Refused);
        foreach (var run in new[] { baseline, blackHole, refused })
        {
            TestContext.WriteLine(run.ToString());
        }

        baseline.Evidence.Should().BeGreaterThan(0, "the reachable collector received exports");
        blackHole.Evidence.Should().BeGreaterThan(0, "the exporter connected to the collector that never answers");
        refused.Evidence.Should().BeGreaterThan(0, "the exporter reported failed exports to the closed port");

        foreach (var run in new[] { baseline, blackHole, refused })
        {
            run.IdleStops.Concat(run.MovingStops).Should().OnlyContain(t => t < StopBound, "every Stop completes in under 1 s ({0})", run.Collector);
        }

        foreach (var outage in new[] { blackHole, refused })
        {
            P99(outage.IdleStops).Should().BeLessThanOrEqualTo(P99(baseline.IdleStops) + Margin, "idle Stop ({0})", outage.Collector);
            P99(outage.MovingStops).Should().BeLessThanOrEqualTo(P99(baseline.MovingStops) + Margin, "Stop during a move ({0})", outage.Collector);
            P99(outage.RelaysOff).Should().BeLessThanOrEqualTo(P99(baseline.RelaysOff) + Margin, "the relays off after Stop ({0})", outage.Collector);
            outage.LimitStops.Max().Should().BeLessThanOrEqualTo(baseline.LimitStops.Max() + Margin, "the limit stop ({0})", outage.Collector);
            outage.WatchdogOverrun.Should().BeLessThanOrEqualTo(baseline.WatchdogOverrun + Margin, "the watchdog stop ({0})", outage.Collector);
        }
    }

    private static async Task<Run> MeasureAsync(CollectorBehavior behavior)
    {
        await using var collector = OtlpCollectorStub.Start(behavior);
        using var failures = new OtlpExporterFailures();
        await using var rig = await EmulatedRoofRig.StartAsync(
            Scenario.Production(travelMeters: 1.0, settings: new Dictionary<string, string?>(collector.Settings())) with { Kestrel = true });
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        for (var i = 0; i < WarmUpStops; i++)
        {
            await TimedStopAsync(client);
        }

        var idle = new List<TimeSpan>();
        for (var i = 0; i < IdleStops; i++)
        {
            idle.Add((await TimedStopAsync(client)).Elapsed);
        }

        // Off the closed limit first, so the timed moves run in mid-travel and the limit stop has a roof to close.
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.1, "the roof to leave the closed limit");
        await client.AcceptedAsync("Stop");
        await rig.WaitForRestAsync("the Stop");

        var moving = new List<TimeSpan>();
        var relaysOff = new List<TimeSpan>();
        for (var i = 0; i < MovingStops; i++)
        {
            await client.AcceptedAsync("Open");
            await rig.WaitForPlantAsync(p => p.OutputFrequencyHz > 0, "the drive to run");
            var sent = rig.Plant.Elapsed / rig.Options.TimeScale;
            var (elapsed, status) = await TimedStopAsync(client);
            status.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
            moving.Add(elapsed);
            relaysOff.Add(rig.ContactChanges(1).First(c => !c.Closed && c.At >= sent).At - sent);
            await rig.WaitForRestAsync("the Stop");
        }

        var limitStops = new List<TimeSpan> { await LimitStopAsync(rig, client) };

        await rig.ConfigureAsync(r => r with { SafetyWatchdogTimeoutSeconds = WatchdogSeconds });
        await client.AcceptedAsync("Open");
        (await rig.WaitForRestAsync("the watchdog stop")).LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        var forward = rig.ContactChanges(1).TakeLast(2).ToArray();
        var overrun = forward[1].At - forward[0].At - TimeSpan.FromSeconds(WatchdogSeconds);
        await rig.ConfigureAsync(r => r with { SafetyWatchdogTimeoutSeconds = ProductionWatchdogSeconds });
        await client.AcceptedAsync("ClearFault");
        limitStops.Add(await LimitStopAsync(rig, client));

        rig.Session.Plant.Violations.Should().BeEmpty();
        var evidence = behavior switch
        {
            CollectorBehavior.Reachable => collector.Exports,
            CollectorBehavior.BlackHole => collector.Connections,
            _ => failures.Count
        };
        return new Run(behavior, idle, moving, relaysOff, limitStops, overrun, evidence);
    }

    /// <summary>Closes the roof onto its limit and returns the time from the closed limit input (IN2) to the relay off.</summary>
    private static async Task<TimeSpan> LimitStopAsync(EmulatedRoofRig rig, HttpClient client)
    {
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        var started = rig.ContactChanges(2).Last(c => c.Closed).At;
        var reached = rig.EventTimes(PlantEventKind.Input, "IN2 HIGH").First(t => t > started);
        var off = rig.ContactChanges(2).First(c => !c.Closed && c.At > started).At;
        return off - reached;
    }

    private static async Task<(TimeSpan Elapsed, RoofStatusResponse Status)> TimedStopAsync(HttpClient client)
    {
        var clock = Stopwatch.StartNew();
        using var response = await client.PostAsync($"{Scenario.RoofApi}/Stop", content: null);
        var status = await ApiJson.ReadAsync<RoofStatusResponse>(response);
        var elapsed = clock.Elapsed;
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Stop must be accepted");
        status.CommandedMotion.Should().Be(RoofMotionDirection.None);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(0);
        return (elapsed, status);
    }

    /// <summary>The 99th percentile by nearest rank (the largest of fewer than 100 samples).</summary>
    private static TimeSpan P99(IReadOnlyList<TimeSpan> samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted[(int)Math.Ceiling(0.99 * sorted.Length) - 1];
    }

    private sealed record Run(
        CollectorBehavior Collector,
        IReadOnlyList<TimeSpan> IdleStops,
        IReadOnlyList<TimeSpan> MovingStops,
        IReadOnlyList<TimeSpan> RelaysOff,
        IReadOnlyList<TimeSpan> LimitStops,
        TimeSpan WatchdogOverrun,
        int Evidence)
    {
        public override string ToString()
            => $"C13 collector {Collector}: idle Stop p50 {Ms(Median(IdleStops))} p99 {Ms(P99(IdleStops))}; "
                + $"Stop during a move p50 {Ms(Median(MovingStops))} p99 {Ms(P99(MovingStops))}; "
                + $"relays off after Stop p99 {Ms(P99(RelaysOff))}; limit stop (IN2 to relay off) max {Ms(LimitStops.Max())}; "
                + $"watchdog overrun {Ms(WatchdogOverrun)}; evidence {Evidence}";

        private static TimeSpan Median(IReadOnlyList<TimeSpan> samples) => samples.Order().ElementAt(samples.Count / 2);

        private static string Ms(TimeSpan value) => $"{value.TotalMilliseconds:F1} ms";
    }
}
