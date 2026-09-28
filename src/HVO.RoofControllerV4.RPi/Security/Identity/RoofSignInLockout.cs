using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>Whether a sign-in attempt may be checked (<see cref="RoofSignInLockout.TryBeginAttempt"/>).</summary>
public enum RoofSignInAdmission
{
    /// <summary>The attempt is reserved; end it with <see cref="RoofSignInLockout.EndAttempt"/>.</summary>
    Admitted,

    /// <summary>The name or kiosk is locked out.</summary>
    LockedOut,

    /// <summary>
    /// Every guess left before a lockout is already being checked, or (in practice never) every remembered unknown name
    /// has an attempt in progress. Retry shortly.
    /// </summary>
    Busy
}

/// <summary>How a reserved attempt ended (<see cref="RoofSignInLockout.EndAttempt"/>).</summary>
public enum RoofSignInOutcome
{
    /// <summary>The secret was right: failures and lockouts are forgotten.</summary>
    Succeeded,

    /// <summary>The secret was wrong: the failure counts towards a lockout.</summary>
    Failed,

    /// <summary>The secret was never checked (the hasher was busy, or the request was cancelled).</summary>
    NotChecked
}

/// <summary>
/// Counts failed sign-ins per name (<c>name:&lt;lower-case name&gt;</c>), failed PINs per name
/// (<c>pin:&lt;lower-case name&gt;</c>) and failed PINs per kiosk (<c>device:&lt;key id&gt;</c>). After
/// <see cref="RoofIdentityOptions.LockoutThreshold"/> failures in a row, sign-in for that name or at that kiosk is
/// refused for <see cref="RoofIdentityOptions.LockoutDuration"/>, doubling with each further lockout up to
/// <see cref="RoofIdentityOptions.MaximumLockoutDuration"/>. A success, or <see cref="RoofIdentityOptions.FailureMemory"/>
/// without a failure, starts again from nothing.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt is reserved before its secret is checked and counted when the check ends, so attempts sent in parallel
/// never get more guesses than the threshold: once failures plus attempts in progress reach it, further attempts are
/// refused as busy until those end.
/// </para>
/// <para>
/// Kept in memory: a restart forgets it, which costs an attacker a restart they cannot cause. Stop never signs in, so a
/// lockout can never keep anyone from stopping the roof. The names of people in the identity store and the kiosk keys
/// are "known" and always remembered: there are only as many as the store holds. A name that is not a person is counted
/// the same way, so its refusals look the same as a real name's, but at most <see cref="MaximumUnknownEntries"/> of
/// those are remembered; beyond that, the one tried least recently is forgotten. A flood of made-up names can therefore
/// only push out other made-up names: it never wipes a person's or a kiosk's count, and never refuses them.
/// </para>
/// </remarks>
public sealed class RoofSignInLockout
{
    internal const int MaximumUnknownEntries = 16384;

    /// <summary>How long to wait before retrying when every remaining guess is being checked.</summary>
    internal static readonly TimeSpan InProgressRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying when every remembered unknown name has an attempt in progress.</summary>
    internal static readonly TimeSpan FullRetry = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The keys of names that are not people, least recently tried first.</summary>
    private readonly LinkedList<string> _unknown = new();

    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly TimeProvider _time;

