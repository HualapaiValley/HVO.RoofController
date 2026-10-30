using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>Whose commands a history request reads: one caller's own, or (null) everyone's.</summary>
/// <param name="Kind">A person, or an API key that is not a person's own.</param>
/// <param name="Name">The person or the key's name.</param>
public sealed record RoofCommandHistoryOwner(RoofHistoryPrincipalKind Kind, string Name)
{
    /// <summary>
    /// True for the owner's own commands: sent as them, or, for a person, by an API key whose holder said it acted for
    /// them (the web UI's Stop key, say).
    /// </summary>
    public bool Owns(RoofCommandHistoryEntry entry)
        => (entry.PrincipalKind == Kind && RoofCommandHistory.SameName(entry.User, Name))
            || (Kind == RoofHistoryPrincipalKind.Person && RoofCommandHistory.SameName(entry.OnBehalfOf, Name));
}

/// <summary>A request for a page of the history, newest first.</summary>
public sealed record RoofCommandHistoryFilter
{
    /// <summary>Only this caller's own commands; null for everyone's (an admin's request).</summary>
    public RoofCommandHistoryOwner? Owner { get; init; }

    /// <summary>Only commands sent by, or on behalf of, this name (case-insensitive).</summary>
    public string? User { get; init; }

    /// <summary>Only commands at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only commands before this time.</summary>
    public DateTimeOffset? To { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = RoofCommandHistoryContract.DefaultTake;

    internal bool Matches(RoofCommandHistoryEntry entry)
        => (Owner is null || Owner.Owns(entry))
            && (string.IsNullOrEmpty(User) || RoofCommandHistory.SameName(entry.User, User) || RoofCommandHistory.SameName(entry.OnBehalfOf, User))
            && (From is not { } from || entry.TimestampUtc >= from)
            && (To is not { } to || entry.TimestampUtc < to);
}

/// <summary>
/// The roof command history (#206): every Open, Close, Stop and Clear fault the API was sent, accepted or refused, and
/// refused lease renewals, with who sent it, from where, and the roof's status afterwards. Kept in memory and in the
/// history file (<see cref="RoofCommandHistoryOptions.StorePath"/>), for <see cref="RoofCommandHistoryOptions.Retention"/>
/// and up to <see cref="RoofCommandHistoryOptions.MaximumEntries"/> entries.
/// </summary>
/// <remarks>
/// Recording never waits for the disk and never throws for it: an entry is kept in memory at once, and this hosted
/// service appends it to the file in the background, so a slow or failing disk never delays a command's answer (a
/// Stop's above all). A file that cannot be read leaves the history in memory only, and the file as it is.
/// </remarks>
public sealed class RoofCommandHistory : BackgroundService
{
    /// <summary>How soon a write that failed is tried again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private const int MaximumDetailLength = 200;
    private const int MaximumClientLength = 100;

    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TimeProvider _time;
    private readonly ILogger<RoofCommandHistory> _logger;
    private readonly RoofCommandHistoryFile? _file;
    private readonly TimeSpan _retention;
    private readonly int _maximumEntries;

    // Oldest first. Guarded by _gate, as are _pending, _lastId and _compact.
    private readonly List<RoofCommandHistoryEntry> _entries = [];

    // Recorded and not yet written to the file.
    private readonly List<RoofCommandHistoryEntry> _pending = [];
    private long _lastId;
    private bool _compact;

    // Guarded by _fileGate: only the writer touches the file.
    private int _fileLines;
    private bool _fileEndsWithNewline = true;
    private bool _writeFailing;

