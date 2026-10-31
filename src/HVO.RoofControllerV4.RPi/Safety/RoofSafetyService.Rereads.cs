using System.Globalization;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>A telescope source that can re-read itself at the last moment for an Alpaca command (#220).</summary>
public interface ITelescopeRereadSource : ITelescopeParkSource
{
    /// <summary>
    /// The reads a Close needs, sent in parallel: <c>connected</c> and <c>atpark</c>, and <c>canpark</c> too until a clean
    /// full read since the start has shown the mount can park. Clear needs every answer clean, connected true and parked;
    /// anything else throws (a failed read), so a configuration error is never cleared by a re-read.
    /// </summary>
    Task<TelescopeParkReading> RereadAsync(CancellationToken cancellationToken);

    /// <summary>How long its recent GETs took (background reads and re-reads), newest last.</summary>
    IReadOnlyList<TimeSpan> RecentLatencies { get; }
}

/// <summary>A blocking telescope as a command's policy judges it (#220): clear, not clear, or unknown.</summary>
public enum RoofScopeClearance
{
    Clear,
    NotClear,
    Unknown
}

/// <summary>One blocking telescope judged for one command.</summary>
/// <param name="Name">The source's name, as reason keys carry it.</param>
/// <param name="Clearance">Clear, not clear, or unknown.</param>
/// <param name="Text">Why, in words fit to show.</param>
/// <param name="IssuedAfterArrival">True when a read of it was issued after the command arrived (the fresh-evidence guard).</param>
public sealed record RoofScopeJudgement(string Name, RoofScopeClearance Clearance, string Text, bool IssuedAfterArrival);

// The last-moment re-reads of the blocking telescopes for Alpaca commands (#220, §2, §16 Z1, §18 R8-1/R8-2, §19 L2/L4,
// §20 N5/N7): per telescope at most two in flight, issued no closer than 250 ms apart; a command uses only a read issued
// after it arrived (its ticket's issue); each read's result goes into the telescope's one reading slot, ordered by issue.
public sealed partial class RoofSafetyService
{
    /// <summary>The most re-reads of one telescope in flight at once.</summary>
    internal const int MaxRereadsInFlight = 2;

    /// <summary>The shortest time between two re-reads of one telescope.</summary>
    internal static readonly TimeSpan RereadSpacing = TimeSpan.FromMilliseconds(250);

    private long _sequence;
    private volatile bool _running;

    /// <summary>True once the service has started reading its sources (the live list exists).</summary>
    public bool IsRunning => _running;

    /// <summary>The controller's monotonic clock, as tickets record a command's arrival.</summary>
    public long Timestamp() => _time.GetTimestamp();

    /// <summary>The names of the telescopes that can block the roof, as the service reads them (the live list).</summary>
    public IReadOnlyList<string> LiveBlockingScopes
        => _running ? _sources.Where(IsBlocking).Select(source => source.Name).ToArray() : [];

    private static bool IsBlocking(Source source) => source.Kind == RoofSafetySourceKind.TelescopePark && source.Counts;

    /// <summary>Starts a read of <paramref name="source"/> in its slot: its request sequence, and when it was issued.</summary>
    private long Issue_NoLock(Source source)
    {
        var sequence = ++_sequence;
        var now = _time.GetTimestamp();
        source.IssuedTimestamps[sequence] = now;
        source.LatestIssuedTimestamp = now;
        return sequence;
    }

    /// <summary>
    /// Re-reads every blocking telescope for a command that arrived at <paramref name="arrival"/> (monotonic), and waits
    /// until each has an answer or <paramref name="budgetEnd"/> passes. A re-read in flight that was issued after the
    /// arrival, or one queued to be, is joined instead of starting another; a joined re-read is cancelled only at the
    /// latest budget end among its commands, and never later than the read timeout.
    /// </summary>
    public async Task RereadBlockingScopesAsync(long arrival, long budgetEnd)
    {
        if (!_running)
        {
            return;
        }

        var waits = new List<Task>();
        lock (_gate)
        {
            foreach (var source in _sources.Where(IsBlocking))
            {
                waits.Add(RequestReread_NoLock(source, arrival, budgetEnd));
            }
        }

        if (waits.Count == 0)
        {
            return;
        }

        var remaining = _time.GetElapsedTime(_time.GetTimestamp(), budgetEnd);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        await Task.WhenAll(waits).WaitAsync(remaining, _time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Judges every configured blocking telescope for a command that arrived at <paramref name="arrival"/>, with ages
    /// judged now: clear only when the newest read that ended was issued after the arrival, succeeded and says clear, and
    /// is still within its <see cref="IRoofSafetySource.MaxReadingAge"/>. A configured telescope missing from the live list
    /// (the service not started, or a source that did not load) is unknown. Never waits.
    /// </summary>
    /// <param name="arrival">When the command arrived (its ticket's issue), monotonic.</param>
    /// <param name="configured">The configured blocking telescopes' names (the validated configuration's).</param>
    public IReadOnlyList<RoofScopeJudgement> JudgeBlockingScopes(long arrival, IReadOnlyCollection<string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var options = Options();
        var judgements = new List<RoofScopeJudgement>();
        lock (_gate)
        {
            var live = _running ? _sources.Where(IsBlocking).ToDictionary(source => source.Name, StringComparer.Ordinal) : [];
            foreach (var name in configured.Distinct(StringComparer.Ordinal))
            {
                var label = $"Telescope '{name}'";
                if (!live.TryGetValue(name, out var source))
                {
                    judgements.Add(new RoofScopeJudgement(name, RoofScopeClearance.Unknown, $"{label} is not being read", IssuedAfterArrival: false));
                    continue;
                }

                var issuedAfter = source.LatestIssuedTimestamp is { } latest && latest >= arrival;
                if (source.ReadingIssuedTimestamp is not { } readingIssued || readingIssued < arrival || source.LastSafe is not { } clear
                    || source.LastReadTimestamp is not { } readAt)
                {
                    var why = source.LastAttemptFailed && source.ShownFailure is { } failure ? $" ({failure})" : string.Empty;
                    judgements.Add(new RoofScopeJudgement(name, RoofScopeClearance.Unknown,
                        $"{label} could not be read just now{why}", issuedAfter));
                    continue;
                }

                if (source.LastAttemptFailed)
                {
                    var why = source.ShownFailure is { } failure ? $" ({failure})" : string.Empty;
                    judgements.Add(new RoofScopeJudgement(name, RoofScopeClearance.Unknown, $"{label} could not be read just now{why}", issuedAfter));
                    continue;
                }

                var maxAge = source.Inner.MaxReadingAge is { } own && own > TimeSpan.Zero ? own : options.MaxReadingAge;
                if (_time.GetElapsedTime(readAt) + source.ObservedLag > maxAge)
                {
                    judgements.Add(new RoofScopeJudgement(name, RoofScopeClearance.Unknown,
                        string.Create(CultureInfo.InvariantCulture, $"{label}: the reading is older than {maxAge.TotalSeconds:0.#} s"), issuedAfter));
                    continue;
                }

                judgements.Add(clear
                    ? new RoofScopeJudgement(name, RoofScopeClearance.Clear, $"{label} is {source.LastSaid}", issuedAfter)
                    : new RoofScopeJudgement(name, RoofScopeClearance.NotClear, $"{label} is {source.LastSaid}", issuedAfter));
            }
        }

        return judgements;
    }

    /// <summary>Each blocking telescope's GET latency (p95, p99) from its recent reads; empty for sources that do not record it.</summary>
    public IReadOnlyList<RoofScopeLatency> BlockingScopeLatencies()
    {
        var latencies = new List<RoofScopeLatency>();
        foreach (var source in _sources.Where(IsBlocking))
        {
            if (source.Inner is not ITelescopeRereadSource reread)
            {
                continue;
            }

            var samples = reread.RecentLatencies.Select(latency => latency.TotalMilliseconds).Order().ToArray();
            latencies.Add(new RoofScopeLatency(source.Name, Percentile(samples, 0.95), Percentile(samples, 0.99), samples.Length));
        }

        return latencies;
    }

    private static double? Percentile(double[] sorted, double fraction)
        => sorted.Length == 0 ? null : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private Task RequestReread_NoLock(Source source, long arrival, long budgetEnd)
    {
        var state = source.Rereads;

        // A read issued after this command arrived has already ended (the one its ticket started, say): it is the answer.
        if (source.CompletedIssuedTimestamp is { } completed && completed >= arrival)
        {
            return Task.CompletedTask;
        }

        // Join a re-read issued after this command arrived, or the one queued to be issued (which will be).
        var joined = state.InFlight.LastOrDefault(reread => reread.IssuedTimestamp >= arrival) ?? state.Queued;
        if (joined is not null)
        {
            Extend_NoLock(joined, budgetEnd);
            return joined.Done.Task;
        }

        var reread = new Reread { CancelAt = budgetEnd };
        var now = _time.GetTimestamp();
        if (state.InFlight.Count < MaxRereadsInFlight && EarliestIssue_NoLock(state) <= now)
        {
            IssueReread_NoLock(source, reread);
        }
        else
        {
            state.Queued = reread;
            ScheduleQueued_NoLock(source);
        }

        return reread.Done.Task;
    }

    private long EarliestIssue_NoLock(RereadState state)
        => state.LastIssueTimestamp is { } last ? last + (long)(RereadSpacing.TotalSeconds * _time.TimestampFrequency) : long.MinValue;

    /// <summary>A joining command's budget end extends the re-read's cancellation, capped at the read timeout from its issue.</summary>
    private void Extend_NoLock(Reread reread, long budgetEnd)
    {
        if (budgetEnd <= reread.CancelAt)
        {
            return;
        }

        reread.CancelAt = budgetEnd;
        if (reread.IssuedTimestamp != 0)
        {
            ArmCancel_NoLock(reread);
        }
    }

    private void ScheduleQueued_NoLock(Source source)
    {
        var state = source.Rereads;
        if (state.Queued is not { } queued)
        {
            return;
        }

        queued.Timer?.Dispose();
        var now = _time.GetTimestamp();
        var due = Math.Max(EarliestIssue_NoLock(state), now);
        if (due > queued.CancelAt)
        {
            // It could not be issued before every waiting command's budget ends: it ends unread.
            state.Queued = null;
            queued.Done.TrySetResult();
            return;
        }

        var wait = _time.GetElapsedTime(now, due);
        queued.Timer = _time.CreateTimer(_ => TryIssueQueued(source), null, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void TryIssueQueued(Source source)
    {
        lock (_gate)
        {
            var state = source.Rereads;
            if (state.Queued is not { } queued)
            {
                return;
            }

            var now = _time.GetTimestamp();
            if (now > queued.CancelAt)
            {
                state.Queued = null;
                queued.Timer?.Dispose();
                queued.Done.TrySetResult();
                return;
            }

            if (state.InFlight.Count < MaxRereadsInFlight && EarliestIssue_NoLock(state) <= now)
            {
                state.Queued = null;
                queued.Timer?.Dispose();
                IssueReread_NoLock(source, queued);
            }
            else if (state.InFlight.Count < MaxRereadsInFlight)
            {
                ScheduleQueued_NoLock(source);
            }

            // Otherwise a re-read in flight ends first and issues it (CompleteReread).
        }
    }

    private void IssueReread_NoLock(Source source, Reread reread)
    {
        var state = source.Rereads;
        reread.Sequence = Issue_NoLock(source);
        reread.IssuedTimestamp = _time.GetTimestamp();
        state.LastIssueTimestamp = reread.IssuedTimestamp;
        state.InFlight.Add(reread);
        ArmCancel_NoLock(reread);
        var token = reread.Cancellation.Token;
        reread.Read = Task.Run(() => RereadAsync(source, reread, token), CancellationToken.None);
    }

    /// <summary>Cancels the re-read at its latest budget end, never later than the read timeout from its issue.</summary>
    private void ArmCancel_NoLock(Reread reread)
    {
        var cap = reread.IssuedTimestamp + (long)(Options().ReadTimeout.TotalSeconds * _time.TimestampFrequency);
        var at = Math.Min(reread.CancelAt, cap);
        var wait = _time.GetElapsedTime(_time.GetTimestamp(), at);
        reread.CancelTimer?.Dispose();
        reread.CancelTimer = _time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), reread.Cancellation,
            wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private async Task RereadAsync(Source source, Reread reread, CancellationToken token)
    {
        try
        {
            var park = (ITelescopeParkSource)source.Inner;
            var reading = park is ITelescopeRereadSource fast
                ? await fast.RereadAsync(token).ConfigureAwait(false)
                : await park.ReadParkStateAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var (clear, evidence) = TelescopeRoofClearance.Judge(reading);
            RecordReading(source, reread.Sequence, clear, evidence, reading.ObservedUtc, reading.Detail);
        }
        catch (Exception ex)
        {
            var failure = token.IsCancellationRequested ? "the re-read timed out"
                : ex is RoofSafetySourceException described ? Bounded(described.Message)
                : ex.GetType().Name;
            lock (_gate)
            {
                RecordFailure_NoLock(source, reread.Sequence, failure, shown: token.IsCancellationRequested || ex is RoofSafetySourceException, out _);
            }

            _logger.LogInformation("Safety source {Kind} '{Source}': the last-moment re-read failed ({Failure}); it counts as unknown for the command.",
                source.Kind, source.Name, failure);
        }
        finally
        {
            lock (_gate)
            {
                var state = source.Rereads;
                state.InFlight.Remove(reread);
                reread.CancelTimer?.Dispose();
                if (state.Queued is not null)
                {
                    ScheduleQueued_NoLock(source);
                }
            }

            // The token source is left to the collector: a cancel timer firing as the read ends must not meet a disposed one.
            reread.Done.TrySetResult();
        }
    }

    /// <summary>A telescope's last-moment re-reads (guarded by the service's gate).</summary>
    private sealed class RereadState
    {
        public List<Reread> InFlight { get; } = [];

        /// <summary>The next re-read, waiting for a slot or the spacing; commands that arrive meanwhile join it.</summary>
        public Reread? Queued { get; set; }

        public long? LastIssueTimestamp { get; set; }
    }

    private sealed class Reread
    {
        public long Sequence { get; set; }

        /// <summary>When it was issued (monotonic); 0 while queued.</summary>
        public long IssuedTimestamp { get; set; }

        /// <summary>The latest budget end among its commands (monotonic).</summary>
        public long CancelAt { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Timer { get; set; }

        public ITimer? CancelTimer { get; set; }

        public Task? Read { get; set; }
    }
}
