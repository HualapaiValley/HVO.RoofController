using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Limits sign-in attempts from each address to <see cref="RoofWebOptions.SignInAttemptsPerMinute"/> a minute. The
/// controller has its own, stricter limits (failed sign-ins per name, and sign-ins per address); since the web UI calls
/// it over the container's loopback, every person using the web UI shares the controller's per-address limit, and this
/// limit keeps one visitor from using it up.
/// </summary>
public sealed class WebSignInLimiter
{
    /// <summary>The window the limit counts in.</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly IOptions<RoofWebOptions> _options;
    private readonly TimeProvider _time;
    private long _nextSweepTicks;

    public WebSignInLimiter(IOptions<RoofWebOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    /// <summary>True when another attempt from <paramref name="address"/> is allowed now (and counts it).</summary>
    public bool TryAcquire(IPAddress? address)
    {
        var now = _time.GetUtcNow();
        SweepIfDue(now);
        var key = address?.ToString() ?? "(unknown)";
        var counter = _counters.GetOrAdd(key, _ => new Counter());
        lock (counter)
        {
            if (now - counter.WindowStart >= Window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            if (counter.Count >= _options.Value.SignInAttemptsPerMinute)
            {
                return false;
            }

            counter.Count++;
            return true;
        }
    }

    /// <summary>The addresses counted (tests).</summary>
    internal int Count => _counters.Count;

    // Forgets addresses whose window has passed, at most once a window.
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + Window).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (key, counter) in _counters)
        {
            lock (counter)
            {
                if (now - counter.WindowStart >= Window)
                {
                    _counters.TryRemove(new KeyValuePair<string, Counter>(key, counter));
                }
            }
        }
    }

    private sealed class Counter
    {
        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.MinValue;

        public int Count { get; set; }
    }
}
