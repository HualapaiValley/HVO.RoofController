using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Decides when to renew the controller's operator lease. The lease is a dead-man control: it is renewed only for
/// motion this app requested, only while the app is in the foreground, and only while the controller reports a
/// lease (<see cref="RoofStatusResponse.LeaseSecondsRemaining"/>). If the app is backgrounded or loses the
/// controller, renewals stop and the controller stops the roof when the lease runs out.
/// </summary>
public sealed class LeaseRenewalTracker
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private RoofMotionDirection _requested;
    private string? _instanceId;
    private double? _leaseSecondsRemaining;
    private double _maxObservedLeaseSeconds;
    private long _nextDueTimestamp;
    private bool _inFlight;

    public LeaseRenewalTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Motion this app asked for and is keeping alive, or None.</summary>
    public RoofMotionDirection TrackedMotion
    {
        get
        {
            lock (_sync)
            {
                return _requested;
            }
        }
    }

    /// <summary>Renewal interval: a third of the largest lease the controller has reported for this motion.</summary>
    public TimeSpan Interval
    {
        get
        {
            lock (_sync)
            {
                return GetInterval();
            }
        }
    }

    /// <summary>
    /// Records that this app asked for <paramref name="direction"/> (after the Open/Close request completed, whether
    /// or not its outcome is known) and applies the snapshot the request returned, if any.
    /// </summary>
    public void NoteMotionRequested(RoofMotionDirection direction, RoofStatusResponse? status)
    {
        lock (_sync)
        {
            ResetLocked();
            if (direction == RoofMotionDirection.None)
            {
                return;
            }

            _requested = direction;
            _nextDueTimestamp = _timeProvider.GetTimestamp();
            if (status is not null)
            {
                ObserveLocked(status);
                if (_leaseSecondsRemaining is not null)
                {
                    _nextDueTimestamp += ToTimestampTicks(GetInterval());
                }
            }
        }
    }

    /// <summary>Applies an accepted status snapshot. Stops tracking when the requested motion is no longer commanded.</summary>
    public void Observe(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
        {
            ObserveLocked(status);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ResetLocked();
        }
    }

    /// <summary>
    /// Returns true when a renewal should be sent now and marks it in flight. Call <see cref="CompleteRenewal"/>
    /// when the request finishes.
    /// </summary>
    public bool TryBeginRenewal(bool isForeground)
    {
        lock (_sync)
        {
            if (!isForeground || _inFlight || _requested == RoofMotionDirection.None || _leaseSecondsRemaining is null)
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (now < _nextDueTimestamp)
            {
                return false;
            }

            _inFlight = true;
            return true;
        }
    }

    /// <summary>
    /// Finishes a renewal. <paramref name="status"/> is the accepted snapshot the controller returned (null on
    /// failure); <paramref name="leaseNotActive"/> is true when the controller answered <c>LeaseNotActive</c>.
    /// </summary>
    public void CompleteRenewal(RoofStatusResponse? status, bool leaseNotActive)
    {
        lock (_sync)
        {
            _inFlight = false;

            if (leaseNotActive)
            {
                ResetLocked();
                return;
            }

            if (_requested == RoofMotionDirection.None)
            {
                return;
            }

            var now = _timeProvider.GetTimestamp();
            if (status is null)
            {
                var retry = GetInterval() < FailureRetryInterval ? GetInterval() : FailureRetryInterval;
                _nextDueTimestamp = now + ToTimestampTicks(retry);
                return;
            }

            ObserveLocked(status);
            if (_requested != RoofMotionDirection.None)
            {
                _nextDueTimestamp = now + ToTimestampTicks(GetInterval());
            }
        }
    }

    private void ObserveLocked(RoofStatusResponse status)
    {
        if (_requested == RoofMotionDirection.None)
        {
            return;
        }

        var instanceId = string.IsNullOrWhiteSpace(status.ControllerInstanceId) ? null : status.ControllerInstanceId;
        if (_instanceId is not null && instanceId is not null && !string.Equals(_instanceId, instanceId, StringComparison.Ordinal))
        {
            ResetLocked();
            return;
        }

        if (status.CommandedMotion != _requested)
        {
            ResetLocked();
            return;
        }

        _instanceId ??= instanceId;
        _leaseSecondsRemaining = status.LeaseSecondsRemaining;
        if (status.LeaseSecondsRemaining is { } lease && lease > _maxObservedLeaseSeconds)
        {
            _maxObservedLeaseSeconds = lease;
        }
    }

    private TimeSpan GetInterval()
    {
        var third = TimeSpan.FromSeconds(_maxObservedLeaseSeconds / 3.0);
        return third > MinimumInterval ? third : MinimumInterval;
    }

    private long ToTimestampTicks(TimeSpan span) => (long)(span.TotalSeconds * _timeProvider.TimestampFrequency);

    private void ResetLocked()
    {
        _requested = RoofMotionDirection.None;
        _instanceId = null;
        _leaseSecondsRemaining = null;
        _maxObservedLeaseSeconds = 0;
        _nextDueTimestamp = 0;
    }
}
