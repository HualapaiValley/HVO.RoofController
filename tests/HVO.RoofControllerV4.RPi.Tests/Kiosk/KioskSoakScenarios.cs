using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Screens;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's soak (#47): the kiosk's console and its screen (the headless shell at the Touch Display 2's size, drawn as
/// it changes) against the emulated roof with the production settings, with the status feed's production reconnect
/// delays. Each cycle unlocks with a PIN, touches Open, touches Stop in mid-travel, touches Close (the kiosk renews the
/// lease until the roof reaches its closed limit) and locks; every <see cref="RestartEvery"/> cycles the controller
/// restarts, and the kiosk must reconnect once and show the live status again. It samples the managed heap (after a full
/// collection), the process's threads and the feed's connections, and checks that every Stop was acknowledged, each
/// restart was one reconnect within <see cref="ReconnectBound"/>, and the heap and threads are flat after the warm-up.
/// </summary>
/// <remarks>
/// The run is <see cref="DefaultDuration"/> in the scenario job, and <c>HVO_KIOSK_SOAK_DURATION</c> (a time span such as
/// <c>00:30:00</c>) in the nightly kiosk soak. The heap and threads are assessed only when the run leaves at least
/// <see cref="MinimumTrendWindow"/> after the warm-up; a shorter run records them. The results
/// (<c>kiosk-soak-summary.md</c>, <c>kiosk-soak-samples.csv</c>) go to <c>HVO_SOAK_RESULTS_DIR</c>, or the test results,
/// and are written before the checks fail the test; the last screen (its draws, its layout and Stop) is one of the checks
/// (<see cref="FinishAsync"/>). The resources are the test process's, which runs the emulator, the
/// controller and the kiosk: a leak in any of them fails the soak. A restart leaves no stopped controller behind: the rig
/// disposes the rate limiters ASP.NET Core leaves running, which would hold each one (<see cref="PipelineRateLimiters"/>).
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
[TestCategory(Category)]
public sealed class KioskSoakScenarios
{
    /// <summary>The kiosk soak alone, as the nightly job runs it; it is not the controller's <see cref="Scenario.SoakCategory"/>.</summary>
    public const string Category = "KioskSoak";

    private const string DurationVariable = "HVO_KIOSK_SOAK_DURATION";
    private const string ResultsVariable = "HVO_SOAK_RESULTS_DIR";
    private const string Operator = "olga";
    private const double TravelMeters = 0.25;
    private const int RestartEvery = 4;
    private const double MemoryGrowthLimit = 0.10;
    private const long MemorySlackBytes = 4L * 1024 * 1024;
    private const int ThreadMargin = 10;
    private const int EndSamples = 3;

    internal const string SummaryFile = "kiosk-soak-summary.md";
    internal const string SamplesFile = "kiosk-soak-samples.csv";

    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinimumTrendWindow = TimeSpan.FromMinutes(5);

    // The feed's production delays: 1 s, doubling to 30 s, with jitter. A restart takes a few seconds, so the kiosk is
    // back within a few attempts; the bound allows the longest delay and a connection.
    private static readonly TimeSpan ReconnectBound = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MotionBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PumpEvery = TimeSpan.FromMilliseconds(50);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheKiosk_MovesAndStopsTheRoof_ThroughControllerRestarts_WithFlatResources()
    {
        var duration = SoakDuration();
        var warmUp = TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(1).Ticks, duration.Ticks / 4));
        var sampleEvery = Clamp(duration / 60, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));

        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: KioskKeySettings(), travelMeters: TravelMeters)
            with { LogCapacity = 1_000, ConsoleLog = false });
        await AddOperatorAsync(rig);
        TestServer? server = rig.App.Server;
        TestServer Current() => Volatile.Read(ref server) ?? throw new HttpRequestException("The controller is restarting (test).");

        using var logs = new RecordingLoggerProvider(1_000);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            Credential = new RoofKioskCredential(RoofClientApiTests.KioskKey),
            CreateHandler = () => ClientTestSupport.CreateHandler(Current),
            WebSocketFactory = ClientTestSupport.WebSocketFactory(Current),
            LoggerFactory = loggerFactory
        });
        var run = new KioskSoakRun(duration, warmUp);
        await using var console = new KioskConsole(client, logger: loggerFactory.CreateLogger<KioskConsole>());
        console.Changed += () => run.Observe(console.View);
        console.Start();
        await UntilAsync(() => console.View.FeedLabel == "live", "the live status", ReconnectBound);
        await using var screen = await KioskScreen.ShowAsync(console, 1280, 720, 8.2);

        // The kiosk's UI thread: the shell's updates run as they are posted, and the screen is drawn.
        using var stopping = new CancellationTokenSource();
        var pumping = PumpAsync(screen, stopping.Token);

        var clock = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        Exception? failure = null;
        try
        {
            while (clock.Elapsed < duration)
            {
                await CycleAsync(rig, console, screen, run);
                if (run.Cycles % RestartEvery == 0)
                {
                    Volatile.Write(ref server, null);
                    await rig.RestartControllerAsync(crash: false);
                    Volatile.Write(ref server, rig.App.Server);
                    await AddOperatorAsync(rig);
                    var restarted = Stopwatch.StartNew();
                    run.Restarts++;
                    await UntilAsync(
                        () => run.Connections > run.Restarts && console.View.FeedLabel == "live",
                        $"the kiosk to reconnect after restart {run.Restarts}",
                        ReconnectBound);
                    run.Reconnects.Add(restarted.Elapsed);
                }

                if (clock.Elapsed >= nextSample)
                {
                    run.Samples.Add(await SampleAsync(screen, run, clock.Elapsed));
                    nextSample = clock.Elapsed + sampleEvery;
                }
            }

            run.Samples.Add(await SampleAsync(screen, run, clock.Elapsed));
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await stopping.CancelAsync();
        run.Elapsed = clock.Elapsed;
        var results = ResultsDirectory();
        var checks = await FinishAsync(run, rig.Session.Plant.Violations.Count, failure, FinalScreenAsync, logs, results);
        TestContext.WriteLine(File.ReadAllText(Path.Combine(results, SummaryFile)));

        checks.Where(check => !check.Passed).Should().BeEmpty("every kiosk soak criterion must hold (results in {0})", results);

        async Task FinalScreenAsync()
        {
            await pumping;
            await screen.RenderAsync("soak-end", TestContext);
        }
    }

    /// <summary>
    /// The end of a run: waits for the last screen (the pump's draws, then a render that checks its layout and Stop), then
    /// evaluates the run and writes its results. A fault in the last screen is a failed check written with the others, so
    /// a run that fails keeps its samples and says why.
    /// </summary>
    internal static async Task<IReadOnlyList<KioskSoakCheck>> FinishAsync(
        KioskSoakRun run,
        int violations,
        Exception? failure,
        Func<Task> finalScreen,
        RecordingLoggerProvider logs,
        string directory)
    {
        Exception? screenFailure = null;
        try
        {
            await finalScreen();
        }
        catch (Exception exception)
        {
            screenFailure = exception;
        }

        var checks = Evaluate(run, violations, failure, screenFailure);
        WriteResults(directory, run, checks, logs);
        return checks;
    }

    /// <summary>Unlock, Open, Stop in mid-travel, Close to the closed limit, lock: the Stop and the moves are touches on the screen.</summary>
    private static async Task CycleAsync(EmulatedRoofRig rig, KioskConsole console, KioskScreen screen, KioskSoakRun run)
    {
        (await console.UnlockAsync(Operator, TestSecrets.Pin)).Should().BeNull("the operator's PIN unlocks the kiosk");
        await UntilAsync(() => console.View is { UnlockedBy: Operator, OpenBlock: null }, "Open to be offered", MotionBound);

        await screen.TouchAsync(await OnUiAsync(() => screen.Find("open")!));
        await rig.WaitForPlantAsync(plant => plant.PositionMeters > TravelMeters / 2, "the roof to reach mid-travel", MotionBound);

        var stop = Stopwatch.StartNew();
        await screen.TouchAsync(screen.Shell.Stop);
        run.Stops++;
        await UntilAsync(() => console.View is { StopInFlight: false, StopOutcome: not RoofStopOutcome.Sent }, "Stop's answer", MotionBound);
        run.StopLatencies.Add(stop.Elapsed);
        if (console.View.StopOutcome == RoofStopOutcome.Acknowledged)
        {
            run.Acknowledged++;
        }
        else
        {
            run.Note($"Stop {run.Stops}: {console.View.StopOutcome} ({console.View.StopMessage})");
        }

        var stopped = await rig.WaitForRestAsync("the kiosk's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);

        await UntilAsync(() => console.View.CloseBlock is null, "Close to be offered", MotionBound);
        await screen.TouchAsync(await OnUiAsync(() => screen.Find("close")!));
        var closed = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, MotionBound, "the roof to close");
        closed.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached, "the kiosk renewed the lease until the closed limit");
        await UntilAsync(() => !console.View.HoldsLease, "the kiosk to let the lease go", MotionBound);

        await console.LockAsync();
        console.View.IsUnlocked.Should().BeFalse();
        run.Cycles++;
    }

    private static async Task PumpAsync(KioskScreen screen, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await screen.DrawAsync();
            try
            {
                await Task.Delay(PumpEvery, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<KioskSoakSample> SampleAsync(KioskScreen screen, KioskSoakRun run, TimeSpan elapsed)
    {
        await screen.DrawAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return new KioskSoakSample(elapsed, run.Cycles, GC.GetTotalMemory(forceFullCollection: true), process.Threads.Count, run.Connections, run.Restarts);
    }

    private static List<KioskSoakCheck> Evaluate(KioskSoakRun run, int violations, Exception? failure, Exception? screenFailure)
    {
        var checks = new List<KioskSoakCheck>
        {
            new("Every cycle completed", failure is null, failure is null ? $"{run.Cycles} cycles" : $"after {run.Cycles} cycles: {Describe(failure)}"),
            new("The last screen", screenFailure is null, screenFailure is null ? "drawn, laid out, with Stop in full" : Describe(screenFailure)),
            new("Every Stop acknowledged", run.Stops > 0 && run.Acknowledged == run.Stops, $"{run.Acknowledged} of {run.Stops}"),
            new("A restart was ridden through", run.Restarts > 0, $"{run.Restarts} restarts"),
            new(
                "Each restart is one reconnect",
                run.Connections == run.Restarts + 1,
                $"{run.Connections} connections for {run.Restarts} restarts"),
            new(
                "Each reconnect within the bound",
                run.Reconnects.Count == run.Restarts && run.Reconnects.All(took => took <= ReconnectBound),
                run.Reconnects.Count == 0 ? "none" : $"slowest {run.Reconnects.Max().TotalSeconds:0.0} s (bound {ReconnectBound.TotalSeconds:0} s)"),
            new("The plant's invariants held", violations == 0, $"{violations} violations")
        };

        var trend = run.Samples.Where(sample => sample.Elapsed >= run.WarmUp).ToList();
        var window = trend.Count > 0 ? trend[^1].Elapsed - trend[0].Elapsed : TimeSpan.Zero;
        if (window >= MinimumTrendWindow && trend.Count >= EndSamples * 2)
        {
            var heapStart = Median(trend.Take(EndSamples).Select(sample => sample.HeapBytes));
            var heapEnd = Median(trend.TakeLast(EndSamples).Select(sample => sample.HeapBytes));
            var heapLimit = (long)(heapStart * (1 + MemoryGrowthLimit)) + MemorySlackBytes;
            checks.Add(new("The managed heap is flat", heapEnd <= heapLimit, $"{Megabytes(heapStart)} to {Megabytes(heapEnd)} (limit {Megabytes(heapLimit)})"));
            var threadsStart = Median(trend.Take(EndSamples).Select(sample => (long)sample.Threads));
            var threadsEnd = Median(trend.TakeLast(EndSamples).Select(sample => (long)sample.Threads));
            checks.Add(new("The threads are flat", threadsEnd <= threadsStart + ThreadMargin, $"{threadsStart} to {threadsEnd} (margin {ThreadMargin})"));
        }
        else
        {
            var first = run.Samples.FirstOrDefault();
            var last = run.Samples.LastOrDefault();
            checks.Add(new(
                "Resources recorded (the run is too short to assess them)",
                true,
                first is null || last is null ? "no samples" : $"heap {Megabytes(first.HeapBytes)} to {Megabytes(last.HeapBytes)}, threads {first.Threads} to {last.Threads}"));
        }

        return checks;
    }

    private string ResultsDirectory()
        => Environment.GetEnvironmentVariable(ResultsVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "kiosk-soak");

    /// <summary>An exception on one line of the summary's table.</summary>
    private static string Describe(Exception exception)
        => $"{exception.GetType().Name}: {string.Join(' ', exception.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))}"
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static void WriteResults(string directory, KioskSoakRun run, List<KioskSoakCheck> checks, RecordingLoggerProvider logs)
    {
        Directory.CreateDirectory(directory);

        var summary = new StringBuilder();
        summary.AppendLine("## Kiosk soak");
        summary.AppendLine();
        summary.AppendLine(CultureInfo.InvariantCulture, $"{run.Elapsed:hh\\:mm\\:ss} (warm-up {run.WarmUp:hh\\:mm\\:ss}): {run.Cycles} cycles, {run.Stops} Stops, {run.Restarts} controller restarts.");
        if (run.StopLatencies.Count > 0)
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"Stop, from the touch to its answer on the screen: median {Median(run.StopLatencies.Select(took => took.Ticks)) / TimeSpan.TicksPerMillisecond} ms, slowest {run.StopLatencies.Max().TotalMilliseconds:0} ms.");
        }

        summary.AppendLine();
        summary.AppendLine("| Check | Result | Detail |");
        summary.AppendLine("| --- | --- | --- |");
        foreach (var check in checks)
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"| {check.Name} | {(check.Passed ? "pass" : "FAIL")} | {check.Detail} |");
        }

        if (run.Notes.Count > 0)
        {
            summary.AppendLine();
            foreach (var note in run.Notes)
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
            }
        }

        var serious = logs.Serious;
        if (serious.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(CultureInfo.InvariantCulture, $"The kiosk logged {serious.Count} warnings or errors; the first:");
            foreach (var entry in serious.Take(10))
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {entry.Level} {entry.Category}: {entry.Message}");
            }
        }

        File.WriteAllText(Path.Combine(directory, SummaryFile), summary.ToString());

        var samples = new StringBuilder("elapsed_s,cycles,heap_bytes,threads,connections,restarts\n");
        foreach (var sample in run.Samples)
        {
            samples.AppendLine(CultureInfo.InvariantCulture, $"{sample.Elapsed.TotalSeconds:0.0},{sample.Cycles},{sample.HeapBytes},{sample.Threads},{sample.Connections},{sample.Restarts}");
        }

        File.WriteAllText(Path.Combine(directory, SamplesFile), samples.ToString());
    }

    /// <summary>The kiosk's device key, as the installation configures it: a viewer key that may unlock with a PIN.</summary>
    private static Dictionary<string, string?> KioskKeySettings() => new()
    {
        ["RoofControllerSecurity:ApiKeys:4:Name"] = "soak-kiosk",
        ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:4:Key"] = RoofClientApiTests.KioskKey,
        ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
    };

    /// <summary>The operator with a PIN. The rig keeps people in memory (it has no secrets file), so a restart forgets them.</summary>
    private static async Task AddOperatorAsync(EmulatedRoofRig rig)
    {
        using var admin = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.AddUserAsync(new RoofUserCreateRequest
        {
            Name = Operator,
            Role = RoofControllerApiContract.OperatorRole,
            Password = TestSecrets.Password,
            Pin = TestSecrets.Pin
        });
    }

    private static TimeSpan SoakDuration()
        => Environment.GetEnvironmentVariable(DurationVariable) is { Length: > 0 } configured
            ? TimeSpan.Parse(configured, CultureInfo.InvariantCulture)
            : DefaultDuration;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");

    internal sealed record KioskSoakSample(TimeSpan Elapsed, int Cycles, long HeapBytes, int Threads, int Connections, int Restarts);

    internal sealed record KioskSoakCheck(string Name, bool Passed, string Detail);

    internal sealed class KioskSoakRun(TimeSpan duration, TimeSpan warmUp)
    {
        private const int MaxNotes = 100;
        private RoofStatusFeedState _feedState;
        private int _connections;

        public TimeSpan Duration { get; } = duration;

        public TimeSpan WarmUp { get; } = warmUp;

        public TimeSpan Elapsed { get; set; }

        public int Cycles { get; set; }

        public int Stops { get; set; }

        public int Acknowledged { get; set; }

        public int Restarts { get; set; }

        /// <summary>How many times the kiosk's feed connected, from its view.</summary>
        public int Connections => Volatile.Read(ref _connections);

        public List<TimeSpan> Reconnects { get; } = [];

        public List<TimeSpan> StopLatencies { get; } = [];

        public List<KioskSoakSample> Samples { get; } = [];

        public List<string> Notes { get; } = [];

        /// <summary>Counts the feed's connections as the kiosk's view shows them.</summary>
        public void Observe(KioskView view)
        {
            lock (Samples)
            {
                if (view.FeedState == RoofStatusFeedState.Connected && _feedState != RoofStatusFeedState.Connected)
                {
                    Interlocked.Increment(ref _connections);
                }

                _feedState = view.FeedState;
            }
        }

        public void Note(string note)
        {
            if (Notes.Count < MaxNotes)
            {
                Notes.Add(note);
            }
        }
    }
}
