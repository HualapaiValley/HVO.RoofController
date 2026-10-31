using System.Globalization;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>The observatory's safety conditions, as the controller judges them (docs/safety-service.md).</summary>
public interface IRoofSafetyService
{
    /// <summary>
    /// The safety state now, judged from the readings the service holds against the clock, so a reading that has
    /// grown too old counts as Unknown at once. Never waits on a source.
    /// </summary>
    RoofSafetyStatus GetCurrent();
}

/// <summary>
/// Reads every <see cref="ITelescopeParkSource"/> and <see cref="IWeatherSafetySource"/> (the registered ones, then the
/// configured ASCOM Alpaca devices, <see cref="RoofSafetyConfiguredSources"/>) once per
/// <see cref="RoofSafetyOptions.PollInterval"/>, each read bounded by <see cref="RoofSafetyOptions.ReadTimeout"/>, and
/// combines the readings into Safe, Unsafe or Unknown with reasons. Every change of the state or its reasons is logged.
/// </summary>
/// <remarks>
/// <para>The conditions are Safe only when at least one weather source is required and every source that counts (each
/// required weather source, each telescope that can block the roof) has a recent reading that says safe: the weather
/// safe, the telescope clear of the roof (<see cref="TelescopeRoofClearance"/>). A weather source set Ignored, and a
/// telescope that cannot block the roof, are judged and shown the same way but never decide the conditions. The weather
/// alone is judged too (<see cref="RoofSafetyStatus.WeatherState"/>), for the controller's Alpaca SafetyMonitor.</para>
/// <para>A source's reading counts until it is older than its <see cref="IRoofSafetySource.MaxReadingAge"/> (or
/// <see cref="RoofSafetyOptions.MaxReadingAge"/>); after that the source is Unknown, whatever it said. A failed read
/// keeps the last reading, which goes stale in its turn. Ages are measured on the monotonic clock, so a wall-clock
/// step neither revives an old reading nor ages a new one.</para>
/// <para>A read runs on the thread pool, so an adapter that blocks holds up only itself; a source whose last read has
/// not ended is not read again until it does. Nothing here reaches the roof: the interlock
/// (<see cref="RoofSafetyInterlock"/>) reads <see cref="GetCurrent"/> when Open is requested.</para>
/// </remarks>
public sealed class RoofSafetyService : BackgroundService, IRoofSafetyService
{
    /// <summary>Longest detail kept from a reading, so a source cannot fill the status snapshot.</summary>
    internal const int MaxDetailLength = 200;

    private readonly Source[] _sources;
    private readonly IOptionsMonitor<RoofSafetyOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RoofSafetyService> _logger;
    private readonly object _gate = new();
    private RoofSafetyOptions? _lastGoodOptions;
    private bool _optionsRefused;
    private RoofSafetyState? _loggedState;
    private string? _loggedReasons;
    private DateTimeOffset? _stateSince;

    /// <param name="telescopes">The mounts registered as sources (the tests' simulated ones), read first.</param>
    /// <param name="weather">The weather sources registered, read after the mounts.</param>
    /// <param name="options">The <c>RoofSafety</c> settings.</param>
    /// <param name="time">The clock; the system's by default.</param>
    /// <param name="logger">The log.</param>
    /// <param name="configured">
    /// The ASCOM Alpaca devices the settings configure: each mount after the registered ones, each weather source after
    /// the registered weather sources.
    /// </param>
    public RoofSafetyService(
        IEnumerable<ITelescopeParkSource> telescopes,
        IEnumerable<IWeatherSafetySource> weather,
        IOptionsMonitor<RoofSafetyOptions> options,
        TimeProvider? time = null,
        ILogger<RoofSafetyService>? logger = null,
        RoofSafetyConfiguredSources? configured = null)
    {
        ArgumentNullException.ThrowIfNull(telescopes);
        ArgumentNullException.ThrowIfNull(weather);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RoofSafetyService>.Instance;
        telescopes = configured is null ? telescopes : [.. telescopes, .. configured.Telescopes];
        weather = configured is null ? weather : [.. weather, .. configured.WeatherSources];
        _sources =
        [
            .. telescopes.Select(source => new Source(source, RoofSafetySourceKind.TelescopePark, source.BlocksRoof, async token =>
            {
                var reading = await source.ReadParkStateAsync(token).ConfigureAwait(false);
                var (clear, evidence) = TelescopeRoofClearance.Judge(reading);
                return (clear, evidence, reading.ObservedUtc, reading.Detail);
            })),
            .. weather.Select(source => new Source(source, RoofSafetySourceKind.Weather, source.Requirement == RoofWeatherSourceRequirement.Required, async token =>
            {
                var reading = await source.ReadWeatherSafetyAsync(token).ConfigureAwait(false);
                return (reading.IsSafe, reading.IsSafe ? "safe" : "unsafe", reading.ObservedUtc, reading.Detail);
            }))
        ];
    }

