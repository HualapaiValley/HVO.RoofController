using FluentAssertions;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Supervision;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

[TestClass]
public sealed class SupervisorStateReaderTests
{
    [TestMethod]
    public async Task Read_WithoutAStatePath_IsNotSupervised()
    {
        var reader = new SupervisorStateReader(WebTestSupport.Monitor(new RoofWebOptions()));

        var reading = await reader.ReadAsync();

        reading.Should().Be(SupervisorReading.NotSupervised);
    }

    [TestMethod]
    public async Task Read_TheSupervisorsOwnFormat_ReadsEveryField()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("supervisor.json");
        await File.WriteAllTextAsync(path, WebTestSupport.SupervisorStateSample);
        var reader = new SupervisorStateReader(WebTestSupport.Monitor(new RoofWebOptions { SupervisorStatePath = path }));

        var reading = await reader.ReadAsync();

        reading.Availability.Should().Be(SupervisorAvailability.Available);
        reading.Problem.Should().BeNull();
        var snapshot = reading.Snapshot!;
        snapshot.Supervisor.Should().Be("running");
        snapshot.UpdatedAt.Should().Be(new DateTimeOffset(2026, 9, 29, 5, 45, 58, TimeSpan.Zero));
        snapshot.CrashLimit.Should().Be(5);
        snapshot.CrashWindowSeconds.Should().Be(120);
        snapshot.ForceRestartMinSeconds.Should().Be(10);
        snapshot.LastForcedRestart.Should().Be(new ForcedRestartRecord
        {
            At = new DateTimeOffset(2026, 9, 29, 5, 45, 58, TimeSpan.Zero),
            Outcome = ForcedRestartRecord.Outcomes.Ignored,
        });
        snapshot.Controller.Should().Be(new SupervisedProcess
        {
            State = SupervisedProcess.States.Running,
            Pid = 974360,
            Starts = 2,
            RecentCrashes = 1,
            LastExitCode = 1,
            LastExitReason = "crashed (exit code 1)",
            LastExitAt = new DateTimeOffset(2026, 9, 29, 5, 45, 54, TimeSpan.Zero),
        });
        snapshot.Ui.Should().Be(new SupervisedProcess { State = SupervisedProcess.States.Running, Pid = 974380, Starts = 1 });
    }

    [TestMethod]
    public async Task Read_NoForcedRestartYet_IsNull()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("supervisor.json");
        await File.WriteAllTextAsync(path, WebTestSupport.SupervisorStateSample.Replace(
            "\"lastForcedRestart\":{\"at\":\"2026-09-29T05:45:58Z\",\"outcome\":\"ignored\"}", "\"lastForcedRestart\":null", StringComparison.Ordinal));
        var reader = new SupervisorStateReader(WebTestSupport.Monitor(new RoofWebOptions { SupervisorStatePath = path }));

        var reading = await reader.ReadAsync();

        reading.Snapshot!.LastForcedRestart.Should().BeNull();
        reading.Snapshot.Controller.Starts.Should().Be(2);
    }

    [TestMethod]
    public void ForcedRestartOutcomes_AreTheSupervisorsWords()
    {
        var script = File.ReadAllText(Path.Combine(ControllerForcedRestartTests.RepositoryRoot(), "src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh"));

        script.Should().Contain($"FORCED_RESTART_OUTCOME={ForcedRestartRecord.Outcomes.Restarted}")
            .And.Contain($"FORCED_RESTART_OUTCOME={ForcedRestartRecord.Outcomes.Ignored}");
    }

    [TestMethod]
    public async Task Read_AMissingFile_SaysTheSupervisorHasNotWrittenIt()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var reader = new SupervisorStateReader(WebTestSupport.Monitor(new RoofWebOptions { SupervisorStatePath = directory.File("nope/supervisor.json") }));

        var reading = await reader.ReadAsync();

        reading.Availability.Should().Be(SupervisorAvailability.Unreadable);
        reading.Snapshot.Should().BeNull();
        reading.Problem.Should().Contain("the supervisor has not written its state");
    }

    [TestMethod]
    [DataRow("{\"supervisor\":")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"controller\":{\"state\":\"running\"}}")]
    [DataRow("{\"controller\":null,\"ui\":{\"state\":\"running\"}}")]
    [DataRow("{\"controller\":{\"state\":\"running\"},\"ui\":null}")]
    public async Task Read_AFileThatIsNotTheSupervisorsState_IsUnreadable(string content)
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("supervisor.json");
        await File.WriteAllTextAsync(path, content);
        var reader = new SupervisorStateReader(WebTestSupport.Monitor(new RoofWebOptions { SupervisorStatePath = path }));

        var reading = await reader.ReadAsync();

        reading.Availability.Should().Be(SupervisorAvailability.Unreadable);
        reading.Snapshot.Should().BeNull();
        reading.Problem.Should().StartWith(path);
    }
}

[TestClass]
public sealed class ControllerForcedRestartTests
{
    [TestMethod]
    public void Request_LeavesTheFileTheSupervisorWatches()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var restart = new ControllerForcedRestart(WebTestSupport.Monitor(new RoofWebOptions { SupervisorControlPath = directory.Path }));

        var result = restart.Request();

        result.Outcome.Should().Be(ForcedRestartRequestOutcome.Requested);
        result.Message.Should().Be("The supervisor was asked to kill and restart the controller. It does so within a second, unless the controller started too recently (by default, less than 10 s ago).");
        // The name container/roof-supervisor.sh watches for (FORCE_RESTART_REQUEST).
        File.Exists(Path.Combine(directory.Path, "force-restart-controller")).Should().BeTrue();
        ControllerForcedRestart.RequestFileName.Should().Be("force-restart-controller");
    }

    [TestMethod]
    public void Request_WithoutAControlDirectory_IsNotSupervised()
    {
        var restart = new ControllerForcedRestart(WebTestSupport.Monitor(new RoofWebOptions()));

        var result = restart.Request();

        result.Outcome.Should().Be(ForcedRestartRequestOutcome.NotSupervised);
        result.Message.Should().Contain("not running under the container's supervisor");
    }

    [TestMethod]
    public void Request_WhenTheDirectoryIsMissing_Fails_AndSaysWhere()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var control = Path.Combine(directory.Path, "missing");
        var restart = new ControllerForcedRestart(WebTestSupport.Monitor(new RoofWebOptions { SupervisorControlPath = control }));

        var result = restart.Request();

        result.Outcome.Should().Be(ForcedRestartRequestOutcome.Failed);
        result.Message.Should().Contain(Path.Combine(control, "force-restart-controller"));
    }

    [TestMethod]
    public void SupervisorScript_WatchesTheSameFileName()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh"));

        script.Should().Contain($"FORCE_RESTART_REQUEST=\"${{CONTROL_DIR}}/{ControllerForcedRestart.RequestFileName}\"");
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "HVO.RoofController.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
