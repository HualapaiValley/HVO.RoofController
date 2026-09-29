using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>The controller's answer to the readiness probe.</summary>
public enum ControllerReadiness
{
    /// <summary>/health/ready answered 200: the controller's hardware checks pass.</summary>
    Ready,

    /// <summary>The controller answered, but not ready (503, or another error).</summary>
    NotReady,

    /// <summary>No answer: the process is not listening, or did not answer in time.</summary>
    Unreachable,
}

/// <summary>The readiness probe's result, with what the controller said (or why it could not be asked).</summary>
public sealed record ControllerProbe(ControllerReadiness Readiness, string Detail);

/// <summary>One check of the controller's readiness and the supervisor's state.</summary>
public sealed record RoofWebStatus(ControllerProbe Controller, SupervisorReading Supervisor, DateTimeOffset CheckedAt);

/// <summary>Checks the controller's readiness (anonymous, over the container's loopback) and reads the supervisor's state.</summary>
public sealed class RoofWebStatusProbe
{
    private readonly RoofControllerClient _client;
    private readonly SupervisorStateReader _supervisor;
    private readonly TimeProvider _timeProvider;

    public RoofWebStatusProbe(RoofControllerClient client, SupervisorStateReader supervisor, TimeProvider timeProvider)
    {
        _client = client;
        _supervisor = supervisor;
        _timeProvider = timeProvider;
    }

    public async Task<RoofWebStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var readiness = ProbeControllerAsync(cancellationToken);
        var supervisor = _supervisor.ReadAsync(cancellationToken);
        return new RoofWebStatus(await readiness.ConfigureAwait(false), await supervisor.ConfigureAwait(false), _timeProvider.GetUtcNow());
    }

    private async Task<ControllerProbe> ProbeControllerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.Health.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
            var status = string.IsNullOrWhiteSpace(result.Status) ? "no status" : result.Status;
            return result.IsHealthy
                ? new ControllerProbe(ControllerReadiness.Ready, status)
                : new ControllerProbe(ControllerReadiness.NotReady, $"HTTP {(int)result.StatusCode}, {status}");
        }
        catch (RoofApiException ex)
        {
            return new ControllerProbe(ControllerReadiness.NotReady, $"HTTP {(int)ex.StatusCode}, {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            return new ControllerProbe(ControllerReadiness.Unreachable, ex.Message);
        }
    }
}
