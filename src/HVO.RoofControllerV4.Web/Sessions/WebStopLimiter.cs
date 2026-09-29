using System.Collections.Concurrent;
using System.Net;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Limits <c>POST /stop</c> from each address: <see cref="Burst"/> at once, then one more every
/// <see cref="RefillInterval"/> (four a second). A person pressing Stop, even over and over, never reaches it; a script
/// posting Stop in a loop cannot make the web UI flood the controller and its log (the controller never limits Stop).
/// </summary>
public sealed class WebStopLimiter
{
    /// <summary>The Stops one address may send at once.</summary>
    public const int Burst = 30;

    /// <summary>How often an address earns another Stop, up to <see cref="Burst"/>.</summary>
    public static TimeSpan RefillInterval { get; } = TimeSpan.FromMilliseconds(250);

    // An address that has earned back its whole burst is forgotten at the next sweep.
    private static readonly TimeSpan Full = RefillInterval * Burst;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _nextSweepTicks;

    public WebStopLimiter(TimeProvider time) => _time = time;

    /// <summary>True when another Stop from <paramref name="address"/> may be sent now (and counts it).</summary>
    public bool TryAcquire(IPAddress? address)
    {
        var now = _time.GetUtcNow();
        SweepIfDue(now);
        var bucket = _buckets.GetOrAdd(address?.ToString() ?? "(unknown)", _ => new Bucket(now));
        lock (bucket)
        {
            var earned = (now - bucket.CountedAt) / RefillInterval;
            if (earned >= 1)
            {
                bucket.Tokens = Math.Min(Burst, bucket.Tokens + (int)Math.Min(earned, Burst));
                bucket.CountedAt = bucket.Tokens == Burst ? now : bucket.CountedAt + RefillInterval * Math.Floor(earned);
            }

            if (bucket.Tokens == 0)
            {
                return false;
            }

            if (bucket.Tokens == Burst)
            {
                bucket.CountedAt = now;
            }

            bucket.Tokens--;
            return true;
        }
    }

    /// <summary>The addresses counted (tests).</summary>
    internal int Count => _buckets.Count;

    // Forgets addresses that have earned back their whole burst, at most once in that time.
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + Full).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                if (now - bucket.CountedAt >= Full)
                {
                    _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket));
                }
            }
        }
    }

    private sealed class Bucket(DateTimeOffset now)
    {
        public int Tokens { get; set; } = Burst;

        // When Tokens was last brought up to date: whole refill intervals since then have not been counted yet.
        public DateTimeOffset CountedAt { get; set; } = now;
    }
}
