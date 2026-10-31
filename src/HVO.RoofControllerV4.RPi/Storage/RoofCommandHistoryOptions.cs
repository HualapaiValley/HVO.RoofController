using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>
/// Configuration section <c>RoofCommandHistory</c>: where the roof command history is kept (#206) and for how long.
/// Read once, at startup.
/// </summary>
public sealed class RoofCommandHistoryOptions
{
    public const string SectionName = "RoofCommandHistory";

    /// <summary>The history file's name when <see cref="StorePath"/> is not set: next to the identity store.</summary>
    public const string DefaultFileName = "command-history.jsonl";

    internal static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(1);

    internal static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(3650);

    internal const int MinimumEntries = 100;

    // A worst-case file at this many (every line the longest an entry can be, the anonymous entries too, and a quarter
    // more lines before it is compacted: about 30 MB) stays inside half the 64 MB the installer's backups take in all,
    // which hold the identity store's directory whole.
    internal const int MostEntries = 20_000;

    /// <summary>
    /// The history file (JSON Lines, one entry per line, readable by the controller's user only). When empty, the file
    /// is <see cref="DefaultFileName"/> in the identity store's directory (<c>RoofControllerSecurity:Identity:StorePath</c>);
    /// with no identity store either, the history is kept in memory and lost when the controller restarts.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>How long an entry is kept: 1 day to 3650 days. Default 90 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(RoofCommandHistoryContract.DefaultRetentionDays);

    /// <summary>How many entries are kept, the oldest going first: 100 to 20,000. Default 10,000.</summary>
    public int MaximumEntries { get; set; } = RoofCommandHistoryContract.DefaultMaximumEntries;

    /// <summary>
    /// The addresses whose <c>X-Forwarded-For</c> the history believes: the web UI's, which names the person's browser.
    /// When not set, the controller's own loopback (127.0.0.0/8 and ::1), where the web UI in its container connects
    /// from; anything from elsewhere on the network reaches the controller through Docker's bridge and is never believed.
    /// </summary>
    public string[]? TrustedProxies { get; set; }

    /// <summary>
    /// The problems with these settings (empty when valid). The history never stops the controller from starting: a
    /// setting out of range is logged, and its default is used.
    /// </summary>
    internal IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Retention < MinimumRetention || Retention > MaximumRetention)
        {
            problems.Add($"{SectionName}:{nameof(Retention)} must be from 1 day to 3650 days; the default, {RoofCommandHistoryContract.DefaultRetentionDays} days, is used.");
        }

        if (MaximumEntries is < MinimumEntries or > MostEntries)
        {
            problems.Add($"{SectionName}:{nameof(MaximumEntries)} must be from {MinimumEntries} to {MostEntries}; the default, {RoofCommandHistoryContract.DefaultMaximumEntries}, is used.");
        }

        foreach (var proxy in TrustedProxies ?? [])
        {
            if (!IPAddress.TryParse(proxy?.Trim(), out _))
            {
                problems.Add($"{SectionName}:{nameof(TrustedProxies)} has '{proxy}', which is not an IP address; it is ignored.");
            }
        }

        return problems;
    }

    /// <summary>The trusted proxies that are addresses; null when none is configured (the loopback is trusted then).</summary>
    internal IReadOnlyList<IPAddress>? ParseTrustedProxies()
    {
        var parsed = (TrustedProxies ?? [])
            .Select(proxy => IPAddress.TryParse(proxy?.Trim(), out var address) ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) : null)
            .OfType<IPAddress>()
            .ToArray();
        return TrustedProxies is null or { Length: 0 } ? null : parsed;
    }
}
