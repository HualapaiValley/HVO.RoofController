using System;
using System.Threading;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// <see cref="TimeProvider"/> whose wall clock can be stepped (as by an NTP correction, or a Pi without an RTC battery
/// setting its clock after boot) while the monotonic timestamp and timers keep following a <see cref="ManualTimeProvider"/>.
/// </summary>
internal sealed class SteppingWallClockTimeProvider : TimeProvider
{
    private readonly ManualTimeProvider _inner;
    private long _wallClockOffsetTicks;

    public SteppingWallClockTimeProvider(ManualTimeProvider? inner = null)
    {
        _inner = inner ?? new ManualTimeProvider();
    }

    /// <summary>How far the wall clock has been stepped from the monotonic time.</summary>
    public TimeSpan WallClockOffset => TimeSpan.FromTicks(Interlocked.Read(ref _wallClockOffsetTicks));

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + WallClockOffset;

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>Moves real time forward (the wall clock, the timestamp and timers alike). See <see cref="ManualTimeProvider.Advance"/>.</summary>
    public void Advance(TimeSpan delta) => _inner.Advance(delta);

    /// <summary>Steps only the wall clock, by <paramref name="delta"/> (negative steps it back).</summary>
    public void StepWallClock(TimeSpan delta) => Interlocked.Add(ref _wallClockOffsetTicks, delta.Ticks);
}
