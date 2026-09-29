using System.Globalization;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>How serious a status is, for its colour on the page.</summary>
public enum RoofWebStatusLevel
{
    Good,
    Busy,
    Warning,
    Bad,
}

/// <summary>A status to show: a headline, the details and how serious it is.</summary>
public sealed record RoofWebStatusView(RoofWebStatusLevel Level, string Headline, IReadOnlyList<string> Details);

/// <summary>
/// The wording for the controller process's state. It says only what the controller or the supervisor reported: the
/// process and its readiness, never the roof's physical state.
/// </summary>
public static class RoofWebStatusText
{
    public static RoofWebStatusView DescribeController(RoofWebStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var details = new List<string>();
        var snapshot = status.Supervisor.Snapshot;
        var controller = snapshot?.Controller;
        if (controller is not null && LastExit(controller) is { } lastExit)
        {
            details.Add(lastExit);
        }

        switch (controller?.State)
        {
            case SupervisedProcess.States.CrashLoop:
                details.Insert(0, string.Create(
                    CultureInfo.InvariantCulture,
                    $"It crashed {controller.RecentCrashes} times within {snapshot!.CrashWindowSeconds} s, so the supervisor left it stopped instead of starting it again while the roof may need attention. The container's health check fails; Docker marks it unhealthy after three failed checks."));
                details.Add("Find the cause in the container's log. A forced restart (docker exec roof-controller touch /run/hvo-roof/control/force-restart-controller), or a restart of the container, starts the controller again.");
                return new RoofWebStatusView(RoofWebStatusLevel.Bad, "The controller is stopped after repeated crashes", details);

            case SupervisedProcess.States.Restarting:
                return new RoofWebStatusView(RoofWebStatusLevel.Busy, "The controller is restarting", details);

            case SupervisedProcess.States.Starting:
                return new RoofWebStatusView(RoofWebStatusLevel.Busy, "The controller is starting", details);

            case SupervisedProcess.States.Stopping:
                return new RoofWebStatusView(RoofWebStatusLevel.Busy, "The controller is stopping with the container", details);

            case SupervisedProcess.States.Stopped:
                return new RoofWebStatusView(RoofWebStatusLevel.Bad, "The controller is stopped", details);
        }

        // Running under the supervisor, or no supervisor to ask: the readiness probe decides.
        switch (status.Controller.Readiness)
        {
            case ControllerReadiness.Ready:
                return new RoofWebStatusView(RoofWebStatusLevel.Good, "The controller is ready", details);

            case ControllerReadiness.NotReady:
                details.Insert(0, $"Its readiness check answered {status.Controller.Detail}.");
                return new RoofWebStatusView(RoofWebStatusLevel.Warning, "The controller is running but not ready", details);

            default:
                details.Insert(0, status.Controller.Detail);
                if (controller?.State == SupervisedProcess.States.Running)
                {
                    details.Add("The supervisor reports it running. If it stays unanswered, force a restart (docker exec roof-controller touch /run/hvo-roof/control/force-restart-controller) or restart the container.");
                }

                return new RoofWebStatusView(RoofWebStatusLevel.Bad, "The controller is not answering", details);
        }
    }

    /// <summary>The supervisor's own line: its state, or why the web UI cannot see it.</summary>
    public static string DescribeSupervisor(SupervisorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Availability switch
        {
            SupervisorAvailability.NotSupervised => "The web UI is not running under the container's supervisor.",
            SupervisorAvailability.Unreadable => $"The supervisor's state could not be read: {reading.Problem}",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"Supervisor {reading.Snapshot!.Supervisor}: controller {reading.Snapshot.Controller.State} (started {reading.Snapshot.Controller.Starts} times), web UI {reading.Snapshot.Ui.State} (started {reading.Snapshot.Ui.Starts} times). Updated {reading.Snapshot.UpdatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC."),
        };
    }

    private static string? LastExit(SupervisedProcess process)
    {
        if (string.IsNullOrEmpty(process.LastExitReason))
        {
            return null;
        }

        return process.LastExitAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"Last exit: {process.LastExitReason}, at {at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.")
            : $"Last exit: {process.LastExitReason}.";
    }
}
