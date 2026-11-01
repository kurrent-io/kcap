using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class StartupWatchTests {
    sealed class CaptureLogger : ILogger {
        public readonly List<string> Messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }

    /// A boot that stalls names the step it stalled in at every interval, and a boot that finished says nothing more.
    [Test]
    public async Task Warns_each_interval_with_the_current_step_until_disposed() {
        var time   = new FakeTimeProvider();
        var logger = new CaptureLogger();
        using var watch = new StartupWatch(logger, time, "finding vendor CLIs", TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(30));
        watch.Entering("connecting to the server");
        time.Advance(TimeSpan.FromSeconds(30));
        watch.Dispose();
        time.Advance(TimeSpan.FromSeconds(60));

        await Assert.That(logger.Messages).IsEquivalentTo([
            "Startup has not finished 30s after the host was built; it is finding vendor CLIs",
            "Startup has not finished 60s after the host was built; it is connecting to the server",
        ], CollectionOrdering.Matching);
    }
}
