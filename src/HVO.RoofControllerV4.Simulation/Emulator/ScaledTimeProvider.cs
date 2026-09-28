namespace HVO.RoofControllerV4.Simulation.Emulator;

/// <summary>
/// A clock that runs <see cref="Scale"/> times as fast as another (the system clock by default), for the HAT emulator's
/// plant. A scale change keeps the time continuous: the clock runs at the new rate from the time it had reached.
/// </summary>
/// <remarks>
/// Only <see cref="GetTimestamp"/> and <see cref="GetUtcNow"/> are scaled; the plant reads nothing else. Timers
/// created from this provider are the inner clock's timers, so they run on its time, unscaled.
/// </remarks>
public sealed class ScaledTimeProvider : TimeProvider
{
    public const double MinScale = 0.1;
    public const double MaxScale = 100;

    private readonly object _gate = new();
    private readonly TimeProvider _inner;
    private readonly DateTimeOffset _utcOrigin;
    private readonly long _timestampOrigin;
    private long _innerBase;
    private double _scaledBaseTicks;
    private double _scale;

    public ScaledTimeProvider(TimeProvider? inner = null, double scale = 1)
    {
        CheckScale(scale);
        _inner = inner ?? System;
        _utcOrigin = _inner.GetUtcNow();
        _timestampOrigin = _inner.GetTimestamp();
        _innerBase = _timestampOrigin;
        _scale = scale;
    }

    /// <summary>How many times as fast as the inner clock this clock runs, from <see cref="MinScale"/> to <see cref="MaxScale"/>.</summary>
    public double Scale
    {
        get { lock (_gate) { return _scale; } }
        set
        {
            CheckScale(value);
            lock (_gate)
            {
                var now = _inner.GetTimestamp();
                _scaledBaseTicks = ScaledTicks(now);
                _innerBase = now;
                _scale = value;
            }
        }
    }

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestampOrigin + (long)ScaledTicks(_inner.GetTimestamp());
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _inner.CreateTimer(callback, state, dueTime, period);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            var elapsed = ScaledTicks(_inner.GetTimestamp()) / _inner.TimestampFrequency;
            return _utcOrigin + TimeSpan.FromSeconds(elapsed);
        }
    }

    /// <summary>Timestamp ticks (inner frequency) since the origin on this clock.</summary>
    private double ScaledTicks(long innerNow) => _scaledBaseTicks + (innerNow - _innerBase) * _scale;

    private static void CheckScale(double scale)
    {
        if (double.IsNaN(scale) || scale < MinScale || scale > MaxScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), $"The time scale must be between {MinScale} and {MaxScale}.");
        }
    }
}
