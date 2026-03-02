using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace HVO.RoofControllerV4.RPi.Logging;

public sealed class ConsoleLogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConsoleLogBuffer _buffer;
    private IExternalScopeProvider? _scopeProvider;

    public ConsoleLogLoggerProvider(ConsoleLogBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) => new ConsoleLogLogger(categoryName, _buffer, () => _scopeProvider);

    public void Dispose()
    {
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    private sealed class ConsoleLogLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConsoleLogBuffer _buffer;
        private readonly Func<IExternalScopeProvider?> _scopeProviderAccessor;

        public ConsoleLogLogger(string categoryName, ConsoleLogBuffer buffer, Func<IExternalScopeProvider?> scopeProviderAccessor)
        {
            _categoryName = categoryName;
            _buffer = buffer;
            _scopeProviderAccessor = scopeProviderAccessor;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var scopeProvider = _scopeProviderAccessor();
            return scopeProvider?.Push(state) ?? NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            var options = _buffer.Options;
            if (options.MinimumLevel == LogLevel.None)
            {
                return false;
            }

            return logLevel >= options.MinimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            if (formatter is null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var scopes = CaptureScopes();
            var entry = new ConsoleLogEntry(DateTimeOffset.UtcNow, logLevel, _categoryName, message.Trim(), exception?.ToString(), scopes);
            _buffer.Append(entry);
        }

        private IReadOnlyList<string> CaptureScopes()
        {
            var scopeProvider = _scopeProviderAccessor();
            if (scopeProvider is null)
            {
                return Array.Empty<string>();
            }

            var scopes = new List<string>();
            scopeProvider.ForEachScope<object?>((scope, _) =>
            {
                if (scope is null)
                {
                    return;
                }

                var text = scope.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    scopes.Add(text!);
                }
            }, null);

            return scopes;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