    public RoofSignInLockout(IOptionsMonitor<RoofIdentityOptions> options, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The key for sign-in by name and password.</summary>
    public static string ForName(string name) => "name:" + name.ToLowerInvariant();

    /// <summary>The key for PIN sign-in as <paramref name="name"/>, at any kiosk.</summary>
    public static string ForPin(string name) => "pin:" + name.ToLowerInvariant();

    /// <summary>The key for PIN sign-in at the kiosk holding the key <paramref name="keyId"/>.</summary>
    public static string ForDevice(string keyId) => "device:" + keyId;

    /// <summary>True while <paramref name="key"/> is locked out; <paramref name="retryAfter"/> is the time left.</summary>
    public bool IsLockedOut(string key, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_entries.TryGetValue(key, out var entry) && entry.LockedUntil > now)
            {
                retryAfter = entry.LockedUntil - now;
                return true;
            }
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Reserves one attempt for <paramref name="key"/> before its secret is checked. When admitted, the caller must end
    /// it with <see cref="EndAttempt"/> (in a <c>finally</c>); otherwise <paramref name="retryAfter"/> says when to retry.
    /// </summary>
    /// <param name="key">The name or kiosk (<see cref="ForName"/>, <see cref="ForPin"/>, <see cref="ForDevice"/>).</param>
    /// <param name="known">True for a person in the identity store or a kiosk key; false for any other name.</param>
    /// <param name="retryAfter">When refused, how long to wait.</param>
    public RoofSignInAdmission TryBeginAttempt(string key, bool known, out TimeSpan retryAfter)
    {
        var options = _options.CurrentValue;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_entries.TryGetValue(key, out var entry))
            {
                Classify(key, entry, known);
                if (entry.LockedUntil > now)
                {
                    retryAfter = entry.LockedUntil - now;
                    return RoofSignInAdmission.LockedOut;
                }

                ForgetIfStale(entry, now, options);
                if (entry.Failures + entry.InFlight >= options.LockoutThreshold)
                {
                    retryAfter = InProgressRetry;
                    return RoofSignInAdmission.Busy;
                }
            }
            else
            {
                if (!known && !MakeRoomForUnknown())
                {
                    retryAfter = FullRetry;
                    return RoofSignInAdmission.Busy;
                }

                entry = new Entry();
                _entries[key] = entry;
                Classify(key, entry, known);
            }

            entry.InFlight++;
            retryAfter = TimeSpan.Zero;
            return RoofSignInAdmission.Admitted;
        }
    }

    /// <summary>
    /// Ends an attempt reserved with <see cref="TryBeginAttempt"/>. Returns the lockout's length when this failure
    /// started one, otherwise null.
    /// </summary>
    public TimeSpan? EndAttempt(string key, RoofSignInOutcome outcome)
    {
        var options = _options.CurrentValue;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.InFlight == 0)
            {
                throw new InvalidOperationException("No sign-in attempt is in progress for this name or kiosk.");
            }

            var now = _time.GetUtcNow();
            entry.InFlight--;
            TimeSpan? lockedFor = outcome switch
            {
                RoofSignInOutcome.Failed => CountFailure(entry, now, options),
                RoofSignInOutcome.Succeeded => Forget(entry),
                _ => null
            };

            RemoveIfEmpty(key, entry, now);
            return lockedFor;
        }
    }

    /// <summary>Counts a failure without a reservation (tests).</summary>
    internal TimeSpan? RecordFailure(string key, bool known = true)
    {
        if (TryBeginAttempt(key, known, out _) != RoofSignInAdmission.Admitted)
        {
            return null;
        }

        return EndAttempt(key, RoofSignInOutcome.Failed);
    }

    /// <summary>Forgets <paramref name="key"/>'s failures and lockouts, as a success does (tests).</summary>
    internal void RecordSuccess(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                Forget(entry);
                RemoveIfEmpty(key, entry, _time.GetUtcNow());
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How many of <see cref="Count"/> are names that are not people (tests).</summary>
    internal int UnknownCount
    {
        get
        {
            lock (_gate)
            {
                return _unknown.Count;
            }
        }
    }

    private static TimeSpan? CountFailure(Entry entry, DateTimeOffset now, RoofIdentityOptions options)
    {
        ForgetIfStale(entry, now, options);
        entry.LastFailure = now;
        entry.Failures++;
        if (entry.Failures < options.LockoutThreshold)
        {
            return null;
        }

        var duration = options.LockoutDuration;
        for (var i = 0; i < entry.Lockouts && duration < options.MaximumLockoutDuration; i++)
        {
            duration += duration;
        }

        if (duration > options.MaximumLockoutDuration)
        {
            duration = options.MaximumLockoutDuration;
        }

        entry.Lockouts++;
        entry.Failures = 0;
        entry.LockedUntil = now + duration;
        return duration;
    }

    private static TimeSpan? Forget(Entry entry)
    {
        entry.Failures = 0;
        entry.Lockouts = 0;
        entry.LockedUntil = default;
        return null;
    }

    /// <summary>Failures and lockouts older than the memory no longer count (a lockout in force always does).</summary>
    private static void ForgetIfStale(Entry entry, DateTimeOffset now, RoofIdentityOptions options)
    {
        if (entry.LockedUntil <= now && now - entry.LastFailure >= options.FailureMemory)
        {
            Forget(entry);
        }
    }

    /// <summary>
    /// Keeps <paramref name="entry"/> among the unknown names, as the one tried most recently, or takes it out of them
    /// once the name is a person (and back in if the person is removed).
    /// </summary>
    private void Classify(string key, Entry entry, bool known)
    {
        if (entry.Recency is { } node)
        {
            _unknown.Remove(node);
            if (known)
            {
                entry.Recency = null;
            }
            else
            {
                _unknown.AddLast(node);
            }
        }
        else if (!known)
        {
            // A person or kiosk that was removed joins the unknown names; the cap holds unless every one of them has an
            // attempt in progress.
            MakeRoomForUnknown();
            entry.Recency = _unknown.AddLast(key);
        }
    }

    private void RemoveIfEmpty(string key, Entry entry, DateTimeOffset now)
    {
        if (entry.InFlight == 0 && entry.Failures == 0 && entry.Lockouts == 0 && entry.LockedUntil <= now)
        {
            _entries.Remove(key);
            if (entry.Recency is { } node)
            {
                _unknown.Remove(node);
                entry.Recency = null;
            }
        }
    }

    /// <summary>
    /// Makes room for one more unknown name by forgetting the ones tried least recently (never one with an attempt in
    /// progress); false only when every remembered unknown name has an attempt in progress.
    /// </summary>
    private bool MakeRoomForUnknown()
    {
        var node = _unknown.First;
        while (_unknown.Count >= MaximumUnknownEntries && node is not null)
        {
            var next = node.Next;
            if (_entries[node.Value].InFlight == 0)
            {
                _entries.Remove(node.Value);
                _unknown.Remove(node);
            }

            node = next;
        }

        return _unknown.Count < MaximumUnknownEntries;
    }

    private sealed class Entry
    {
        public int Failures;
        public int Lockouts;
        public int InFlight;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;

        /// <summary>This entry's place among the unknown names; null for a person or a kiosk.</summary>
        public LinkedListNode<string>? Recency;
    }
}
