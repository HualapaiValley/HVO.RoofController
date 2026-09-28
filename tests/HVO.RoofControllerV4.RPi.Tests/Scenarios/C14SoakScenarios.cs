using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Camera;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C14 (RV-6), the soak: the production settings against the emulated plant, cycling Open, Stop, Close, Stop through
/// the API as the documented soak script does, with status polling at a client's rate, a camera stream opened and
/// closed periodically, and OTLP export to a collector that never answers (the hour-long outage of C13 step 4). Every
/// fifth cycle stops in mid-travel first. It samples the process's resources and the controller's health, and checks
/// the C14 pass criteria and the plant's invariants.
/// </summary>
/// <remarks>
/// <para>
/// In real time, with the roof shortened to 25 cm: a cycle takes about 10 s where the installation moves about twice a
/// night, so an hour of soak is about a year of moves.
/// </para>
/// <para>
/// The run is <see cref="DefaultDuration"/> in the scenario job, and <c>HVO_SOAK_DURATION</c> (a time span such as
/// <c>02:00:00</c>) in the nightly soak. The results (<c>soak-summary.md</c>, <c>soak-summary.json</c>,
/// <c>soak-samples.csv</c>, and <c>soak-log.txt</c> with every Warning or above and the latest log entries) go to
/// <c>HVO_SOAK_RESULTS_DIR</c>, or the test results, and are written before the checks fail the test. The resources are
/// the test process's, which runs the emulator and the controller: a leak in either fails the soak.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
[TestCategory(Scenario.SoakCategory)]
public sealed class C14SoakScenarios
{
    private const string DurationVariable = "HVO_SOAK_DURATION";
    private const string ResultsVariable = "HVO_SOAK_RESULTS_DIR";
    private const int MidTravelStopEvery = 5;
    private const double TravelMeters = 0.25;
    private const double MemoryGrowthLimit = 0.10;
    private const int ThreadMargin = 10;
    private const int FileDescriptorMargin = 10;
    private const int EndSamples = 5;
    private const int MinimumTrendSamples = 10;
    private const int LogCapacity = 1_000;
    private const int MaxNotes = 100;

    // A quarter's 99th percentile is compared only when every quarter has this many Stops (a soak of about half an hour
    // or more); in fewer it is little more than the quarter's slowest Stop.
    private const int MinimumPercentileStops = 100;
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(90);

    // The resources are assessed over at least this long after the warm-up; a shorter run (the scenario job) records them.
    private static readonly TimeSpan MinimumTrendWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CameraHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StreamCloseWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopLatencyMargin = TimeSpan.FromMilliseconds(100);

