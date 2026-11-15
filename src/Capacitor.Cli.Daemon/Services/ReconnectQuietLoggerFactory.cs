using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The logger factory a hub's own client logs through. While the hub is not connected, SignalR's client logs every
/// failed negotiate and start as an error with its stack trace, several per attempt; those go to Debug here, and
/// <see cref="LoggedRetryPolicy"/> says what happened in one line. Once connected, its lines pass as they are.
/// </summary>
sealed class ReconnectQuietLoggerFactory(ILoggerFactory inner, Func<bool> reconnecting) : ILoggerFactory {
    const string ClientPrefix = "Microsoft.AspNetCore.";

    public ILogger CreateLogger(string categoryName) {
        var logger = inner.CreateLogger(categoryName);

        return categoryName.StartsWith(ClientPrefix, StringComparison.Ordinal) ? new Quiet(logger, reconnecting) : logger;
    }

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() { }

    sealed class Quiet(ILogger inner, Func<bool> reconnecting) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            var level = logLevel is > LogLevel.Debug and < LogLevel.Critical && reconnecting() ? LogLevel.Debug : logLevel;
            if (inner.IsEnabled(level)) inner.Log(level, eventId, state, exception, formatter);
        }
    }
}
