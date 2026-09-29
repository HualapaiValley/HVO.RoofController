using System.Net;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Sends Stop on a connection of its own, so it never waits behind a slow command, a camera stream or a stuck request.
/// Nothing is locked or queued: two Stops in a row are two requests.
/// </summary>
internal sealed class RoofStopper : IDisposable
{
    private readonly RoofHttp _http;
    private readonly ILogger _logger;

    public RoofStopper(RoofConnectionOptions options, Func<RoofCredential?> credential, ILogger logger)
    {
        _http = new RoofHttp(options, credential, options.StopTimeout);
        _logger = logger;
    }

    public async Task<RoofStopResult> StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(HttpMethod.Post, RoofApiRoutes.Stop, null, RoofCredentialUse.Stop, cancellationToken).ConfigureAwait(false);
            RoofStatusResponse? status = null;
            try
            {
                status = await RoofHttp.ReadAsync<RoofStatusResponse>(response, cancellationToken).ConfigureAwait(false);
            }
            catch (RoofProtocolException ex)
            {
                // Accepted, but the snapshot could not be read: report the stop as acknowledged without a relay claim.
                _logger.LogWarning(ex, "Stop was accepted but its status could not be read");
            }

            var (outcome, message) = RoofStopText.Classify(true, status, null, string.Empty);
            return new RoofStopResult(outcome, message, status, null);
        }
        catch (RoofApiException ex)
        {
            _logger.LogWarning("Stop refused: HTTP {StatusCode} {Code}", (int)ex.StatusCode, ex.CodeText);
            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new RoofStopResult(RoofStopOutcome.Failed, RoofStopText.SignedOut, null, ex);
            }

            var (outcome, message) = RoofStopText.Classify(false, ex.RoofStatus, ex.Code, ex.Message);
            return new RoofStopResult(outcome, message, ex.RoofStatus, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or RoofProtocolException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Stop could not be sent or was not answered");
            return new RoofStopResult(RoofStopOutcome.Failed, RoofStopText.Failed(RoofText.DescribeFailure(ex)), null, ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
