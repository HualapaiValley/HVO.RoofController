using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Net;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>A request for a page of the history, newest first.</summary>
public sealed record RoofCommandHistoryFilter
{
    /// <summary>
    /// Only commands recorded under one of these identifiers (<see cref="RoofCommandHistoryPrincipals"/>): the caller's
    /// own, or those an admin asked for. Null: everyone's (an admin's request). Never a name.
    /// </summary>
    public IReadOnlyCollection<string>? PrincipalIds { get; init; }

    /// <summary>
    /// Whether the page says it may hold everyone's commands: true for an admin's request, filtered or not. Null: when
    /// <see cref="PrincipalIds"/> is null.
    /// </summary>
    public bool? AllUsers { get; init; }

    /// <summary>Only commands at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only commands before this time.</summary>
    public DateTimeOffset? To { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = RoofCommandHistoryContract.DefaultTake;

    internal bool Matches(RoofCommandHistoryEntry entry)
        => (PrincipalIds is null || (entry.PrincipalId is { } id && PrincipalIds.Contains(id)))
            && (From is not { } from || (entry.LastTimestampUtc ?? entry.TimestampUtc) >= from)
            && (To is not { } to || entry.TimestampUtc < to);
}

/// <summary>
/// The roof command history (#206): every Open, Close, Stop and Clear fault the API was sent, accepted or refused, and
/// refused lease renewals, with who sent it (by <see cref="RoofCommandHistoryPrincipals">identifier</see>, never by name
/// alone), from where, and the roof's status afterwards. Kept in memory and in the history file
/// (<see cref="RoofCommandHistoryOptions.StorePath"/>), for <see cref="RoofCommandHistoryOptions.Retention"/> and up to
/// <see cref="RoofCommandHistoryOptions.MaximumEntries"/> entries, besides up to
/// <see cref="RoofCommandHistoryContract.MaximumAnonymousEntries"/> anonymous ones.
/// </summary>
/// <remarks>
/// <para>Recording never waits for the disk and never throws for it: an entry is kept in memory at once, and this hosted
/// service appends it to the file in the background (within about a second, in one write for what came meanwhile), so a
/// slow or failing disk never delays a command's answer (a Stop's above all). A file that cannot be read leaves the
/// history in memory only, and the file as it is.</para>
/// <para>Stop is never limited. Anonymous Stops (<c>AllowAnonymousStop</c>) from one source with the same answer, each
/// within <see cref="AnonymousStopWindow"/> of the last, are kept as one entry with a count, and anonymous entries have a
/// cap of their own, so nobody signed out can push anyone's commands out of the history.</para>
/// </remarks>
public sealed class RoofCommandHistory : BackgroundService
{
    /// <summary>How soon a write that failed is tried again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long the writer gathers what is recorded before it writes: one write, not one per command.</summary>
    internal static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(1);

    /// <summary>Anonymous Stops from one source with the same answer, each this soon after the last, are one entry.</summary>
    internal static readonly TimeSpan AnonymousStopWindow = TimeSpan.FromMinutes(10);

    private const int MaximumDetailLength = 200;

    // The safety interlock's decision for an Open, noted on the request (NoteSafety).
    private static readonly object SafetyItem = new();
    private const int MaximumClientLength = 100;

    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TimeProvider _time;
    private readonly ILogger<RoofCommandHistory> _logger;
    private readonly RoofCommandHistoryPrincipals _principals;
    private readonly RoofCommandHistoryFile? _file;
    private readonly TimeSpan _retention;
    private readonly int _maximumEntries;
    private readonly IReadOnlyList<IPAddress>? _trustedProxies;

    // Oldest first (by identifier). Guarded by _gate, as are _pending, _lastId, _sequence, _compact and _anonymousStops.
    private readonly List<RoofCommandHistoryEntry> _entries = [];

    // Recorded or changed and not yet written to the file, each with the sequence number it was queued at.
    private readonly List<(long Sequence, RoofCommandHistoryEntry Entry)> _pending = [];

