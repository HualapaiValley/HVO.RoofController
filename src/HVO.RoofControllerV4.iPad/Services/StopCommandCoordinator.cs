using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

public enum StopState
{
    /// <summary>No Stop has been requested in this session.</summary>
    None,

    /// <summary>The request is being sent. Nothing is known yet.</summary>
    Sending,

    /// <summary>The controller accepted Stop but did not report a verified relay register.</summary>
    Acknowledged,

    /// <summary>
    /// The controller accepted Stop and its relay register read back all-off with no commanded motion. This is a
    /// register observation, not proof that the relay contacts opened or that the roof stopped.
    /// </summary>
    RelayRegisterVerified,

    /// <summary>The controller could not verify its relay register after Stop. Use the physical stop.</summary>
    RelayRegisterUnverified,

    /// <summary>The controller answered and refused Stop (for example a rejected API key).</summary>
    Rejected,

    /// <summary>No answer within the deadline. The controller may or may not have received Stop.</summary>
    OutcomeUnknown,

    /// <summary>Stop was not sent because the app has no valid controller address.</summary>
    NotSent
}

/// <summary>
/// Result of one Stop press. <see cref="Sequence"/> orders presses; only the newest is shown.
/// </summary>
public sealed record StopOutcome(
    long Sequence,
    StopState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    int Attempts,
    string? TargetHost,
    RoofStatusResponse? Snapshot,
    RoofControllerApiException? Error,
    string Message)
{
    public static StopOutcome Idle { get; } = new(0, StopState.None, DateTimeOffset.MinValue, null, 0, null, null, null, string.Empty);

    public bool IsPending => State == StopState.Sending;

    /// <summary>True when the operator must not assume the roof stopped.</summary>
    public bool RequiresPhysicalCheck => State is StopState.RelayRegisterUnverified or StopState.Rejected or StopState.OutcomeUnknown or StopState.NotSent;
}

/// <summary>Timing limits for the Stop path.</summary>
public sealed class StopCommandTimings
{
    /// <summary>End-to-end budget for one Stop press, including the retry.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Timeout for the first attempt; the rest of the deadline is left for one retry.</summary>
    public TimeSpan FirstAttemptTimeout { get; init; } = TimeSpan.FromMilliseconds(1800);

