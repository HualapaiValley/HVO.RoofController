using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.RPi.Tests.Web;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The end of the kiosk soak (<see cref="KioskSoakScenarios.FinishAsync"/>), without the soak: a run that fails keeps its
/// results, and says why.
/// </summary>
[TestClass]
public sealed class KioskSoakResultsTests
{
    [TestMethod]
    public async Task ALastScreenThatFails_IsACheck_WrittenWithTheRunsResults()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var logs = new RecordingLoggerProvider(10);
        var run = Run();
        var cycle = new TimeoutException("Timed out waiting for Stop's answer.");

        var checks = await KioskSoakScenarios.FinishAsync(
            run,
            violations: 0,
            cycle,
            () => throw new AssertFailedException("soak-end: text cut off:\n  notice | \"Stop failed\""),
            logs,
            directory.Path);

        checks.Where(check => !check.Passed).Select(check => check.Name).Should().Equal("Every cycle completed", "The last screen");
        var summary = File.ReadAllLines(directory.File(KioskSoakScenarios.SummaryFile));
        summary.Should().Contain("| Every cycle completed | FAIL | after 3 cycles: TimeoutException: Timed out waiting for Stop's answer. |");
        summary.Should().Contain("| The last screen | FAIL | AssertFailedException: soak-end: text cut off: notice \\| \"Stop failed\" |");
        File.ReadAllLines(directory.File(KioskSoakScenarios.SamplesFile)).Should().HaveCount(1 + run.Samples.Count, "the samples are kept");
    }

    [TestMethod]
    public async Task APumpThatFailed_IsTheLastScreensFailure()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var logs = new RecordingLoggerProvider(10);
        var pumping = Task.FromException(new InvalidOperationException("The draw failed."));

        var checks = await KioskSoakScenarios.FinishAsync(
            Run(),
            violations: 0,
            failure: null,
            async () =>
            {
                await pumping;
                Assert.Fail("The last screen is not drawn after the pump failed.");
            },
            logs,
            directory.Path);

        checks.Should().ContainSingle(check => !check.Passed).Which.Should().Be(
            new KioskSoakScenarios.KioskSoakCheck("The last screen", false, "InvalidOperationException: The draw failed."));
        File.Exists(directory.File(KioskSoakScenarios.SummaryFile)).Should().BeTrue();
    }

    [TestMethod]
    public async Task ALastScreenThatIsWhole_Passes()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var logs = new RecordingLoggerProvider(10);

        var checks = await KioskSoakScenarios.FinishAsync(Run(), violations: 0, failure: null, () => Task.CompletedTask, logs, directory.Path);

        checks.Should().OnlyContain(check => check.Passed);
        File.ReadAllText(directory.File(KioskSoakScenarios.SummaryFile)).Should().Contain("| The last screen | pass |");
    }

    /// <summary>Three cycles, with a restart ridden through and every Stop acknowledged.</summary>
    private static KioskSoakScenarios.KioskSoakRun Run()
    {
        var run = new KioskSoakScenarios.KioskSoakRun(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30))
        {
            Elapsed = TimeSpan.FromMinutes(2),
            Cycles = 3,
            Stops = 3,
            Acknowledged = 3,
            Restarts = 1
        };
        run.Observe(new KioskView { FeedState = RoofStatusFeedState.Connected });
        run.Observe(new KioskView { FeedState = RoofStatusFeedState.Reconnecting });
        run.Observe(new KioskView { FeedState = RoofStatusFeedState.Connected });
        run.Reconnects.Add(TimeSpan.FromSeconds(4));
        run.StopLatencies.Add(TimeSpan.FromMilliseconds(120));
        run.Samples.Add(new(TimeSpan.Zero, 0, 50_000_000, 40, 1, 0));
        run.Samples.Add(new(TimeSpan.FromMinutes(2), 3, 51_000_000, 41, 2, 1));
        return run;
    }
}