    // The controller's read staleness limit with the production settings: max(3 x the 1 s verification interval, 5 s).
    private static readonly TimeSpan ReadAgeLimit = TimeSpan.FromSeconds(5);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [CommissioningCheck("C14")]
    [CommissioningCheck("C13", "4")]
    public async Task TheRoof_CyclesForTheSoakDuration_WithFlatResources_AndEveryInvariantHeld()
    {
        var duration = SoakDuration();
        var warmUp = TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(1).Ticks, duration.Ticks / 4));
        var sampleEvery = Clamp(duration / 60, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        var cameraEvery = Clamp(duration / 30, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2));

        using var counters = new RoofMeterCounters();
        await using var collector = OtlpCollectorStub.Start(CollectorBehavior.BlackHole);
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            // A short history, so the emulator's event log reaches its size limit during the warm-up; the rig keeps the
            // latest log entries, and the host writes no console log, which the test framework would keep in memory
            // for the result. None then grows the heap the soak measures; soak-log.txt has the entries the rig kept.
            plant: p => p with { MaxHistory = 1_000 },
            settings: new Dictionary<string, string?>(collector.Settings()),
            travelMeters: TravelMeters) with { Kestrel = true, Camera = true, LogCapacity = LogCapacity, ConsoleLog = false });
        using var operatorClient = rig.CreateApiClient(TestApiKeys.Operator);
        using var viewer = rig.CreateApiClient(TestApiKeys.Viewer);
        using var anonymous = rig.CreateApiClient();

        var run = new SoakRun(duration, warmUp);
        var limitEventsAtStart = LimitEvents(counters);
        var safetyStopsAtStart = counters.TotalsBy("roof.controller.safety.stops", "roof.stop.reason");
        using var stopping = new CancellationTokenSource();
        var polling = PollStatusAsync(viewer, run, stopping.Token);
        var watching = WatchCameraAsync(viewer, run, cameraEvery, stopping.Token);

        var clock = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        Exception? cycleFailure = null;
        try
        {
            while (clock.Elapsed < duration)
            {
                await CycleAsync(rig, operatorClient, run);
                if (clock.Elapsed >= nextSample)
                {
                    run.Samples.Add(await SampleAsync(rig, viewer, anonymous, collector, run, clock.Elapsed));
                    nextSample = clock.Elapsed + sampleEvery;
                }
            }

            run.Samples.Add(await SampleAsync(rig, viewer, anonymous, collector, run, clock.Elapsed));
        }
        catch (Exception exception)
        {
            cycleFailure = exception;
        }

        await stopping.CancelAsync();
        await Task.WhenAll(polling, watching);
        run.Elapsed = clock.Elapsed;
        await WaitForCameraStreamsToCloseAsync(rig);

        var checks = Evaluate(run, rig, counters, limitEventsAtStart, safetyStopsAtStart, collector, cycleFailure);
        var results = WriteResults(run, checks, counters, rig);
        TestContext.WriteLine(File.ReadAllText(Path.Combine(results, "soak-summary.md")));

        checks.Where(c => !c.Passed).Should().BeEmpty("every C14 criterion must hold (results in {0})", results);
    }

    /// <summary>Open to the open limit, Stop, Close to the closed limit, Stop; every fifth cycle stops in mid-travel first.</summary>
    private static async Task CycleAsync(EmulatedRoofRig rig, HttpClient client, SoakRun run)
    {
        var cycle = run.Cycles + 1;
        if (cycle % MidTravelStopEvery == 0)
        {
            await client.AcceptedAsync("Open");
            await rig.WaitForPlantAsync(p => p.PositionMeters > TravelMeters / 2, "the roof to reach mid-travel");
            (await client.AcceptedAsync("Stop")).LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
            await rig.WaitForRestAsync("the mid-travel Stop");
            run.MidTravelStops++;
        }

        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        await TimedStopAsync(client, run);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        await TimedStopAsync(client, run);
        run.Cycles = cycle;
    }

    private static async Task TimedStopAsync(HttpClient client, SoakRun run)
    {
        var sent = Stopwatch.GetTimestamp();
        var status = await client.AcceptedAsync("Stop");
        run.StopLatencies.Add((run.Clock.Elapsed, Stopwatch.GetElapsedTime(sent)));
        status.CommandedMotion.Should().Be(RoofMotionDirection.None);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(0);
    }

    /// <summary>Status polling at a client's rate: every answer must succeed, and <c>statusVersion</c> must never go back.</summary>
    private static async Task PollStatusAsync(HttpClient viewer, SoakRun run, CancellationToken stopping)
    {
        var lastVersion = long.MinValue;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var response = await viewer.GetAsync($"{Scenario.RoofApi}/Status", stopping);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    run.Note($"Status answered {(int)response.StatusCode}");
                    Interlocked.Increment(ref run.StatusFailures);
                }
                else
                {
                    var status = await ApiJson.ReadAsync<RoofStatusResponse>(response);
                    if (status.StatusVersion < lastVersion)
                    {
                        run.Note($"statusVersion went back from {lastVersion} to {status.StatusVersion}");
                        Interlocked.Increment(ref run.StatusVersionRegressions);
                    }

                    lastVersion = Math.Max(lastVersion, status.StatusVersion);
                    Interlocked.Increment(ref run.StatusPolls);
                }

                await Task.Delay(StatusPollInterval, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException exception)
            {
                run.Note($"Status request failed: {exception.Message}");
                Interlocked.Increment(ref run.StatusFailures);
            }
        }
    }

    /// <summary>A viewer opens the camera through the proxy every <paramref name="every"/>, watches it for a few seconds and leaves.</summary>
    private static async Task WatchCameraAsync(HttpClient viewer, SoakRun run, TimeSpan every, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var hold = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                hold.CancelAfter(CameraHold);
                var frames = 0;
                try
                {
                    using var response = await viewer.GetAsync("/api/v1.0/Camera/2/mjpeg", HttpCompletionOption.ResponseHeadersRead, hold.Token);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        run.Note($"The camera stream answered {(int)response.StatusCode}");
                        Interlocked.Increment(ref run.CameraFailures);
                    }
                    else
                    {
                        var reader = new MjpegReader(await response.Content.ReadAsStreamAsync(hold.Token), EmulatedCamera.Boundary);
                        while (await reader.ReadPartAsync(hold.Token) is not null)
                        {
                            frames++;
                        }
                    }
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    // The viewer leaves after the hold.
                }

                if (!stopping.IsCancellationRequested)
                {
                    Interlocked.Increment(ref run.CameraStreams);
                    Interlocked.Add(ref run.CameraFrames, frames);
                    if (frames == 0)
                    {
                        run.Note("A camera stream delivered no frame");
                        Interlocked.Increment(ref run.CameraFailures);
                    }
                }

                await Task.Delay(every - CameraHold, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                run.Note($"The camera stream failed: {exception.Message}");
                Interlocked.Increment(ref run.CameraFailures);
            }
        }
    }

    private static async Task<Sample> SampleAsync(
        EmulatedRoofRig rig, HttpClient viewer, HttpClient anonymous, OtlpCollectorStub collector, SoakRun run, TimeSpan at)
    {
        using var ready = await anonymous.GetAsync("/health/ready");
        using var response = await viewer.GetAsync($"{Scenario.RoofApi}/Status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await ApiJson.ReadAsync<RoofStatusResponse>(response);
        var now = DateTimeOffset.UtcNow;

        var managed = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        return new Sample(
            At: at,
            Cycles: run.Cycles,
            ManagedBytes: managed,
            WorkingSetBytes: process.WorkingSet64,
            Threads: process.Threads.Count,
            FileDescriptors: OperatingSystem.IsLinux() ? Directory.GetFiles("/proc/self/fd").Length : process.HandleCount,
            Ready: ready.StatusCode == HttpStatusCode.OK,
            StatusVersion: status.StatusVersion,
            InputsHealthy: status.InputsHealthy,
            InputReadAgeSeconds: (now - (status.LastSuccessfulInputReadUtc ?? DateTimeOffset.MinValue)).TotalSeconds,
            ConsecutiveInputReadFailures: status.ConsecutiveInputReadFailures,
            RelayReadsHealthy: status.RelayRegisterReadsHealthy,
            RelayReadAgeSeconds: (now - (status.LastSuccessfulRelayReadUtc ?? DateTimeOffset.MinValue)).TotalSeconds,
            ConsecutiveRelayReadFailures: status.ConsecutiveRelayReadFailures,
            FaultLatched: status.IsFaultLatched,
            Violations: rig.Plant.Violations,
            LogEntries: rig.Logs.Count,
            HeldCollectorConnections: collector.Held);
    }

    private static List<Check> Evaluate(
        SoakRun run,
        EmulatedRoofRig rig,
        RoofMeterCounters counters,
        IReadOnlyDictionary<string, long> limitEventsAtStart,
        IReadOnlyDictionary<string, long> safetyStopsAtStart,
        OtlpCollectorStub collector,
        Exception? cycleFailure)
    {
        var checks = new List<Check>
        {
            new("Every cycle ended as commanded",
                cycleFailure is null && run.Cycles > 0,
                cycleFailure is null
                    ? $"{run.Cycles} cycles: each move ended at its limit with LimitSwitchReached, {run.MidTravelStops} mid-travel Stops with NormalStop, and every Stop verified all relays off"
                    : $"cycle {run.Cycles + 1} failed: {cycleFailure.Message}")
        };

        var limitEvents = LimitEvents(counters);
        var deltas = limitEvents.ToDictionary(e => e.Key, e => e.Value - limitEventsAtStart.GetValueOrDefault(e.Key));
        var eventText = string.Join(", ", LimitEventKeys.Select(k => $"{k} {deltas.GetValueOrDefault(k)}"));
        checks.Add(new("Limit events match the cycles one for one",
            LimitEventKeys.All(k => deltas.GetValueOrDefault(k) == run.Cycles) && deltas.Keys.All(LimitEventKeys.Contains),
            $"{eventText}; {run.Cycles} cycles"));

        var safetyStops = counters.TotalsBy("roof.controller.safety.stops", "roof.stop.reason")
            .ToDictionary(s => s.Key, s => s.Value - safetyStopsAtStart.GetValueOrDefault(s.Key))
            .Where(s => s.Value != 0)
            .ToDictionary(s => s.Key, s => s.Value);
        var limitStops = safetyStops.GetValueOrDefault(nameof(RoofControllerStopReason.LimitSwitchReached));
        checks.Add(new("Only limit arrivals stopped the roof for safety",
            safetyStops.Keys.All(k => k == nameof(RoofControllerStopReason.LimitSwitchReached)) && limitStops == 2L * run.Cycles,
            safetyStops.Count == 0 ? "none" : string.Join(", ", safetyStops.Select(s => $"{s.Key} {s.Value}"))));

        var serious = rig.Logs.Serious.Where(e => e.Level >= LogLevel.Error).ToArray();
        checks.Add(new("No fault latch, Error or Critical log entry",
            serious.Length == 0 && run.Samples.All(s => !s.FaultLatched),
            serious.Length == 0 ? "none" : string.Join(" | ", serious.Take(5).Select(e => $"{e.Level}: {e.Message}"))));

        var violations = rig.Session.Plant.Violations;
        checks.Add(new("No plant invariant broken",
            violations.Count == 0,
            violations.Count == 0 ? "none" : string.Join(" | ", violations.Take(5).Select(v => $"{v.Kind}: {v.Detail}"))));

        checks.Add(new("Status answered, and statusVersion only increased",
            run.StatusFailures == 0 && run.StatusVersionRegressions == 0 && run.StatusPolls > 0,
            $"{run.StatusPolls} polls, {run.StatusFailures} failed, {run.StatusVersionRegressions} regressions"));

        var unhealthy = run.Samples.Where(s => !s.Ready || !s.InputsHealthy || !s.RelayReadsHealthy
            || s.ConsecutiveInputReadFailures != 0 || s.ConsecutiveRelayReadFailures != 0
            || s.InputReadAgeSeconds > ReadAgeLimit.TotalSeconds || s.RelayReadAgeSeconds > ReadAgeLimit.TotalSeconds).ToArray();
        checks.Add(new("Ready, with healthy and fresh input and relay reads, at every sample",
            unhealthy.Length == 0 && run.Samples.Count > 0,
            $"{run.Samples.Count} samples; oldest input read {Max(run.Samples, s => s.InputReadAgeSeconds):0.00} s, oldest relay read {Max(run.Samples, s => s.RelayReadAgeSeconds):0.00} s"
            + (unhealthy.Length == 0 ? string.Empty : $"; first unhealthy at {unhealthy[0].At:hh\\:mm\\:ss}")));

        checks.Add(StopLatencyCheck(run));
        checks.AddRange(ResourceChecks(run));

        var openStreams = rig.Camera.GetStatus().OpenStreams;
        checks.Add(new("The camera streamed through the proxy, and every stream closed",
            run.CameraStreams > 0 && run.CameraFailures == 0 && openStreams == 0,
            $"{run.CameraStreams} streams, {run.CameraFrames} frames, {run.CameraFailures} failed, {openStreams} still open {StreamCloseWait.TotalSeconds:0} s after the soak"));

        checks.Add(new("The exporter kept trying the collector that never answers",
            collector.Connections > 0,
            $"{collector.Connections} connections accepted and never answered, {collector.Held} still held"));
        return checks;
    }

    /// <summary>
    /// Every Stop under 1 s, and no drift through the outage: each quarter's median, and its 99th percentile once every
    /// quarter has <see cref="MinimumPercentileStops"/> Stops, within 100 ms (the C13 margin) of the first quarter's.
    /// C13 compares Stops during an outage with a reachable collector.
    /// </summary>
    private static Check StopLatencyCheck(SoakRun run)
    {
        var stops = run.StopLatencies.ToArray();
        if (stops.Length == 0)
        {
            return new Check("Stop latency did not drift", false, "no Stop was timed");
        }

        var quarters = Enumerable.Range(0, 4)
            .Select(q => stops.Where(s => s.At >= run.Elapsed * q / 4 && (q == 3 || s.At < run.Elapsed * (q + 1) / 4)).Select(s => s.Latency).ToArray())
            .Where(q => q.Length > 0)
            .ToArray();
        var medians = quarters.Select(q => TimeSpan.FromTicks((long)Median(q.Select(l => (double)l.Ticks)))).ToArray();
        var percentiles = quarters.All(q => q.Length >= MinimumPercentileStops) ? quarters.Select(P99).ToArray() : [];
        var slowest = stops.Max(s => s.Latency);
        return new Check("Stop latency did not drift",
            slowest < StopBound
            && medians.All(m => m <= medians[0] + StopLatencyMargin)
            && percentiles.All(p => p <= percentiles[0] + StopLatencyMargin),
            $"{stops.Length} Stops, slowest {slowest.TotalMilliseconds:0} ms; by quarter: median {Milliseconds(medians)}, "
            + (percentiles.Length > 0
                ? $"99th percentile {Milliseconds(percentiles)}"
                : $"99th percentile not compared (fewer than {MinimumPercentileStops} Stops in a quarter: {string.Join(", ", quarters.Select(q => q.Length))})"));
    }

    private static string Milliseconds(IEnumerable<TimeSpan> values) => string.Join(", ", values.Select(v => $"{v.TotalMilliseconds:0} ms"));

    /// <summary>Waits up to <see cref="StreamCloseWait"/> for the proxy to notice that the soak's last viewer left.</summary>
    private static async Task WaitForCameraStreamsToCloseAsync(EmulatedRoofRig rig)
    {
        var waited = Stopwatch.StartNew();
        while (rig.Camera.GetStatus().OpenStreams != 0 && waited.Elapsed < StreamCloseWait)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// After the warm-up (a quarter of the run, at most an hour): the memory grows less than 10% (C14), as the working
    /// set that <c>docker stats</c> reports and as the managed heap after a full collection, which shows a leak the
    /// working set hides; neither trends upward; and the threads and file descriptors stay flat. A run too short for a
    /// trend (<see cref="MinimumTrendWindow"/>, <see cref="MinimumTrendSamples"/> samples) records them only.
    /// </summary>
    private static IEnumerable<Check> ResourceChecks(SoakRun run)
    {
        var settled = run.Samples.Where(s => s.At >= run.WarmUp).ToArray();
        if (settled.Length < MinimumTrendSamples || settled[^1].At - settled[0].At < MinimumTrendWindow)
        {
            var span = settled.Length == 0 ? TimeSpan.Zero : settled[^1].At - settled[0].At;
            var all = run.Samples;
            yield return new Check("Resources flat after the warm-up", true,
                $"not assessed: {settled.Length} samples over {span:hh\\:mm\\:ss} after the {run.WarmUp:hh\\:mm\\:ss} warm-up, "
                + $"under {MinimumTrendSamples} samples or {MinimumTrendWindow.TotalMinutes:0} minutes (the nightly soak assesses them); "
                + (all.Count == 0 ? "no samples" : $"managed heap {all[0].ManagedBytes / 1048576.0:0.0} MB to {all[^1].ManagedBytes / 1048576.0:0.0} MB, "
                    + $"working set {all[0].WorkingSetBytes / 1048576.0:0} MB to {all[^1].WorkingSetBytes / 1048576.0:0} MB, "
                    + $"threads {all[0].Threads} to {all[^1].Threads}, file descriptors {all[0].FileDescriptors} to {all[^1].FileDescriptors}"));
            yield break;
        }

        var first = settled.Take(EndSamples).ToArray();
        var last = settled.TakeLast(EndSamples).ToArray();
        yield return MemoryCheck("Working set grew less than 10% after the warm-up", settled, first, last, s => s.WorkingSetBytes);
        yield return MemoryCheck("Managed heap grew less than 10% after the warm-up", settled, first, last, s => s.ManagedBytes);

        var threadsStart = Median(first.Select(s => (double)s.Threads));
        var threadsEnd = Median(last.Select(s => (double)s.Threads));
        var fdsStart = Median(first.Select(s => (double)s.FileDescriptors));
        var fdsEnd = Median(last.Select(s => (double)s.FileDescriptors));
        yield return new Check("Threads and file descriptors flat after the warm-up",
            threadsEnd <= threadsStart + ThreadMargin && fdsEnd <= fdsStart + FileDescriptorMargin,
            $"threads {threadsStart:0} to {threadsEnd:0}, file descriptors {fdsStart:0} to {fdsEnd:0}");
    }

    /// <summary>
    /// The median of the last samples against the first grows less than 10%, and so does the least-squares trend over
    /// the settled run (a leak that the end samples' noise hides).
    /// </summary>
    private static Check MemoryCheck(string name, Sample[] settled, Sample[] first, Sample[] last, Func<Sample, long> bytes)
    {
        var start = Median(first.Select(s => (double)bytes(s)));
        var end = Median(last.Select(s => (double)bytes(s)));
        var growth = (end - start) / start;
        var slope = Slope(settled.Select(s => (s.At.TotalSeconds, (double)bytes(s))).ToArray());
        var trend = slope * (settled[^1].At - settled[0].At).TotalSeconds / start;
        return new Check(name,
            growth < MemoryGrowthLimit && trend < MemoryGrowthLimit,
            $"{start / 1048576:0.0} MB to {end / 1048576:0.0} MB ({growth:+0.0%;-0.0%}), trend {trend:+0.0%;-0.0%} over {settled[^1].At - settled[0].At:hh\\:mm\\:ss}");
    }

    private static readonly string[] LimitEventKeys = ["closed cleared", "open reached", "open cleared", "closed reached"];
    private static readonly (string, string) FullTravel = ("roof.stop.reason", nameof(RoofControllerStopReason.LimitSwitchReached));
    private static readonly (string, string) FromLimit = ("roof.travel.from_limit", "true");

    private static Dictionary<string, long> LimitEvents(RoofMeterCounters counters)
        => new[] { "open", "closed" }
            .SelectMany(limit => new[] { "reached", "cleared" }.Select(state => (limit, state)))
            .ToDictionary(
                e => $"{e.limit} {e.state}",
                e => counters.Total("roof.controller.limit.switch.events", ("roof.limit.switch", e.limit), ("roof.limit.state", e.state)));

    private string WriteResults(SoakRun run, IReadOnlyList<Check> checks, RoofMeterCounters counters, EmulatedRoofRig rig)
    {
        var directory = Environment.GetEnvironmentVariable(ResultsVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "soak");
        Directory.CreateDirectory(directory);

        var timing = new (string Name, RoofMeterCounters.Distribution Values)[]
        {
            ("Full travel, opening", counters.Values("roof.controller.travel.duration", ("roof.direction", "opening"), FullTravel, FromLimit)),
            ("Full travel, closing", counters.Values("roof.controller.travel.duration", ("roof.direction", "closing"), FullTravel, FromLimit)),
            ("Drive start delay (IN4)", counters.Values("roof.controller.drive.start_delay")),
            ("Drive stop delay (IN4)", counters.Values("roof.controller.drive.stop_delay")),
            ("Start limit release", counters.Values("roof.controller.departure.release"))
        };

        var markdown = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"## C14 soak: {(checks.All(c => c.Passed) ? "PASS" : "FAIL")}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{run.Elapsed:hh\\:mm\\:ss} of {run.Duration:hh\\:mm\\:ss}, {run.Cycles} cycles ({run.MidTravelStops} with a mid-travel Stop), warm-up {run.WarmUp:hh\\:mm\\:ss}, {run.Samples.Count} samples. The production settings against the emulated plant ({TravelMeters} m of travel, real time), OTLP export to a collector that never answers.")
            .AppendLine()
            .AppendLine("| Check | Result | Detail |")
            .AppendLine("|---|---|---|");
        foreach (var check in checks)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {check.Name} | {(check.Passed ? "pass" : "**FAIL**")} | {check.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        markdown.AppendLine()
            .AppendLine("| Motion timing (s) | Count | Mean | Max |")
            .AppendLine("|---|---|---|---|");
        foreach (var (name, values) in timing)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {values.Count} | {values.Mean:0.000} | {(values.Count == 0 ? 0 : values.Max):0.000} |");
        }

        if (!run.Notes.IsEmpty)
        {
            markdown.AppendLine().AppendLine("Notes (first 20):").AppendLine();
            foreach (var note in run.Notes.Take(20))
            {
                markdown.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
            }
        }

        File.WriteAllText(Path.Combine(directory, "soak-summary.md"), markdown.ToString());
        File.WriteAllText(Path.Combine(directory, "soak-summary.json"), JsonSerializer.Serialize(new
        {
            passed = checks.All(c => c.Passed),
            elapsedSeconds = run.Elapsed.TotalSeconds,
            durationSeconds = run.Duration.TotalSeconds,
            warmUpSeconds = run.WarmUp.TotalSeconds,
            cycles = run.Cycles,
            midTravelStops = run.MidTravelStops,
            checks = checks.Select(c => new { name = c.Name, passed = c.Passed, detail = c.Detail }),
            motionTiming = timing.Select(t => new { name = t.Name, count = t.Values.Count, mean = t.Values.Mean, max = t.Values.Count == 0 ? 0 : t.Values.Max }),
            logEntries = rig.Logs.Count,
            seriousLogEntries = rig.Logs.Serious.GroupBy(e => e.Level.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            notes = run.Notes.Take(100)
        }, new JsonSerializerOptions { WriteIndented = true }));

        var csv = new StringBuilder("seconds,cycles,managed_bytes,working_set_bytes,threads,file_descriptors,ready,status_version,inputs_healthy,input_read_age_s,input_read_failures,relay_reads_healthy,relay_read_age_s,relay_read_failures,fault_latched,violations,log_entries,held_collector_connections\n");
        foreach (var s in run.Samples)
        {
            csv.AppendLine(string.Join(',',
                s.At.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture), s.Cycles, s.ManagedBytes, s.WorkingSetBytes, s.Threads, s.FileDescriptors,
                s.Ready, s.StatusVersion, s.InputsHealthy, s.InputReadAgeSeconds.ToString("0.000", CultureInfo.InvariantCulture), s.ConsecutiveInputReadFailures,
                s.RelayReadsHealthy, s.RelayReadAgeSeconds.ToString("0.000", CultureInfo.InvariantCulture), s.ConsecutiveRelayReadFailures,
                s.FaultLatched, s.Violations, s.LogEntries, s.HeldCollectorConnections));
        }

        File.WriteAllText(Path.Combine(directory, "soak-samples.csv"), csv.ToString());

        var serious = rig.Logs.Serious;
        var latest = rig.Logs.Entries;
        File.WriteAllLines(Path.Combine(directory, "soak-log.txt"), new[] { $"Every Warning or above ({serious.Count}):" }
            .Concat(serious.Select(FormatLogEntry))
            .Append(string.Empty)
            .Append($"The latest {latest.Count} of {rig.Logs.Count} entries:")
            .Concat(latest.Select(FormatLogEntry)));
        foreach (var file in new[] { "soak-summary.md", "soak-summary.json", "soak-samples.csv", "soak-log.txt" })
        {
            TestContext.AddResultFile(Path.Combine(directory, file));
        }

        return directory;
    }

    private static string FormatLogEntry((string Category, LogLevel Level, string Message) entry)
        => $"{entry.Level} {entry.Category}: {entry.Message}";

    private static TimeSpan SoakDuration()
    {
        var configured = Environment.GetEnvironmentVariable(DurationVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultDuration;
        }

        return TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var duration) && duration > TimeSpan.Zero
            ? duration
            : throw new ArgumentException($"{DurationVariable} must be a positive time span such as 02:00:00, not '{configured}'.");
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private static TimeSpan P99(TimeSpan[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double Max(IEnumerable<Sample> samples, Func<Sample, double> value) => samples.Select(value).DefaultIfEmpty(0).Max();

    /// <summary>The least-squares slope of y over x.</summary>
    private static double Slope((double X, double Y)[] points)
    {
        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var covariance = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var variance = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return variance == 0 ? 0 : covariance / variance;
    }

    private sealed record Check(string Name, bool Passed, string Detail);

    private sealed record Sample(
        TimeSpan At,
        int Cycles,
        long ManagedBytes,
        long WorkingSetBytes,
        int Threads,
        int FileDescriptors,
        bool Ready,
        long StatusVersion,
        bool InputsHealthy,
        double InputReadAgeSeconds,
        int ConsecutiveInputReadFailures,
        bool RelayReadsHealthy,
        double RelayReadAgeSeconds,
        int ConsecutiveRelayReadFailures,
        bool FaultLatched,
        int Violations,
        long LogEntries,
        int HeldCollectorConnections);

    private sealed class SoakRun(TimeSpan duration, TimeSpan warmUp)
    {
        public TimeSpan Duration { get; } = duration;

        public TimeSpan WarmUp { get; } = warmUp;

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public TimeSpan Elapsed { get; set; }

        public int Cycles { get; set; }

        public int MidTravelStops { get; set; }

        public List<Sample> Samples { get; } = [];

        public System.Collections.Concurrent.ConcurrentBag<(TimeSpan At, TimeSpan Latency)> StopLatencies { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<string> Notes { get; } = new();

        public int StatusPolls;
        public int StatusFailures;
        public int StatusVersionRegressions;
        public int CameraStreams;
        public int CameraFrames;
        public int CameraFailures;

        public void Note(string note)
        {
            if (Notes.Count < MaxNotes)
            {
                Notes.Enqueue($"{Clock.Elapsed:hh\\:mm\\:ss} {note}");
            }
        }
    }
}
