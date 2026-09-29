using System.Collections.Concurrent;
using System.Net;
using HVO.RoofControllerV4.Common.Net;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Limits <c>POST /stop</c> for each sender: <see cref="Burst"/> at once, then one more every <see cref="RefillInterval"/>
/// (four a second). A person pressing Stop, even over and over, never reaches it; a script posting Stop in a loop cannot
/// make the web UI flood the controller and its log (the controller never limits Stop). A signed-in person, or one with a
/// Stop pass, is counted by their session (<see cref="ForSession"/>); only a signed-out page is counted by its address
/// (<see cref="ForAddress"/>). Stops are counted after the page's token is checked, so posts from others at the same
/// address (behind a proxy or NAT) never use up a signed-in person's Stops. A post without a valid token reaches nothing,
/// and is counted apart (<see cref="ForRefusals"/>) only to limit how often it is logged; so are a sender's Stops beyond
/// their limit, by that sender (<see cref="ForTooMany"/>).
/// </summary>
public sealed class WebStopLimiter
{
    /// <summary>The Stops one sender may send at once.</summary>
    public const int Burst = 30;

    /// <summary>How often a sender earns another Stop, up to <see cref="Burst"/>.</summary>
    public static TimeSpan RefillInterval { get; } = TimeSpan.FromMilliseconds(250);

    // A sender that has earned back its whole burst is forgotten at the next sweep.
    private static readonly TimeSpan Full = RefillInterval * Burst;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _nextSweepTicks;

    public WebStopLimiter(TimeProvider time) => _time = time;

    /// <summary>A person's Stops, from any of their pages: counted by their session.</summary>
    public static string ForSession(string sessionId) => "session:" + sessionId;

    /// <summary>
    /// A signed-out page's Stops: counted by the address they come from (<see cref="RoofCallerAddress.Of"/>, which counts
    /// a public IPv6 address by its /64).
    /// </summary>
    public static string ForAddress(IPAddress? address, IPAddress? local) => "address:" + RoofCallerAddress.Of(address, local);

    /// <summary>
    /// Posts refused for their token, which reach nothing: counted by address, apart from the Stops sent, only to limit how
    /// often they are logged.
    /// </summary>
    public static string ForRefusals(IPAddress? address, IPAddress? local) => "refused:" + RoofCallerAddress.Of(address, local);

    /// <summary>
    /// <paramref name="sender"/>'s Stops beyond their limit (<see cref="ForSession"/> or <see cref="ForAddress"/>), counted
    /// only to limit how often they are logged: by that sender, so others' refused posts cannot keep them out of the log.
    /// </summary>
    public static string ForTooMany(string sender) => "too-many:" + sender;

    /// <summary>True when another Stop from <paramref name="sender"/> may be sent now (and counts it).</summary>
    public bool TryAcquire(string sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var now = _time.GetUtcNow();
        SweepIfDue(now);
        var bucket = _buckets.GetOrAdd(sender, _ => new Bucket(now));
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

    /// <summary>The senders counted (tests).</summary>
    internal int Count => _buckets.Count;

    // Forgets senders that have earned back their whole burst, at most once in that time.
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
