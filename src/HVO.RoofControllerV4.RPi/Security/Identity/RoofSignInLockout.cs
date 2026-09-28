using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>
/// Counts failed sign-ins per name (<c>name:&lt;lower-case name&gt;</c>) and failed PIN attempts per kiosk
/// (<c>device:&lt;key id&gt;</c>). After <see cref="RoofIdentityOptions.LockoutThreshold"/> failures in a row, sign-in
/// for that name or at that kiosk is refused for <see cref="RoofIdentityOptions.LockoutDuration"/>, doubling with each
/// further lockout up to <see cref="RoofIdentityOptions.MaximumLockoutDuration"/>. A success, or
/// <see cref="RoofIdentityOptions.FailureMemory"/> without a failure, starts again from nothing.
/// </summary>
/// <remarks>
/// Kept in memory: a restart forgets it, which costs an attacker a restart they cannot cause. Stop never signs in, so a
/// lockout can never keep anyone from stopping the roof. At most <see cref="MaximumEntries"/> names and kiosks are
/// remembered; past that, entries that are not locked out are forgotten first, oldest failure first.
/// </remarks>
internal sealed class RoofSignInLockout
{
    internal const int MaximumEntries = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly TimeProvider _time;

    public RoofSignInLockout(IOptionsMonitor<RoofIdentityOptions> options, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The key for sign-in by name and password.</summary>
    public static string ForName(string name) => "name:" + name.ToLowerInvariant();

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
    /// Counts a failure. Returns the lockout's length when this failure started one, otherwise null.
    /// </summary>
    public TimeSpan? RecordFailure(string key)
    {
        var options = _options.CurrentValue;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_entries.TryGetValue(key, out var entry) || now - entry.LastFailure >= options.FailureMemory)
            {
                entry = new Entry();
                MakeRoom(now);
                _entries[key] = entry;
            }

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
    }

    /// <summary>Forgets <paramref name="key"/>'s failures and lockouts after a successful sign-in.</summary>
    public void RecordSuccess(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
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

    private void MakeRoom(DateTimeOffset now)
    {
        if (_entries.Count < MaximumEntries)
        {
            return;
        }

        var forget = _entries
            .OrderBy(pair => pair.Value.LockedUntil > now ? 1 : 0)
            .ThenBy(pair => pair.Value.LastFailure)
            .Take(_entries.Count - MaximumEntries + 1)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in forget)
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public int Failures;
        public int Lockouts;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;
    }
}
