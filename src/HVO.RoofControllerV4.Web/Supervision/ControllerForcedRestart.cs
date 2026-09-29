using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>The outcome of asking the supervisor for a forced restart.</summary>
public enum ForcedRestartRequestOutcome
{
    /// <summary>The request was left for the supervisor, which acts on it within a second.</summary>
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
/// supervisor ignores a request within a few seconds of the controller's start. Only an admin may ask, after
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
                "The supervisor was asked to kill and restart the controller.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ForcedRestartRequestResult(
                ForcedRestartRequestOutcome.Failed,
                $"The request could not be written to {path}: {ex.Message}");
        }
    }
}
