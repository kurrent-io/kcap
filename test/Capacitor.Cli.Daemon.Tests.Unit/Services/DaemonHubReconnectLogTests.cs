using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class DaemonHubReconnectLogTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    sealed class Recording(LogLevel minimum) : ILoggerFactory {
        readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries { get { lock (_entries) return [.. _entries]; } }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        sealed class Logger(Recording owner, string category) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.Minimum;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (!IsEnabled(logLevel)) return;
                lock (owner._entries) owner._entries.Add(new(category, logLevel, formatter(state, exception), exception));
            }
        }

        LogLevel Minimum { get; } = minimum;
    }

    /// <summary>A server that is down fails every negotiate, and SignalR's client logs each failure as an error with
    /// its stack trace; nothing of that may reach a daemon log at its default level.</summary>
    [Test]
    public async Task A_hub_that_cannot_connect_logs_nothing_from_the_client_above_debug() {
        var loggers = new Recording(LogLevel.Trace);
        var config  = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root, Profiles = Resolutions.None(Config.Root) };

        await using var hub = DaemonHub.Build(config, () => Task.FromResult<string?>(null), _ => { }, loggers, TimeProvider.System);

        await Assert.That(async () => await hub.StartAsync()).ThrowsException();

        var client = loggers.Entries.Where(e => e.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)).ToList();
        await Assert.That(client).IsNotEmpty();
        await Assert.That(client.Where(e => e.Level > LogLevel.Debug)).IsEmpty();
    }

    [Test]
    public async Task Each_attempt_is_one_warning_naming_its_reason_and_no_stack_trace() {
        var loggers = new Recording(LogLevel.Information);
        var policy  = new LoggedRetryPolicy(new FixedRetry(TimeSpan.FromSeconds(2)), loggers.CreateLogger("hub"));
        var refused = new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable).");

        policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 0, RetryReason = new IOException("The remote party closed the WebSocket.") });
        policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 1, RetryReason = refused });

        var entries = loggers.Entries;
        await Assert.That(entries.Select(e => e.Level)).IsEquivalentTo([LogLevel.Warning, LogLevel.Warning]);
        await Assert.That(entries.All(e => e.Exception is null)).IsTrue();
        await Assert.That(entries[0].Message).IsEqualTo("Hub connection lost (The remote party closed the WebSocket.); reconnecting in 2s");
        await Assert.That(entries[1].Message).IsEqualTo("Reconnect attempt 1 failed (Response status code does not indicate success: 503 (Service Unavailable).); retrying in 2s");
    }

    sealed class FixedRetry(TimeSpan delay) : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => delay;
    }
}
