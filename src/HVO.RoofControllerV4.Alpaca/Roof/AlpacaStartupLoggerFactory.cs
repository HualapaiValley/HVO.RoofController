using System.Net.Sockets;

namespace HVO.RoofControllerV4.Alpaca.Roof;

/// <summary>
/// Keeps expected status-hub connection failures at Debug while the controller starts alongside Alpaca. After 30 s,
/// or the first successful connection, the underlying logging levels apply again. It does not own the host's factory.
/// </summary>
internal sealed class AlpacaStartupLoggerFactory(ILoggerFactory inner, TimeProvider time) : ILoggerFactory
{
    private readonly long _started = time.GetTimestamp();
    private int _connected;

    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        return categoryName.StartsWith("Microsoft.AspNetCore.Http.Connections.Client.", StringComparison.Ordinal)
            || categoryName.StartsWith("Microsoft.AspNetCore.SignalR.Client.", StringComparison.Ordinal)
            ? new StartupLogger(logger, this)
            : logger;
    }

    public void ControllerConnected() => Interlocked.Exchange(ref _connected, 1);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() { }

    private bool InGrace => Volatile.Read(ref _connected) == 0 && time.GetElapsedTime(_started) < TimeSpan.FromSeconds(30);

    private sealed class StartupLogger(ILogger inner, AlpacaStartupLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        // Let Log select the effective level: a host may enable Debug while disabling Error, or vice versa.
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel)
            || (owner.InGrace && logLevel is LogLevel.Warning or LogLevel.Error && inner.IsEnabled(LogLevel.Debug));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = owner.InGrace && logLevel is LogLevel.Warning or LogLevel.Error
                && exception is HttpRequestException { StatusCode: null } or SocketException or TimeoutException
                ? LogLevel.Debug : logLevel;
            if (inner.IsEnabled(level))
            {
                inner.Log(level, eventId, state, exception, formatter);
            }
        }
    }
}
