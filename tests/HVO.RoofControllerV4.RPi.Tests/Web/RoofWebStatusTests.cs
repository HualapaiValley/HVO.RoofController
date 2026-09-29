using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Supervision;
using static HVO.RoofControllerV4.Web.Supervision.SupervisedProcess;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The status page's wording: what the controller and the supervisor reported, never the roof's state.</summary>
[TestClass]
public sealed class RoofWebStatusTextTests
{
    private static readonly DateTimeOffset ExitAt = new(2026, 9, 29, 5, 59, 58, TimeSpan.Zero);

    [TestMethod]
    public void Ready_WithoutASupervisor_IsGood()
    {
        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Ready));

        view.Should().BeEquivalentTo(new RoofWebStatusView(RoofWebStatusLevel.Good, "The controller is ready", []));
    }

    [TestMethod]
    public void Ready_AfterARequestedRestart_SaysWhyItLastExited()
    {
        var snapshot = WebStatuses.Controller(States.Running, lastExitReason: "restart requested", lastExitAt: ExitAt);

        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Ready, snapshot));

        view.Level.Should().Be(RoofWebStatusLevel.Good);
        view.Headline.Should().Be("The controller is ready");
        view.Details.Should().Equal("Last exit: restart requested, at 2026-09-29 05:59:58 UTC.");
    }

    [TestMethod]
    public void NotReady_SaysWhatTheReadinessCheckAnswered()
    {
        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.NotReady, WebStatuses.Controller(States.Running), "HTTP 503, Unhealthy"));

        view.Level.Should().Be(RoofWebStatusLevel.Warning);
        view.Headline.Should().Be("The controller is running but not ready");
        view.Details.Should().Equal("Its readiness check answered HTTP 503, Unhealthy.");
    }

    [TestMethod]
    public void Unreachable_WhileTheSupervisorSaysRunning_PointsAtTheForcedRestart()
    {
        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Unreachable, WebStatuses.Controller(States.Running), "Connection refused (localhost:8080)"));

        view.Level.Should().Be(RoofWebStatusLevel.Bad);
        view.Headline.Should().Be("The controller is not answering");
        view.Details.Should().Equal(
            "Connection refused (localhost:8080)",
            "The supervisor reports it running. If it stays unanswered, an admin can force a restart.");
    }

    [TestMethod]
    public void Unreachable_WithoutASupervisor_SaysOnlyWhatFailed()
    {
        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Unreachable, detail: "Connection refused"));

        view.Headline.Should().Be("The controller is not answering");
        view.Details.Should().Equal("Connection refused");
    }

    [TestMethod]
    public void Restarting_TakesPrecedenceOverTheProbe_AndSaysWhy()
    {
        var snapshot = WebStatuses.Controller(States.Restarting, 1, "crashed (exit code 134)", ExitAt);

        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Unreachable, snapshot, "Connection refused"));

        view.Level.Should().Be(RoofWebStatusLevel.Busy);
        view.Headline.Should().Be("The controller is restarting");
        view.Details.Should().Equal("Last exit: crashed (exit code 134), at 2026-09-29 05:59:58 UTC.");
    }

    [TestMethod]
    public void CrashLoop_SaysItIsLeftStopped_AndHowToStartItAgain()
    {
        var snapshot = WebStatuses.Controller(States.CrashLoop, 5, "crashed (exit code 1)", ExitAt);

        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Unreachable, snapshot, "Connection refused"));

        view.Level.Should().Be(RoofWebStatusLevel.Bad);
        view.Headline.Should().Be("The controller is stopped after repeated crashes");
        view.Details.Should().Equal(
            "It crashed 5 times within 120 s, so the supervisor left it stopped instead of starting it again while the roof may need attention. The container's health check fails; Docker marks it unhealthy after three failed checks.",
            "Last exit: crashed (exit code 1), at 2026-09-29 05:59:58 UTC.",
            "Find the cause in the container's log. A forced restart (docker exec roof-controller touch /run/hvo-roof/control/force-restart-controller), or a restart of the container, starts the controller again.");
    }

    [TestMethod]
    [DataRow(States.Starting, RoofWebStatusLevel.Busy, "The controller is starting")]
    [DataRow(States.Stopping, RoofWebStatusLevel.Busy, "The controller is stopping with the container")]
    [DataRow(States.Stopped, RoofWebStatusLevel.Bad, "The controller is stopped")]
    public void SupervisorStates_AreNamed(string state, RoofWebStatusLevel level, string headline)
    {
        var view = RoofWebStatusText.DescribeController(WebStatuses.With(ControllerReadiness.Unreachable, WebStatuses.Controller(state)));

        view.Level.Should().Be(level);
        view.Headline.Should().Be(headline);
    }

    [TestMethod]
    public void NoWording_ClaimsTheRoofsPosition()
    {
        var views = new[] { States.Running, States.Restarting, States.CrashLoop, States.Starting, States.Stopping, States.Stopped }
            .SelectMany(state => Enum.GetValues<ControllerReadiness>().Select(readiness =>
                RoofWebStatusText.DescribeController(WebStatuses.With(readiness, WebStatuses.Controller(state, 5, "crashed (exit code 1)", ExitAt)))));

        foreach (var view in views)
        {
            var text = string.Join(" ", view.Details.Prepend(view.Headline));
            text.Should().NotMatchRegex("(?i)\\b(open|closed|closing|opening|moving|safe)\\b");
        }
    }

    [TestMethod]
    public void DescribeSupervisor_CoversEachAvailability()
    {
        RoofWebStatusText.DescribeSupervisor(SupervisorReading.NotSupervised)
            .Should().Be("The web UI is not running under the container's supervisor.");
        RoofWebStatusText.DescribeSupervisor(new SupervisorReading(SupervisorAvailability.Unreadable, null, "/run/hvo-roof/supervisor.json is not valid"))
            .Should().Be("The supervisor's state could not be read: /run/hvo-roof/supervisor.json is not valid");
        RoofWebStatusText.DescribeSupervisor(new SupervisorReading(SupervisorAvailability.Available, WebStatuses.Controller(States.Restarting), null))
            .Should().Be("Supervisor running: controller restarting (started 3 times), web UI running (started 1 times). Updated 2026-09-29 06:00:00 UTC.");
    }
}

