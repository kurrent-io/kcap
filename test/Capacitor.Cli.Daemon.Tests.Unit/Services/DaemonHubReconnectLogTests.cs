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

    /// <summary>SignalR logs a live connection's drop as an error with its stack trace while the hub still reads as
    /// connected, so the hub's state alone cannot hold it back; the policy's own line is what says the connection
    /// was lost.</summary>
    [Test]
    public async Task A_dropped_live_connection_logs_one_warning_and_no_client_error() {
        await using var host = await RecordingHubHost.StartAsync();
        var loggers = new Recording(LogLevel.Trace);
        var config  = new DaemonConfig { Name = "test", ServerUrl = host.Url, ConfigRoot = Config.Root, Profiles = Resolutions.None(Config.Root) };

        await using var hub = DaemonHub.Build(config, () => Task.FromResult<string?>(null), _ => { }, loggers, TimeProvider.System);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Reconnected += _ => { reconnected.TrySetResult(); return Task.CompletedTask; };

        await hub.StartAsync();
        await host.WaitForAsync(h => h.OpenConnections > 0, "the server admitting the hub");
        host.BreakTransports();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var entries = loggers.Entries;
        var client = entries.Where(e => e.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && e.Level > LogLevel.Debug).ToList();
        await Assert.That(client.Where(e => e.Level >= LogLevel.Warning || e.Exception is not null)).IsEmpty();
        await Assert.That(entries.Count(e => e.Message.StartsWith("Hub connection lost", StringComparison.Ordinal))).IsEqualTo(1);
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

    /// <summary>The transport logs a failed send before the hub leaves the connected state; the policy reports that drop.</summary>
    [Test]
    public async Task A_failed_send_while_still_connected_is_quiet() {
        var loggers = new Recording(LogLevel.Trace);
        var logger  = new ReconnectQuietLoggerFactory(loggers, () => false).CreateLogger("Microsoft.AspNetCore.Http.Connections.Client.Internal.WebSocketsTransport");

        logger.Log(LogLevel.Error, new EventId(15, "ErrorSendingMessage"), "Error while sending a message.", new IOException("broken pipe"), (s, _) => s);

        await Assert.That(loggers.Entries.Single().Level).IsEqualTo(LogLevel.Debug);
    }

    /// <summary>The receive loop runs before the hub reads as connected, so a binding fault in that gap must keep its level.</summary>
    [Test]
    public async Task A_binding_failure_keeps_its_level_before_the_hub_reads_as_connected() {
        var loggers = new Recording(LogLevel.Trace);
        var logger  = new ReconnectQuietLoggerFactory(loggers, () => true).CreateLogger("Microsoft.AspNetCore.SignalR.Client.HubConnection");

        logger.Log(LogLevel.Error, new EventId(57, "ArgumentBindingFailure"), "Failed to bind arguments.", new FormatException(), (s, _) => s);

        await Assert.That(loggers.Entries.Single().Level).IsEqualTo(LogLevel.Error);
    }

    sealed class FixedRetry(TimeSpan delay) : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => delay;
    }
}