    public RoofCommandHistory(
        IOptions<RoofCommandHistoryOptions> options,
        IOptions<RoofIdentityOptions> identity,
        TimeProvider time,
        ILogger<RoofCommandHistory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            var loaded = contents.Entries
                .GroupBy(entry => entry.Id)
                .Select(group => group.Last())
                .OrderBy(entry => entry.Id)
                .ToList();
            _lastId = loaded.Count == 0 ? 0 : loaded[^1].Id;
            _entries.AddRange(loaded);
            PruneLocked(_time.GetUtcNow());

            // Lines that are expired, beyond the limit or unreadable are left out of the file at the next write.
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

    /// <summary>How many entries are kept now.</summary>
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
    /// Records a command's answer: <paramref name="entry"/> with the next identifier and the time now. Returns the
    /// entry kept, or null when this kind of entry is not kept (an accepted lease renewal). Never waits for the disk.
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
            stored = entry with { Id = ++_lastId, TimestampUtc = now };
            _entries.Add(stored);
            PruneLocked(now);
            if (_file is not null)
            {
                _pending.Add(stored);
                if (_pending.Count > _maximumEntries)
                {
                    // The file cannot be written; keep no more than the history would.
                    _pending.RemoveRange(0, _pending.Count - _maximumEntries);
                }
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
            AllUsers = filter.Owner is null,
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
                batch = _pending.ToArray();
                upTo = _lastId;
                if (_compact || _fileLines + batch.Length > _maximumEntries + Math.Max(_maximumEntries / 4, 50))
                {
                    // Everything up to upTo is in kept, or no longer kept at all.
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
                _pending.RemoveAll(entry => entry.Id <= upTo);
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
    /// The entry for a roof command sent with <paramref name="context"/>: who sent it, from where and with what program,
    /// and the controller's answer (<paramref name="status"/> when it carried it out, <paramref name="refusal"/> when it
    /// did not). Identifier and time are given by <see cref="Record"/>.
    /// </summary>
    public static RoofCommandHistoryEntry Describe(
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

        return new RoofCommandHistoryEntry
        {
            Command = command,
            User = name,
            PrincipalKind = PrincipalKindOf(user),
            CredentialKind = credential,
            OnBehalfOf = name is null ? null : Present(user.FindFirst(RoofPrincipalFactory.OnBehalfOfClaimType)?.Value),
            Device = credential == RoofCredentialKind.Pin ? Present(user.FindFirst(RoofPrincipalFactory.DeviceClaimType)?.Value) : null,
            RemoteAddress = Address(context.Connection.RemoteIpAddress),
            Client = Printable(context.Request.Headers.UserAgent.ToString(), MaximumClientLength),
            Accepted = refusal is null,
            Code = refusal?.Extensions.TryGetValue(RoofControllerApiContract.ProblemCodeExtension, out var code) == true ? code as string : null,
            Detail = refusal is null ? null : Printable(refusal.Detail, MaximumDetailLength),
            Status = status?.Status,
            StopReason = status?.LastStopReason,
            IsFaultLatched = status?.IsFaultLatched
        };
    }

    /// <summary>The history <paramref name="user"/> may read as their own; null when they are not signed in.</summary>
    public static RoofCommandHistoryOwner? OwnerOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? new RoofCommandHistoryOwner(PrincipalKindOf(user), name)
            : null;
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

    internal static bool SameName(string? left, string? right)
        => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static RoofHistoryPrincipalKind PrincipalKindOf(ClaimsPrincipal user)
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

    private static string? Address(IPAddress? address)
        => address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

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

    private void PruneLocked(DateTimeOffset now)
    {
        var expired = 0;
        var oldest = now - _retention;
        while (expired < _entries.Count && _entries[expired].TimestampUtc < oldest)
        {
            expired++;
        }

        var excess = Math.Max(0, _entries.Count - expired - _maximumEntries);
        if (expired + excess > 0)
        {
            _entries.RemoveRange(0, expired + excess);
        }
    }
}

/// <summary>Registers the roof command history and its background writer.</summary>
public static class RoofCommandHistoryServiceCollectionExtensions
{
    public static IServiceCollection AddRoofCommandHistory(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RoofCommandHistoryOptions>().Bind(configuration.GetSection(RoofCommandHistoryOptions.SectionName));
        services.AddSingleton<RoofCommandHistory>();
        services.AddHostedService(provider => provider.GetRequiredService<RoofCommandHistory>());
        return services;
    }
}