[TestClass]
public sealed class RoofWebStatusProbeTests
{
    [TestMethod]
    public async Task Check_AReadyController_AsksTheReadinessEndpoint()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        var probe = Probe(handler, out var time);

        var status = await probe.CheckAsync();

        status.Controller.Should().Be(new ControllerProbe(ControllerReadiness.Ready, "Healthy"));
        status.Supervisor.Should().Be(SupervisorReading.NotSupervised);
        status.CheckedAt.Should().Be(time.GetUtcNow());
        handler.Requests.Should().ContainSingle().Which.AbsolutePath.Should().Be("/health/ready");
    }

    [TestMethod]
    public async Task Check_AnUnhealthyController_IsNotReady_WithTheStatusAndText()
    {
        var probe = Probe(new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.ServiceUnavailable, "Unhealthy\n")), out _);

        var status = await probe.CheckAsync();

        status.Controller.Should().Be(new ControllerProbe(ControllerReadiness.NotReady, "HTTP 503, Unhealthy"));
    }

    [TestMethod]
    public async Task Check_AnotherError_IsNotReady_WithTheSharedWording()
    {
        var probe = Probe(new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.InternalServerError, "boom")), out _);

        var status = await probe.CheckAsync();

        status.Controller.Readiness.Should().Be(ControllerReadiness.NotReady);
        status.Controller.Detail.Should().StartWith("HTTP 500, ");
    }

    [TestMethod]
    public async Task Check_AnEmptyAnswer_SaysNoStatus()
    {
        var probe = Probe(new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.ServiceUnavailable, " ")), out _);

        (await probe.CheckAsync()).Controller.Should().Be(new ControllerProbe(ControllerReadiness.NotReady, "HTTP 503, no status"));
    }

    [TestMethod]
    public async Task Check_NoListener_IsUnreachable()
    {
        var probe = Probe(new WebTestSupport.StubHandler(_ => throw new HttpRequestException("Connection refused (localhost:8080)")), out _);

        (await probe.CheckAsync()).Controller.Should().Be(new ControllerProbe(ControllerReadiness.Unreachable, "Connection refused (localhost:8080)"));
    }

    [TestMethod]
    public async Task Check_ReadsTheSupervisorState_AlongsideTheProbe()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("supervisor.json");
        await File.WriteAllTextAsync(path, WebTestSupport.SupervisorState(States.Restarting));
        var probe = Probe(new WebTestSupport.StubHandler(_ => throw new HttpRequestException("Connection refused")), out _, new RoofWebOptions { SupervisorStatePath = path });

        var status = await probe.CheckAsync();

        status.Supervisor.Availability.Should().Be(SupervisorAvailability.Available);
        status.Supervisor.Snapshot!.Controller.State.Should().Be(States.Restarting);
        RoofWebStatusText.DescribeController(status).Headline.Should().Be("The controller is restarting");
    }

    private static RoofWebStatusProbe Probe(HttpMessageHandler handler, out ManualTimeProvider time, RoofWebOptions? options = null)
    {
        time = new ManualTimeProvider();
        var client = new HVO.RoofControllerV4.Client.RoofControllerClient(new HVO.RoofControllerV4.Client.RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = () => handler,
        });
        return new RoofWebStatusProbe(client, new SupervisorStateReader(WebTestSupport.Monitor(options ?? new RoofWebOptions())), time);
    }
}