    /// <inheritdoc />
    public RoofSafetyStatus GetCurrent()
    {
        var options = Options();
        RoofSafetySourceStatus[] sources;
        lock (_gate)
        {
            sources = _sources.Select(source => Judge(source, options)).ToArray();
        }

        var counted = sources.Where(source => source.Counts).ToArray();
        var weather = counted.Where(source => source.Kind == RoofSafetySourceKind.Weather).ToArray();
        var (state, reasons) = sources.Length == 0
            ? (RoofSafetyState.Unknown, new[] { RoofSafetyStatus.NoSourcesReason })
            : Combine(counted, hasRequiredWeather: weather.Length > 0);
        var (weatherState, weatherReasons) = Combine(weather, hasRequiredWeather: weather.Length > 0);

        return new RoofSafetyStatus
        {
            State = state,
            Reasons = reasons,
            WeatherState = weatherState,
            WeatherReasons = weatherReasons,
            OwnAlpacaUniqueIds = [.. options.OwnAlpacaUniqueIds ?? []],
            Sources = sources,
            StateSinceUtc = NoteTransition(state, reasons, options.InterlockMode, sources.Length == 0),
            InterlockMode = options.InterlockMode,
            OverrideRole = options.InterlockMode == RoofSafetyInterlockMode.Warn
                ? options.OverrideRole == RoofSafetyOverrideRole.Operator ? RoofControllerApiContract.OperatorRole : RoofControllerApiContract.AdminRole
                : null
        };
    }

    /// <summary>
    /// The state of the sources that count: Unsafe when any says unsafe; otherwise Unknown when none is a required weather
    /// source or any is unknown; otherwise Safe. The reasons are the unsafe ones', then the unknown ones', then
    /// <see cref="RoofSafetyStatus.NoRequiredWeatherSourceReason"/> when it applies; none when Safe.
    /// </summary>
    private static (RoofSafetyState State, string[] Reasons) Combine(IReadOnlyCollection<RoofSafetySourceStatus> counted, bool hasRequiredWeather)
    {
        var state = counted.Any(source => source.State == RoofSafetyState.Unsafe) ? RoofSafetyState.Unsafe
            : !hasRequiredWeather || counted.Any(source => source.State == RoofSafetyState.Unknown) ? RoofSafetyState.Unknown
            : RoofSafetyState.Safe;
        if (state == RoofSafetyState.Safe)
        {
            return (state, []);
        }

        return (state,
        [
            .. counted.Where(source => source.State == RoofSafetyState.Unsafe).Select(source => source.Reason),
            .. counted.Where(source => source.State == RoofSafetyState.Unknown).Select(source => source.Reason),
            .. hasRequiredWeather ? Array.Empty<string>() : new[] { RoofSafetyStatus.NoRequiredWeatherSourceReason }
        ]);
    }

    /// <summary>Reads every source once (each bounded by the read timeout), then judges and logs the result.</summary>
    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var timeout = Options().ReadTimeout;
        await Task.WhenAll(_sources.Select(source => ReadAsync(source, timeout, cancellationToken))).ConfigureAwait(false);
        GetCurrent();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = Options();
        if (_sources.Length == 0 && options.InterlockMode != RoofSafetyInterlockMode.Off)
        {
            _logger.LogWarning(
                "Safety interlock is {InterlockMode} with no safety sources configured: the conditions stay Unknown, so Open {Effect} until sources are configured.",
                options.InterlockMode,
                options.InterlockMode == RoofSafetyInterlockMode.Block ? "is refused" : "needs an override");
        }
        else if (_sources.Length > 0 && !_sources.Any(source => source.Counts && source.Kind == RoofSafetySourceKind.Weather))
        {
            _logger.LogWarning(
                "No safety source is a required weather source: the conditions stay Unknown, so Open {Effect} until one is configured (interlock {InterlockMode}).",
                options.InterlockMode switch
                {
                    RoofSafetyInterlockMode.Block => "is refused",
                    RoofSafetyInterlockMode.Warn => "needs an override",
                    _ => "is only reported as not safe"
                },
                options.InterlockMode);
        }