    // For each anonymous source, the identifier of its latest anonymous Stop entry.
    private readonly Dictionary<string, long> _anonymousStops = new(StringComparer.Ordinal);
    private long _lastId;
    private long _sequence;
    private bool _compact;

    // Guarded by _fileGate: only the writer touches the file.
    private int _fileLines;
    private bool _fileEndsWithNewline = true;
    private bool _writeFailing;

    public RoofCommandHistory(
        IOptions<RoofCommandHistoryOptions> options,
        IOptions<RoofIdentityOptions> identity,
        TimeProvider time,
        ILogger<RoofCommandHistory> logger,
        RoofCommandHistoryPrincipals? principals = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _principals = principals ?? new RoofCommandHistoryPrincipals();

        var settings = options.Value;
        foreach (var problem in settings.Validate())
        {
            _logger.LogError("Command history setting refused: {Problem}", problem);
        }

        _retention = settings.Retention >= RoofCommandHistoryOptions.MinimumRetention && settings.Retention <= RoofCommandHistoryOptions.MaximumRetention
            ? settings.Retention
            : TimeSpan.FromDays(RoofCommandHistoryContract.DefaultRetentionDays);
        _maximumEntries = settings.MaximumEntries is >= RoofCommandHistoryOptions.MinimumEntries and <= RoofCommandHistoryOptions.MostEntries
            ? settings.MaximumEntries
            : RoofCommandHistoryContract.DefaultMaximumEntries;
        _trustedProxies = settings.ParseTrustedProxies();

        var path = ResolvePath(settings.StorePath, identity.Value.StorePath);
        if (path is null)
        {
            _logger.LogWarning(
                "No command history file is configured ({Section}:StorePath, or an identity store next to which to keep it): " +
                "the roof command history is kept in memory and lost when the controller restarts.",
                RoofCommandHistoryOptions.SectionName);
            return;
        }

        try
        {
            var file = new RoofCommandHistoryFile(path);
            var contents = file.Load();
            _file = file;
            _fileLines = contents.Lines;
            _fileEndsWithNewline = contents.EndsWithNewline;

            // An entry changed after it was written (a count of anonymous Stops) is on a later line: the last one counts.
            var loaded = contents.Entries
                .GroupBy(entry => entry.Id)
                .Select(group => group.Last())
                .OrderBy(entry => entry.Id)
                .ToList();
            _lastId = loaded.Count == 0 ? 0 : loaded[^1].Id;
            _entries.AddRange(loaded);
            PruneLocked(_time.GetUtcNow());

            // Lines that are expired, beyond the limits, repeated or unreadable are left out of the file at the next write.
            _compact = _entries.Count != contents.Lines;
            if (contents.Unreadable > 0)
            {
                _logger.LogWarning(
                    "The command history {Path} has {Unreadable} lines that could not be read (cut short by a power cut, or " +
                    "written by a newer version); they are skipped.",
                    file.Path,
                    contents.Unreadable);
            }

            _logger.LogInformation("Command history {Path} loaded: {Entries} entries.", file.Path, _entries.Count);
        }
        catch (RoofCommandHistoryException ex)
        {
            _file = null;
            _entries.Clear();
            _lastId = 0;
            _logger.LogError(
                "Command history unavailable: {Reason} The history is kept in memory only, and the file is left as it is.",
                ex.Message);
        }
    }

    /// <summary>True when entries are written to a file and survive a restart.</summary>
    public bool IsPersistent => _file is not null;

    /// <summary>The history file, or null when the history is kept in memory only.</summary>
    public string? FilePath => _file?.Path;

    public TimeSpan Retention => _retention;

    public int MaximumEntries => _maximumEntries;

    /// <summary>The identifiers commands are recorded and matched by.</summary>
    public RoofCommandHistoryPrincipals Principals => _principals;

