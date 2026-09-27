using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Deterministic <see cref="TimeProvider"/>: time moves only through <see cref="Advance"/>, which fires due timers in
/// due-time order on the calling thread (outside the provider's lock). Timers never fire on creation.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _now.UtcTicks;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>
    /// When true, due timers are consumed without invoking their callbacks (simulates a lost timer callback, so tests can
    /// exercise the supervision backstop).
    /// </summary>
    public bool SuppressTimerCallbacks { get; set; }

    /// <summary>Number of timers currently scheduled.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.DueUtc is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing every timer that becomes due, in order.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + delta;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers
                    .Where(t => t.DueUtc is { } due && due <= target)
                    .OrderBy(t => t.DueUtc)
                    .FirstOrDefault();

                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.DueUtc!.Value > _now)
                {
                    _now = next.DueUtc.Value;
                }

                next.Reschedule_NoLock(_now);
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueUtc { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._lock)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                DueUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                return true;
            }
        }

        public void Reschedule_NoLock(DateTimeOffset now)
        {
            DueUtc = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;
        }

        public void Fire()
        {
            bool disposed;
            lock (_owner._lock)
            {
                disposed = _disposed;
            }

            if (!disposed && !_owner.SuppressTimerCallbacks)
            {
                _callback(_state);
            }
        }

        public void Dispose()
        {
            lock (_owner._lock)
            {
                _disposed = true;
                DueUtc = null;
            }

            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
