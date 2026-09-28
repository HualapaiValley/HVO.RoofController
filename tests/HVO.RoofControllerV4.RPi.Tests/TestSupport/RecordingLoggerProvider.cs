using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Records every log entry of every category, for tests that assert what a host logged. With a
/// <paramref name="capacity"/> (a soak), <see cref="Entries"/> keeps only the latest entries, and
/// <see cref="Serious"/> keeps every Warning or above, so a long run does not grow the heap it measures.
/// </summary>
internal sealed class RecordingLoggerProvider(int? capacity = null) : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> _entries = new();
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> _serious = new();
    private long _count;

    public IReadOnlyList<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

    /// <summary>Every entry at Warning or above, including any the capacity dropped from <see cref="Entries"/>.</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Message, Exception? Exception)> Serious => _serious.ToArray();

    /// <summary>How many entries were logged, including any the capacity dropped.</summary>
    public long Count => Interlocked.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    /// <summary>
    /// The log as text, for a result file or the test's output: every Warning or above, then the latest entries, each
    /// followed by its exception.
    /// </summary>
    public IEnumerable<string> Describe()
    {
        var serious = Serious;
        var latest = Entries;
        return new[] { $"Every Warning or above ({serious.Count}):" }
            .Concat(serious.Select(Format))
            .Append(string.Empty)
            .Append($"The latest {latest.Count} of {Count} entries:")
            .Concat(latest.Select(Format));
    }

    public void Dispose()
    {
    }

    private void Record(string category, LogLevel level, string message, Exception? exception)
    {
        var entry = (category, level, message, exception);
        Interlocked.Increment(ref _count);
        _entries.Enqueue(entry);
        if (level >= LogLevel.Warning)
        {
            _serious.Enqueue(entry);
        }

        while (capacity is { } limit && _entries.Count > limit && _entries.TryDequeue(out _))
        {
        }
    }

    private static string Format((string Category, LogLevel Level, string Message, Exception? Exception) entry)
        => entry.Exception is null
            ? $"{entry.Level} {entry.Category}: {entry.Message}"
            : $"{entry.Level} {entry.Category}: {entry.Message}{Environment.NewLine}{entry.Exception}";

    private sealed class Logger(string category, RecordingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => provider.Record(category, logLevel, formatter(state, exception), exception);
    }
}