    /// <summary>How many entries are kept now, anonymous ones included.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                PruneLocked(_time.GetUtcNow());
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records a command's answer: <paramref name="entry"/> with the next identifier and the time now, or, for an anonymous
    /// Stop like the last from its source within <see cref="AnonymousStopWindow"/>, that entry counted once more. Returns
    /// the entry kept, or null when this kind of entry is not kept (an accepted lease renewal). Never waits for the disk.
    /// </summary>
    public RoofCommandHistoryEntry? Record(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry is { Command: RoofHistoryCommand.Lease, Accepted: true })
        {
            return null;
        }

        RoofCommandHistoryEntry stored;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneLocked(now);
            var source = IsAnonymousStop(entry) ? SourceOf(entry) : null;
            if (source is not null && TryCountAgainLocked(source, entry, now) is { } counted)
            {
                stored = counted;
            }
            else
            {
                stored = entry with { Id = ++_lastId, TimestampUtc = now, Count = 1, LastTimestampUtc = null };
                _entries.Add(stored);
                if (source is not null)
                {
                    RememberAnonymousStopLocked(source, stored.Id);
                }

                PruneLocked(now);
                QueueLocked(stored);
            }
        }

        if (_file is not null)
        {
            Wake();
        }

        return stored;
    }

    /// <summary>A page of the entries that match <paramref name="filter"/>, newest first.</summary>
    public RoofCommandHistoryPage Query(RoofCommandHistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var skip = Math.Max(0, filter.Skip);
        var take = Math.Clamp(filter.Take, 1, RoofCommandHistoryContract.MaximumTake);
        var page = new List<RoofCommandHistoryEntry>();
        var total = 0;
        lock (_gate)
        {
            PruneLocked(_time.GetUtcNow());
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (!filter.Matches(entry))
                {
                    continue;
                }

                if (total >= skip && page.Count < take)
                {
                    page.Add(entry);
                }

                total++;
            }
        }

        return new RoofCommandHistoryPage
        {
            Entries = page,
            TotalCount = total,
            Skip = skip,
            Take = take,
            AllUsers = filter.AllUsers ?? filter.PrincipalIds is null,
            RetentionDays = _retention.TotalDays,
            MaximumEntries = _maximumEntries,
            IsPersistent = IsPersistent
        };
    }

    /// <summary>
    /// Writes what is recorded and not yet written; compacts the file first when it holds too many lines that are no
    /// longer kept. Returns false when the file could not be written (the entries are kept to try again).
    /// </summary>
    internal bool Flush()
    {
        if (_file is null)
        {
            return true;
        }

        lock (_fileGate)
        {
            RoofCommandHistoryEntry[] batch;
            RoofCommandHistoryEntry[]? kept = null;
            long upTo;
            lock (_gate)
            {
                if (_pending.Count == 0 && !_compact)
                {
                    return true;
                }

                PruneLocked(_time.GetUtcNow());
                batch = _pending.Select(item => item.Entry).ToArray();
                upTo = _sequence;
                // Compacted once a quarter more lines than are kept are no longer kept (expired, beyond a limit, or an
                // earlier version of an entry since changed).
                if (_compact || _fileLines + batch.Length > _entries.Count + Math.Max(_entries.Count / 4, 50))
                {
                    // Everything queued up to upTo is in kept as it is now, or no longer kept at all.
                    kept = _entries.ToArray();
                }
            }

            try
            {
                if (kept is not null)
                {
                    _file.Replace(kept);
                    _fileLines = kept.Length;
                    _fileEndsWithNewline = true;
                }
                else
                {
                    _file.Append(batch, startNewLine: !_fileEndsWithNewline);
                    _fileLines += batch.Length;
                    _fileEndsWithNewline = true;
                }
            }
            catch (RoofCommandHistoryException ex)
            {
                if (!_writeFailing)
                {
                    _logger.LogError("The command history could not be saved: {Reason} It is kept in memory, and saving is retried.", ex.Message);
                    _writeFailing = true;
                }

                return false;
            }

            if (_writeFailing)
            {
                _logger.LogInformation("The command history is saved again: {Path}.", _file.Path);
                _writeFailing = false;
            }

            lock (_gate)
            {
                // A change queued meanwhile has a later sequence number, and is written next time.
                _pending.RemoveAll(item => item.Sequence <= upTo);
                if (kept is not null)
                {
                    _compact = false;
                }
            }

            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            // Leaves the file clean at startup when it held lines that are no longer kept.
            Wake();
            while (true)
            {
                await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(WriteDelay, _time, stoppingToken).ConfigureAwait(false);
                while (!Flush())
                {
                    await Task.Delay(RetryDelay, _time, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // What was recorded before the host stopped. The host bounds its wait, so a disk that hangs cannot hold it.
            Flush();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// The history file: <paramref name="configured"/> when set, otherwise <see cref="RoofCommandHistoryOptions.DefaultFileName"/>
    /// in the identity store's directory; null when neither is set.
    /// </summary>
    internal static string? ResolvePath(string? configured, string? identityStorePath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(identityStorePath))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(identityStorePath)) is { Length: > 0 } directory
                ? System.IO.Path.Combine(directory, RoofCommandHistoryOptions.DefaultFileName)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            // The identity store reports its own path problem.
            return null;
        }
    }

    /// <summary>
    /// The entry for a roof command sent with <paramref name="context"/>: who sent it (and their identifier), from where
    /// and with what program, and the controller's answer (<paramref name="status"/> when it carried it out,
    /// <paramref name="refusal"/> when it did not). The address is the person's when the request came from a trusted proxy
    /// (the web UI) naming one address in <c>X-Forwarded-For</c>; otherwise the header is ignored. Identifier and time are
    /// given by <see cref="Record"/>.
    /// </summary>
    public RoofCommandHistoryEntry Describe(
        HttpContext context,
        RoofHistoryCommand command,
        RoofStatusResponse? status,
        ProblemDetails? refusal)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        var name = user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } authenticated } ? authenticated : null;
        var credential = name is null ? null : RoofPrincipalFactory.GetCredentialKind(user);
        if (refusal?.Extensions.TryGetValue(RoofControllerApiContract.ProblemStatusExtension, out var attached) == true)
        {
            status ??= attached as RoofStatusResponse;
        }

        var safety = context.Items.TryGetValue(SafetyItem, out var noted) ? noted as RoofHistorySafetyNote : null;
        var connection = Normalize(context.Connection.RemoteIpAddress);
        var forwarded = connection is not null && IsTrustedProxy(connection)
            ? SingleForwardedAddress(context.Request.Headers[RoofCommandHistoryContract.ForwardedForHeaderName])
            : null;

        return new RoofCommandHistoryEntry
        {
            Command = command,
            User = name,
            PrincipalId = name is null ? null : _principals.IdOf(user),
            PrincipalKind = PrincipalKindOf(user),
            CredentialKind = credential,
            OnBehalfOf = name is null ? null : Present(user.FindFirst(RoofPrincipalFactory.OnBehalfOfClaimType)?.Value),
            Device = credential == RoofCredentialKind.Pin ? Present(user.FindFirst(RoofPrincipalFactory.DeviceClaimType)?.Value) : null,
            RemoteAddress = (forwarded ?? connection)?.ToString(),
            ForwardedBy = forwarded is null ? null : connection!.ToString(),
            Client = Printable(context.Request.Headers.UserAgent.ToString(), MaximumClientLength),
            Accepted = refusal is null,
            Code = refusal?.Extensions.TryGetValue(RoofControllerApiContract.ProblemCodeExtension, out var code) == true ? code as string : null,
            Detail = refusal is null ? null : Printable(refusal.Detail, MaximumDetailLength),
            Status = status?.Status,
            StopReason = status?.LastStopReason,
            IsFaultLatched = status?.IsFaultLatched,
            SafetyState = Printable(safety?.NotSafeState, 32),
            SafetyOverride = safety is { Overridden: true } && refusal is null ? true : null
        };
    }

    /// <summary>
    /// Notes on <paramref name="context"/> what the safety interlock (#207) decided for an Open, for the entry
    /// <see cref="Describe"/> makes of it: the conditions to record
    /// (<see cref="HVO.RoofControllerV4.RPi.Safety.RoofSafetyOpenDecision.HistorySafetyState"/>: null when they were Safe,
    /// or nothing watches them), and whether the caller overrode them. An override is recorded only on an Open the
    /// controller then accepted.
    /// </summary>
    public static void NoteSafety(HttpContext context, string? notSafeState, bool overridden)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[SafetyItem] = new RoofHistorySafetyNote(notSafeState, overridden);
    }

    /// <summary>The history's command for one of <c>RoofController</c>'s operations; null for one it does not record.</summary>
    public static RoofHistoryCommand? CommandFor(string operation) => operation switch
    {
        "open" => RoofHistoryCommand.Open,
        "close" => RoofHistoryCommand.Close,
        "stop" => RoofHistoryCommand.Stop,
        "clear_fault" => RoofHistoryCommand.ClearFault,
        "lease" => RoofHistoryCommand.Lease,
        _ => null
    };

    private static RoofHistoryPrincipalKind PrincipalKindOf(System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.Identity is not { IsAuthenticated: true, Name.Length: > 0 })
        {
            return RoofHistoryPrincipalKind.Anonymous;
        }

        return RoofPrincipalFactory.GetCredentialKind(user) is RoofCredentialKind.Session or RoofCredentialKind.Pin
            || user.HasClaim(RoofPrincipalFactory.OwnedKeyClaimType, "true")
            ? RoofHistoryPrincipalKind.Person
            : RoofHistoryPrincipalKind.ApiKey;
    }

    private bool IsTrustedProxy(IPAddress address)
        => _trustedProxies is null ? IPAddress.IsLoopback(address) : _trustedProxies.Contains(address);

    /// <summary>The one address in <c>X-Forwarded-For</c>; null for none, several (a chain this controller does not know) or a malformed one.</summary>
    private static IPAddress? SingleForwardedAddress(Microsoft.Extensions.Primitives.StringValues values)
    {
        var parts = string.Join(',', values.ToArray()).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 && IPAddress.TryParse(parts[0], out var address) ? Normalize(address) : null;
    }

    private static IPAddress? Normalize(IPAddress? address)
        => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Printable ASCII only (no control characters a terminal would act on), trimmed and cut to <paramref name="length"/>.</summary>
    private static string? Printable(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new string(value.Where(c => c is >= ' ' and <= '~').Take(length).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsAnonymousStop(RoofCommandHistoryEntry entry)
        => entry is { PrincipalKind: RoofHistoryPrincipalKind.Anonymous, Command: RoofHistoryCommand.Stop };

    /// <summary>
    /// An anonymous source, as the sign-in limit counts one (<see cref="RoofCallerAddress"/>): an IPv6 address from another
    /// network by its /64, so picking a new address for each Stop does not make each a new source.
    /// </summary>
    private static string SourceOf(RoofCommandHistoryEntry entry)
        => IPAddress.TryParse(entry.RemoteAddress, out var address) ? RoofCallerAddress.Of(address) : "unknown";

    /// <summary>The source's latest anonymous Stop, counted once more when this one is like it and soon enough after; else null.</summary>
    private RoofCommandHistoryEntry? TryCountAgainLocked(string source, RoofCommandHistoryEntry entry, DateTimeOffset now)
    {
        if (!_anonymousStops.TryGetValue(source, out var id) || IndexOfLocked(id) is not { } index)
        {
            return null;
        }

        var last = _entries[index];
        if (last.Accepted != entry.Accepted || !string.Equals(last.Code, entry.Code, StringComparison.Ordinal)
            || now - (last.LastTimestampUtc ?? last.TimestampUtc) > AnonymousStopWindow)
        {
            return null;
        }

        var counted = last with
        {
            Count = last.Count + 1,
            LastTimestampUtc = now,
            RemoteAddress = entry.RemoteAddress,
            Client = entry.Client,
            Detail = entry.Detail,
            Status = entry.Status,
            StopReason = entry.StopReason,
            IsFaultLatched = entry.IsFaultLatched
        };
        _entries[index] = counted;
        QueueLocked(counted);
        return counted;
    }

    private void RememberAnonymousStopLocked(string source, long id)
    {
        _anonymousStops[source] = id;
        if (_anonymousStops.Count > 2 * RoofCommandHistoryContract.MaximumAnonymousEntries)
        {
            // Forget the sources whose entries are gone.
            foreach (var (gone, _) in _anonymousStops.Where(pair => IndexOfLocked(pair.Value) is null).ToList())
            {
                _anonymousStops.Remove(gone);
            }
        }
    }

    private int? IndexOfLocked(long id)
    {
        var low = 0;
        var high = _entries.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var found = _entries[middle].Id;
            if (found == id)
            {
                return middle;
            }

            if (found < id)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    /// <summary>Queues an entry to be written; a newer version of one already queued takes its place.</summary>
    private void QueueLocked(RoofCommandHistoryEntry entry)
    {
        if (_file is null)
        {
            return;
        }

        var sequence = ++_sequence;
        var queued = _pending.FindLastIndex(item => item.Entry.Id == entry.Id);
        if (queued >= 0)
        {
            _pending.RemoveAt(queued);
        }

        _pending.Add((sequence, entry));
        var most = _maximumEntries + RoofCommandHistoryContract.MaximumAnonymousEntries;
        if (_pending.Count > most)
        {
            // The file cannot be written; keep no more than the history would.
            _pending.RemoveRange(0, _pending.Count - most);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The writer is already due to run.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down: the writer has written what it could.
        }
    }

    /// <summary>
    /// Drops expired entries, then the oldest anonymous entries beyond their own cap, then the oldest of the others beyond
    /// <see cref="MaximumEntries"/>.
    /// </summary>
    private void PruneLocked(DateTimeOffset now)
    {
        var expired = 0;
        var oldest = now - _retention;
        while (expired < _entries.Count && _entries[expired].TimestampUtc < oldest)
        {
            expired++;
        }

        if (expired > 0)
        {
            _entries.RemoveRange(0, expired);
        }

        var anonymous = _entries.Count(entry => entry.PrincipalKind == RoofHistoryPrincipalKind.Anonymous);
        RemoveOldest(anonymous - RoofCommandHistoryContract.MaximumAnonymousEntries, anonymous: true);
        RemoveOldest(_entries.Count - Math.Min(anonymous, RoofCommandHistoryContract.MaximumAnonymousEntries) - _maximumEntries, anonymous: false);

        void RemoveOldest(int excess, bool anonymous)
        {
            for (var i = 0; excess > 0 && i < _entries.Count;)
            {
                if ((_entries[i].PrincipalKind == RoofHistoryPrincipalKind.Anonymous) == anonymous)
                {
                    _entries.RemoveAt(i);
                    excess--;
                }
                else
                {
                    i++;
                }
            }
        }
    }
}

/// <summary>What the safety interlock decided for an Open (<see cref="RoofCommandHistory.NoteSafety"/>).</summary>
internal sealed record RoofHistorySafetyNote(string? NotSafeState, bool Overridden);

/// <summary>Registers the roof command history, the identifiers it records, and its background writer.</summary>
public static class RoofCommandHistoryServiceCollectionExtensions
{
    public static IServiceCollection AddRoofCommandHistory(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RoofCommandHistoryOptions>().Bind(configuration.GetSection(RoofCommandHistoryOptions.SectionName));
        services.AddSingleton(provider => new RoofCommandHistoryPrincipals(
            provider.GetRequiredService<RoofIdentityStore>(),
            provider.GetRequiredService<RoofApiKeyStore>()));
        services.AddSingleton(provider => new RoofCommandHistory(
            provider.GetRequiredService<IOptions<RoofCommandHistoryOptions>>(),
            provider.GetRequiredService<IOptions<RoofIdentityOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<RoofCommandHistory>>(),
            provider.GetRequiredService<RoofCommandHistoryPrincipals>()));
        services.AddHostedService(provider => provider.GetRequiredService<RoofCommandHistory>());
        return services;
    }
}
