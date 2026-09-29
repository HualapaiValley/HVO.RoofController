using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>
/// How the controller drives the roof, for the pages nobody has signed in to (the sign-in page): the controller's
/// anonymous mode read, over the container's loopback. Asked at most once every
/// <see cref="RoofWebOptions.StatusRefreshSeconds"/>, whatever the number of pages; null while the controller has not
/// answered, and the page then claims nothing.
/// </summary>
public sealed class WebControllerMode
{
    private readonly RoofControllerClient _client;
    private readonly TimeProvider _time;
    private readonly TimeSpan _refresh;
    private readonly ILogger<WebControllerMode> _logger;
    private readonly Lock _gate = new();
    private Task<RoofModeResponse?>? _read;
    private DateTimeOffset _readAt;

    public WebControllerMode(RoofControllerClient client, IOptions<RoofWebOptions> options, TimeProvider time, ILogger<WebControllerMode> logger)
    {
        _client = client;
        _time = time;
        _refresh = TimeSpan.FromSeconds(options.Value.StatusRefreshSeconds);
        _logger = logger;
    }

    /// <summary>The controller's mode, or null when it did not answer. Pages share one read.</summary>
    public Task<RoofModeResponse?> ReadAsync()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_read is null || now - _readAt >= _refresh)
            {
                _read = ReadFromControllerAsync();
                _readAt = now;
            }

            return _read;
        }
    }

    private async Task<RoofModeResponse?> ReadFromControllerAsync()
    {
        try
        {
            return await _client.Roof.GetModeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or RoofProtocolException or OperationCanceledException)
        {
            _logger.LogDebug("The controller did not say how it drives the roof ({Failure})", RoofText.DescribeFailure(ex));
            return null;
        }
    }
}