    /// <summary>A retry is only sent if at least this much of the deadline remains.</summary>
    public TimeSpan MinimumRetryWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Pause before the retry after a fast connection failure.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Extra wait beyond an attempt's timeout before the attempt is abandoned regardless of the HTTP stack.</summary>
    public TimeSpan AbandonGrace { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Sends Stop on its own path: it never waits for the status poll or another command, it has a bounded deadline,
/// it retries once on a connectivity failure (Stop is idempotent), and it reports an honest outcome, including
/// "outcome unknown" when no answer arrived.
/// </summary>
public sealed class StopCommandCoordinator
{
    public const int MaxAttempts = 2;

    private readonly RoofControllerConnection _connection;
    private readonly Func<RoofControllerEndpoint, TimeSpan, CancellationToken, Task<Result<RoofStatusResponse>>> _send;
    private readonly TimeProvider _timeProvider;
    private readonly StopCommandTimings _timings;
    private readonly object _sync = new();
    private long _nextSequence;
    private StopOutcome _current = StopOutcome.Idle;

    public StopCommandCoordinator(
        RoofControllerConnection connection,
        Func<RoofControllerEndpoint, TimeSpan, CancellationToken, Task<Result<RoofStatusResponse>>> send,
        TimeProvider? timeProvider = null,
        StopCommandTimings? timings = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timings = timings ?? new StopCommandTimings();
    }

    /// <summary>Raised (on any thread) whenever <see cref="Current"/> changes.</summary>
    public event EventHandler? OutcomeChanged;

    /// <summary>The outcome of the most recent Stop press. An older press finishing later never replaces it.</summary>
    public StopOutcome Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Sends Stop to the active controller. Every call sends, even while another Stop is in flight. The returned
    /// outcome is final (never <see cref="StopState.Sending"/>). Cancelling <paramref name="cancellationToken"/>
    /// abandons the wait; the result is then reported as <see cref="StopState.OutcomeUnknown"/>.
    /// </summary>
    public async Task<StopOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = _connection.Endpoint;
        var started = _timeProvider.GetUtcNow();
        long sequence;
        lock (_sync)
        {
            sequence = ++_nextSequence;
        }

        if (endpoint is null)
        {
            var notConfigured = new RoofControllerApiException(RoofControllerFailureKind.NotConfigured, "no valid controller URL is configured.");
            return Publish(new StopOutcome(sequence, StopState.NotSent, started, started, 0, null, null, notConfigured,
                "Stop NOT sent: no valid controller URL is configured. Use the physical stop."));
        }

        var host = endpoint.DisplayHost;
        Publish(new StopOutcome(sequence, StopState.Sending, started, null, 0, host, null, null, $"Sending Stop to {host}..."));

        var startTimestamp = _timeProvider.GetTimestamp();
        var attempts = 0;
        RoofControllerApiException? lastError = null;

        while (attempts < MaxAttempts)
        {
            var remaining = _timings.Deadline - _timeProvider.GetElapsedTime(startTimestamp);
            if (attempts > 0 && remaining < _timings.MinimumRetryWindow)
            {
                break;
            }

            var timeout = attempts == 0 && remaining > _timings.FirstAttemptTimeout ? _timings.FirstAttemptTimeout : remaining;
            attempts++;

            var result = await SendOnceAsync(endpoint, timeout, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccessful)
            {
                return Publish(Classify(sequence, started, attempts, host, result.Value));
            }

            lastError = RoofControllerApiException.From(result.Error);
            if (!lastError.IsRetryableForIdempotentRequest || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = _timings.RetryDelay;
            var remainingAfterDelay = _timings.Deadline - _timeProvider.GetElapsedTime(startTimestamp) - delay;
            if (attempts < MaxAttempts && delay > TimeSpan.Zero && remainingAfterDelay >= _timings.MinimumRetryWindow)
            {
                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return Publish(ClassifyFailure(sequence, started, attempts, host, lastError));
    }

    private async Task<Result<RoofStatusResponse>> SendOnceAsync(RoofControllerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            // The client enforces the timeout itself; WaitAsync is a hard bound in case the HTTP stack does not honour it.
            return await _send(endpoint, timeout, cancellationToken)
                .WaitAsync(timeout + _timings.AbandonGrace, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            return new RoofControllerApiException(RoofControllerFailureKind.Timeout, "No answer before the Stop deadline.", ex);
        }
        catch (OperationCanceledException ex)
        {
            return new RoofControllerApiException(RoofControllerFailureKind.Cancelled, "Stop was abandoned before an answer arrived.", ex);
        }
        catch (Exception ex)
        {
            return RoofControllerApiException.From(ex);
        }
    }

    private StopOutcome Classify(long sequence, DateTimeOffset started, int attempts, string host, RoofStatusResponse status)
    {
        var completed = _timeProvider.GetUtcNow();

        if (status.RelayRegisterState == RoofRelayRegisterState.Unverified || status.RelayRegisterMask is > 0)
        {
            return new StopOutcome(sequence, StopState.RelayRegisterUnverified, started, completed, attempts, host, status, null,
                $"Stop reached {host} but the relay register did NOT read back all-off. Use the physical stop.");
        }

        if (status.RelayRegisterState == RoofRelayRegisterState.Verified && status.RelayRegisterMask == 0 && status.CommandedMotion == RoofMotionDirection.None)
        {
            return new StopOutcome(sequence, StopState.RelayRegisterVerified, started, completed, attempts, host, status, null,
                $"Stop acknowledged by {host}; relay register reads all-off. This is a register read-back, not proof the contacts opened; watch the roof.");
        }

        return new StopOutcome(sequence, StopState.Acknowledged, started, completed, attempts, host, status, null,
            $"Stop acknowledged by {host}, but the relay register state was not reported as verified. Confirm the roof has stopped.");
    }

    private StopOutcome ClassifyFailure(long sequence, DateTimeOffset started, int attempts, string host, RoofControllerApiException? error)
    {
        var completed = _timeProvider.GetUtcNow();
        error ??= new RoofControllerApiException(RoofControllerFailureKind.Timeout, "No answer before the Stop deadline.");
        var reason = RoofControllerFailureDescriber.Describe(error, "Stop");

        if (error.Kind == RoofControllerFailureKind.NotConfigured)
        {
            return new StopOutcome(sequence, StopState.NotSent, started, completed, attempts, host, null, error,
                $"{reason} Use the physical stop.");
        }

        if (error.ErrorCode == RoofControllerErrorCode.RelayStateUnverified)
        {
            return new StopOutcome(sequence, StopState.RelayRegisterUnverified, started, completed, attempts, host, error.RoofStatus, error,
                $"Stop reached {host} but the controller could not verify its relays are off. Use the physical stop.");
        }

        if (IsOutcomeUnknown(error))
        {
            var tries = attempts == 1 ? "1 attempt" : $"{attempts} attempts";
            return new StopOutcome(sequence, StopState.OutcomeUnknown, started, completed, attempts, host, error.RoofStatus, error,
                $"Stop outcome UNKNOWN after {tries}: {reason} The controller may not have received it. Use the physical stop if the roof is moving.");
        }

        return new StopOutcome(sequence, StopState.Rejected, started, completed, attempts, host, error.RoofStatus, error,
            $"{reason} The roof was NOT stopped by this request. Use the physical stop if the roof is moving.");
    }

    private static bool IsOutcomeUnknown(RoofControllerApiException error)
    {
        if (error.IsConnectivityFailure || error.Kind is RoofControllerFailureKind.Cancelled or RoofControllerFailureKind.InvalidResponse)
        {
            return true;
        }

        // A server error without a definite controller code does not say whether the stop was applied.
        return error.StatusCode is >= 500 && error.ErrorCode is null or RoofControllerErrorCode.Unknown;
    }

    private StopOutcome Publish(StopOutcome outcome)
    {
        bool changed;
        lock (_sync)
        {
            changed = outcome.Sequence >= _current.Sequence;
            if (changed)
            {
                _current = outcome;
            }
        }

        if (changed)
        {
            OutcomeChanged?.Invoke(this, EventArgs.Empty);
        }

        return outcome;
    }
}
