using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>The outcome of asking the supervisor for a forced restart.</summary>
public enum ForcedRestartRequestOutcome
{
    /// <summary>
    /// The request was left for the supervisor, which reads it within a second. It kills and restarts the controller
    /// unless the controller started too recently; supervisor.json's <c>lastForcedRestart</c> records which.
    /// </summary>
    Requested,

    /// <summary>The web UI runs without the supervisor (no control directory is configured).</summary>
    NotSupervised,

    /// <summary>The request could not be written.</summary>
    Failed,
}

public sealed record ForcedRestartRequestResult(ForcedRestartRequestOutcome Outcome, string Message);

/// <summary>
/// Asks the container's supervisor to kill the controller (SIGKILL, as <c>docker kill</c> does) and start it again,
/// for a controller that does not answer. The request is the file force-restart-controller in the supervisor's control
/// directory (<see cref="RoofWebOptions.SupervisorControlPath"/>), which only the web UI's user can write. The
/// supervisor ignores a request within 10 s of the controller's start (HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS;
/// <see cref="SupervisorSnapshot.ForceRestartMinSeconds"/>) and records what it did in
/// <see cref="SupervisorSnapshot.LastForcedRestart"/>. Only an admin may ask, after
/// confirming what a kill means for the roof (commissioning.md C11); the page that asks checks both.
/// </summary>
public sealed class ControllerForcedRestart
{
    /// <summary>The request's file name in the control directory; roof-supervisor.sh watches for it.</summary>
    public const string RequestFileName = "force-restart-controller";

    private readonly IOptionsMonitor<RoofWebOptions> _options;

    public ControllerForcedRestart(IOptionsMonitor<RoofWebOptions> options) => _options = options;

    public ForcedRestartRequestResult Request()
    {
        var directory = _options.CurrentValue.SupervisorControlPath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ForcedRestartRequestResult(
                ForcedRestartRequestOutcome.NotSupervised,
                "The web UI is not running under the container's supervisor, so it cannot restart the controller.");
        }

        var path = Path.Combine(directory, RequestFileName);
        try
        {
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
            return new ForcedRestartRequestResult(
                ForcedRestartRequestOutcome.Requested,
                "The supervisor was asked to kill and restart the controller. It does so within a second, unless the controller started too recently (by default, less than 10 s ago).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ForcedRestartRequestResult(
                ForcedRestartRequestOutcome.Failed,
                $"The request could not be written to {path}: {ex.Message}");
        }
    }
}
