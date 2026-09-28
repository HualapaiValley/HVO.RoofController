using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _serious = new();
    private long _count;

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

    /// <summary>Every entry at Warning or above, including any the capacity dropped from <see cref="Entries"/>.</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Serious => _serious.ToArray();

    /// <summary>How many entries were logged, including any the capacity dropped.</summary>
    public long Count => Interlocked.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose()
    {
    }

    private void Record(string category, LogLevel level, string message)
    {
        var entry = (category, level, message);
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

    private sealed class Logger(string category, RecordingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => provider.Record(category, logLevel, formatter(state, exception));
    }
}