        var longestGap = options.PollInterval + options.ReadTimeout;
        foreach (var source in _sources.Where(source => source.Inner.MaxReadingAge is { } age && age > TimeSpan.Zero && age <= longestGap))
        {
            _logger.LogWarning(
                "Safety source {Kind} '{Source}' allows readings only {MaxReadingAge} old, no longer than the {LongestGap} between two reads (poll interval and read timeout): it can go Unknown between reads.",
                source.Kind, source.Name, source.Inner.MaxReadingAge, longestGap);
        }

        foreach (var duplicate in _sources.GroupBy(source => (source.Kind, source.Name)).Where(group => group.Count() > 1))
        {
            _logger.LogWarning("Two or more safety sources are named {Kind} '{Source}'; their reasons cannot be told apart.", duplicate.Key.Kind, duplicate.Key.Name);
        }

        // Logs the starting state: no sources, or not read yet.
        GetCurrent();
        if (_sources.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Reading {Count} safety source(s) every {PollInterval} (read timeout {ReadTimeout}): {Sources}.",
            _sources.Length, options.PollInterval, options.ReadTimeout,
            string.Join(", ", _sources.Select(source => $"{source.Kind} '{source.Name}'" + (source.Counts ? string.Empty : $" ({DescribeNotCounted(source.Kind)})"))));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Options().PollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Not expected: every read's failure is caught where it happens. The loop keeps polling, and the readings
                // it holds go stale meanwhile, so nothing is left reading Safe on old data.
                _logger.LogError(ex, "Safety source poll failed unexpectedly.");
                await Task.Delay(Options().PollInterval, _time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>
    /// The options now. Settings that fail validation stop the start (<c>ValidateOnStart</c>); should a reload bring
    /// unusable ones, the last good ones stay in force, so the status and the interlock never fail for them.
    /// </summary>
    private RoofSafetyOptions Options()
    {
        try
        {
            var options = _options.CurrentValue;
            lock (_gate)
            {
                _lastGoodOptions = options;
                _optionsRefused = false;
            }

            return options;
        }
        catch (Exception ex)
        {
            bool first;
            RoofSafetyOptions options;
            lock (_gate)
            {
                first = !_optionsRefused;
                _optionsRefused = true;
                options = _lastGoodOptions ?? new RoofSafetyOptions();
            }

            if (first)
            {
                _logger.LogError(
                    "The {Section} settings cannot be used ({Failure}); the last good ones stay in force (interlock {InterlockMode}).",
                    RoofSafetyOptions.SectionName, ex.GetType().Name, options.InterlockMode);
            }

            return options;
        }
    }

    private async Task ReadAsync(Source source, TimeSpan timeout, CancellationToken stopping)
    {
        bool stillReading, firstFailure = false;
        lock (_gate)
        {
            stillReading = source.Pending is { IsCompleted: false };
            if (stillReading)
            {
                // The last read has not ended, though its timeout cancelled it: another would only pile up behind it.
                RecordFailure_NoLock(source, "the last read has not finished", shown: true, out firstFailure);
            }
        }

        if (stillReading)
        {
            if (firstFailure)
            {
                LogFailure(source, "the last read has not finished", null);
            }

            return;
        }

        var timeoutSource = new CancellationTokenSource(timeout, _time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeoutSource.Token);
        var read = Task.Run(() => source.Read(linked.Token), CancellationToken.None);
        lock (_gate)
        {
            source.Pending = read;
        }

        try
        {
            var (safe, said, observedUtc, detail) = await read.WaitAsync(linked.Token).ConfigureAwait(false);
            RecordReading(source, safe, said, observedUtc, detail);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout, and an adapter's own words (RoofSafetySourceException), are fit to show in the reason; any other
            // failure is logged by its type alone, since its message could carry the device's address.
            var timedOut = timeoutSource.IsCancellationRequested;
            var failure = timedOut ? string.Create(CultureInfo.InvariantCulture, $"no answer within {timeout.TotalSeconds:0.#} s")
                : ex is RoofSafetySourceException described ? Bounded(described.Message)
                : ex.GetType().Name;
            bool first;
            lock (_gate)
            {
                RecordFailure_NoLock(source, failure, shown: timedOut || ex is RoofSafetySourceException, out first);
            }

            if (first)
            {
                LogFailure(source, failure, ex);
            }
        }
        finally
        {
            // A read that ignores its cancellation still holds the token: release it once the read ends.
            if (read.IsCompleted)
            {
                linked.Dispose();
                timeoutSource.Dispose();
            }
            else
            {
                _ = read.ContinueWith(
                    _ =>
                    {
                        linked.Dispose();
                        timeoutSource.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private void RecordReading(Source source, bool safe, string said, DateTimeOffset? observedUtc, string? detail)
    {
        var now = _time.GetUtcNow();
        var timestamp = _time.GetTimestamp();
        // An observation the device dates earlier makes the reading that much older; one dated later counts as now.
        var lag = observedUtc is { } observed && observed < now ? now - observed : TimeSpan.Zero;
        int failures;
        lock (_gate)
        {
            failures = source.ConsecutiveFailures;
            source.LastSafe = safe;
            source.LastSaid = said;
            source.LastReadTimestamp = timestamp;
            source.ObservedLag = lag;
            source.LastReadingUtc = now - lag;
            source.Detail = string.IsNullOrWhiteSpace(detail) ? null : Bounded(detail);
            source.ConsecutiveFailures = 0;
            source.Failure = null;
            source.ShownFailure = null;
        }

        if (failures > 0)
        {
            _logger.LogInformation("Safety source {Kind} '{Source}' read again after {Failures} failed read(s).", source.Kind, source.Name, failures);
        }
    }

    /// <summary><paramref name="text"/> cut to <see cref="MaxDetailLength"/>, so a source cannot fill the status snapshot.</summary>
    private static string Bounded(string text) => text.Length <= MaxDetailLength ? text : text[..MaxDetailLength];

    private static void RecordFailure_NoLock(Source source, string failure, bool shown, out bool first)
    {
        source.ConsecutiveFailures++;
        source.Failure = failure;
        source.ShownFailure = shown ? failure : null;
        first = source.ConsecutiveFailures == 1;
    }

    private void LogFailure(Source source, string failure, Exception? error)
    {
        // The type or the timeout only: an adapter's message can carry its device's address.
        _logger.LogWarning(
            "Safety source {Kind} '{Source}' could not be read ({Failure}); its last reading counts until it is too old.",
            source.Kind, source.Name, failure);
        if (error is not null)
        {
            _logger.LogDebug(error, "Safety source {Kind} '{Source}' read failure.", source.Kind, source.Name);
        }
    }

    private RoofSafetySourceStatus Judge(Source source, RoofSafetyOptions options)
    {
        var maxAge = source.Inner.MaxReadingAge is { } own && own > TimeSpan.Zero ? own : options.MaxReadingAge;
        var label = source.Kind == RoofSafetySourceKind.TelescopePark ? $"Telescope '{source.Name}'" : $"Weather monitor '{source.Name}'";
        var status = new RoofSafetySourceStatus
        {
            Name = source.Name,
            Kind = source.Kind,
            Detail = source.Detail,
            LastReadingUtc = source.LastReadingUtc,
            MaxReadingAgeSeconds = maxAge.TotalSeconds,
            Counts = source.Counts
        };

        // Why the last read failed, when the words are fit to show: "no answer within 5 s", "the device is not connected".
        var why = source.ShownFailure is { } shown ? $" ({shown})" : string.Empty;
        if (source.LastSafe is not { } safe || source.LastReadTimestamp is not { } readAt)
        {
            return status with
            {
                State = RoofSafetyState.Unknown,
                Reason = source.Failure is null ? $"{label} has not been read yet" : $"{label} cannot be read{why}"
            };
        }

        var said = source.LastSaid;
        if (_time.GetElapsedTime(readAt) + source.ObservedLag > maxAge)
        {
            return status with
            {
                State = RoofSafetyState.Unknown,
                IsStale = true,
                Reason = string.Create(CultureInfo.InvariantCulture, $"{label}: the last reading ({said}) is older than {maxAge.TotalSeconds:0.#} s")
                    + (source.ShownFailure is { } failed ? $"; the last read failed ({failed})" : string.Empty)
            };
        }

        return status with
        {
            State = safe ? RoofSafetyState.Safe : RoofSafetyState.Unsafe,
            Reason = source.Kind == RoofSafetySourceKind.TelescopePark
                ? $"{label} is {said}"
                : $"{label} reports {said} weather"
        };
    }

    /// <summary>Records the state and reasons, logs a change, and returns when the state last changed.</summary>
    private DateTimeOffset NoteTransition(RoofSafetyState state, IReadOnlyList<string> reasons, RoofSafetyInterlockMode mode, bool noSources)
    {
        var key = string.Join("; ", reasons);
        RoofSafetyState? previous;
        DateTimeOffset since;
        lock (_gate)
        {
            if (_loggedState == state && _loggedReasons == key)
            {
                return _stateSince!.Value;
            }

            previous = _loggedState;
            if (previous != state)
            {
                _stateSince = _time.GetUtcNow();
            }

            _loggedState = state;
            _loggedReasons = key;
            since = _stateSince!.Value;
        }

        var level = state == RoofSafetyState.Safe || (state == RoofSafetyState.Unknown && (noSources || previous is null))
            ? LogLevel.Information
            : LogLevel.Warning;
        if (previous == state)
        {
            _logger.Log(level, "Safety conditions still {State}, now because: {Reasons} (interlock {InterlockMode}).", state, key, mode);
        }
        else if (state == RoofSafetyState.Safe)
        {
            _logger.Log(level, "Safety conditions {State} (was {Previous}; interlock {InterlockMode}).", state, previous?.ToString() ?? "not judged", mode);
        }
        else
        {
            _logger.Log(level, "Safety conditions {State} (was {Previous}; interlock {InterlockMode}): {Reasons}.", state, previous?.ToString() ?? "not judged", mode, key);
        }

        return since;
    }

    /// <summary>Why a source does not count, as the start log says it.</summary>
    private static string DescribeNotCounted(RoofSafetySourceKind kind)
        => kind == RoofSafetySourceKind.TelescopePark ? "does not block the roof" : "ignored";

    /// <summary>One source and what the service holds of it. Its mutable fields are guarded by the service's gate.</summary>
    private sealed class Source(
        IRoofSafetySource inner,
        RoofSafetySourceKind kind,
        bool counts,
        Func<CancellationToken, Task<(bool Safe, string Said, DateTimeOffset? ObservedUtc, string? Detail)>> read)
    {
        public IRoofSafetySource Inner { get; } = inner;

        public RoofSafetySourceKind Kind { get; } = kind;

        /// <summary>True when the source decides the conditions: a required weather source, a telescope that can block the roof.</summary>
        public bool Counts { get; } = counts;

        public string Name { get; } = string.IsNullOrWhiteSpace(inner.Name) ? "unnamed" : inner.Name;

        public Func<CancellationToken, Task<(bool Safe, string Said, DateTimeOffset? ObservedUtc, string? Detail)>> Read { get; } = read;

        public Task? Pending { get; set; }

        public bool? LastSafe { get; set; }

        /// <summary>What the last reading said, as the reasons quote it: "parked", "not parked", "safe", "unsafe".</summary>
        public string LastSaid { get; set; } = string.Empty;

        /// <summary>Monotonic timestamp of the last successful read.</summary>
        public long? LastReadTimestamp { get; set; }

        /// <summary>How much older than its read the device said the last reading was.</summary>
        public TimeSpan ObservedLag { get; set; }

        public DateTimeOffset? LastReadingUtc { get; set; }

        public string? Detail { get; set; }

        /// <summary>Why the last read failed, as logged (the adapter's words, the timeout, or the exception's type).</summary>
        public string? Failure { get; set; }

        /// <summary>Why the last read failed in words fit to show in the reason; null when only its type is known.</summary>
        public string? ShownFailure { get; set; }

        public int ConsecutiveFailures { get; set; }
    }
}

/// <summary>
/// Whether a telescope is clear of the roof's path, from what its source reports: the one place that decides it, so the
/// rest of the safety service asks "is this scope clear of the roof?", never "is it parked?". Parked is the only proof
/// today. Later evidence (a camera that sees where the mounts point, a safe zone in right ascension and declination) adds
/// its own reading and its own words here.
/// </summary>
internal static class TelescopeRoofClearance
{
    /// <summary>Clear or not, and the evidence in the words the reasons quote ("parked", "not parked").</summary>
    public static (bool Clear, string Evidence) Judge(TelescopeParkReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.IsParked ? (true, "parked") : (false, "not parked");
    }
}
