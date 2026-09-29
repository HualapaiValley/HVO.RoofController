using HVO.RoofControllerV4.Client;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>
/// Whether the controller is running and ready, for the pages nobody has signed in to (the sign-in page): the Health
/// page's check (<see cref="RoofWebStatusProbe"/>: the controller's anonymous readiness and the supervisor's state), so a
/// person who cannot sign in, because the controller is stopped or not answering, still sees why. Asked at most once
/// every <see cref="RoofWebOptions.StatusRefreshSeconds"/>, whatever the number of pages; null when the check itself
/// failed, and the page then claims nothing.
/// </summary>
public sealed class WebControllerStatus
{
    private readonly RoofWebStatusProbe _probe;
    private readonly TimeProvider _time;
    private readonly TimeSpan _refresh;
    private readonly ILogger<WebControllerStatus> _logger;
    private readonly Lock _gate = new();
    private Task<RoofWebStatusView?>? _read;
    private DateTimeOffset _readAt;

    public WebControllerStatus(RoofWebStatusProbe probe, IOptions<RoofWebOptions> options, TimeProvider time, ILogger<WebControllerStatus> logger)
    {
        _probe = probe;
        _time = time;
        _refresh = TimeSpan.FromSeconds(options.Value.StatusRefreshSeconds);
        _logger = logger;
    }

    /// <summary>
    /// The controller's status, headline only (the details, which may name the container's addresses, are for people
    /// signed in, on the Health page), or null when it could not be checked. Pages share one check.
    /// </summary>
    public Task<RoofWebStatusView?> ReadAsync()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_read is null || now - _readAt >= _refresh)
            {
                _read = CheckAsync();
                _readAt = now;
            }

            return _read;
        }
    }

    private async Task<RoofWebStatusView?> CheckAsync()
    {
        try
        {
            var view = RoofWebStatusText.DescribeController(await _probe.CheckAsync().ConfigureAwait(false));
            return view with { Details = [] };
        }
        catch (Exception ex) when (ex is RoofProtocolException or OperationCanceledException or IOException)
        {
            _logger.LogDebug("The controller's status could not be checked ({Failure})", RoofText.DescribeFailure(ex));
            return null;
        }
        catch (Exception ex)
        {
            // The headline is optional: every sign-in page awaits this shared check, so a failure it did not expect must
            // not fail them all (and take Stop off them) until the next refresh.
            _logger.LogWarning(ex, "The controller's status could not be checked ({Failure})", RoofText.DescribeFailure(ex));
            return null;
        }
    }
}
