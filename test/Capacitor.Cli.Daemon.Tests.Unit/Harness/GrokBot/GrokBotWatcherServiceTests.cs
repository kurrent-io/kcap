using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.GrokBot;
using Capacitor.Cli.Recording.Harness.GrokBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.GrokBot;

/// <summary>Pins when the daemon runs the Grok Bot watcher: only while the profile asks for it, following
/// the setting both ways without a restart, and never letting a watcher fault escape.</summary>
public class GrokBotWatcherServiceTests {
    /// <summary>A watcher that runs until cancelled, counting its starts and stops.</summary>
    sealed class FakeWatcher : IDisposable {
        readonly SemaphoreSlim _started = new(0);
        readonly SemaphoreSlim _stopped = new(0);

        public int Starts;

        public Func<int, GrokBotWatchOutcome?> EndsAt { get; init; } = _ => null;

        public async Task<GrokBotWatchOutcome> RunAsync(Action<string> _, CancellationToken ct) {
            var start = Interlocked.Increment(ref Starts);
            _started.Release();

            if (EndsAt(start) is { } outcome) return outcome;

            try {
                await Task.Delay(Timeout.Infinite, ct);
            } finally {
                _stopped.Release();
            }

            return GrokBotWatchOutcome.Stopped;
        }

        public Task<bool> StartedAsync() => _started.WaitAsync(TimeSpan.FromSeconds(10));
        public Task<bool> StoppedAsync() => _stopped.WaitAsync(TimeSpan.FromSeconds(10));

        public void Dispose() {
            _started.Dispose();
            _stopped.Dispose();
        }
    }

    /// <summary>Rechecks at once, so the service's waits do not set the pace of a test.</summary>
    sealed class NoWait : TimeProvider {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromMilliseconds(5), period);
    }

    static GrokBotWatcherService Service(Func<bool?> record, FakeWatcher watcher) =>
        new(() => Task.FromResult(record()), watcher.RunAsync, new NoWait(), NullLogger<GrokBotWatcherService>.Instance);

    [Test]
    public async Task The_watcher_runs_while_the_profile_asks_for_it_and_stops_when_it_no_longer_does() {
        bool? record  = true;
        using var watcher = new FakeWatcher();
        using var service = Service(() => record, watcher);

        await service.StartAsync(CancellationToken.None);
        await Assert.That(await watcher.StartedAsync()).IsTrue();

        record = false;
        await Assert.That(await watcher.StoppedAsync()).IsTrue();

        record = true;
        await Assert.That(await watcher.StartedAsync()).IsTrue();
        await Assert.That(watcher.Starts).IsEqualTo(2);

        await service.StopAsync(CancellationToken.None);
        await Assert.That(await watcher.StoppedAsync()).IsTrue();
    }

    [Test]
    public async Task A_profile_that_never_asked_starts_no_watcher() {
        var checks  = 0;
        using var watcher = new FakeWatcher();
        using var service = Service(() => { Interlocked.Increment(ref checks); return false; }, watcher);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref checks) >= 3);
        await service.StopAsync(CancellationToken.None);

        await Assert.That(watcher.Starts).IsEqualTo(0);
    }

    [Test]
    public async Task A_config_that_cannot_be_read_keeps_the_watcher_it_already_started() {
        bool? record  = true;
        var   checks  = 0;
        using var watcher = new FakeWatcher();
        using var service = Service(() => { Interlocked.Increment(ref checks); return record; }, watcher);

        await service.StartAsync(CancellationToken.None);
        await Assert.That(await watcher.StartedAsync()).IsTrue();

        record = null;
        var seen = Volatile.Read(ref checks);
        await WaitUntilAsync(() => Volatile.Read(ref checks) >= seen + 3);

        await Assert.That(watcher.Starts).IsEqualTo(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task A_watcher_that_throws_or_ends_is_started_again_and_never_takes_the_service_down() {
        using var watcher = new FakeWatcher {
            EndsAt = start => start switch {
                1 => throw new InvalidOperationException("boom"),
                2 => GrokBotWatchOutcome.AlreadyRunning,
                _ => null
            }
        };
        using var service = Service(() => true, watcher);

        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++) await Assert.That(await watcher.StartedAsync()).IsTrue();

        await Assert.That(service.ExecuteTask!.IsFaulted).IsFalse();
        await service.StopAsync(CancellationToken.None);
        await Assert.That(service.ExecuteTask.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>An unreadable config keeps the last answer read, and that answer may be "off": it must not
    /// fall back to the "on" that came before it.</summary>
    [Test]
    public async Task An_off_answer_outlives_a_config_that_can_no_longer_be_read() {
        bool? record  = true;
        var   checks  = 0;
        using var watcher = new FakeWatcher();
        using var service = Service(() => { Interlocked.Increment(ref checks); return record; }, watcher);

        await service.StartAsync(CancellationToken.None);
        await Assert.That(await watcher.StartedAsync()).IsTrue();

        record = false;
        await Assert.That(await watcher.StoppedAsync()).IsTrue();

        record = null;
        var seen = Volatile.Read(ref checks);
        await WaitUntilAsync(() => Volatile.Read(ref checks) >= seen + 4);

        await Assert.That(watcher.Starts).IsEqualTo(1);
        await service.StopAsync(CancellationToken.None);
    }

    static Profile Recording(string serverUrl, bool? record = true) => new() { ServerUrl = serverUrl, GrokBot = new GrokBotSettings { Record = record } };

    static ProfileConfig Config(Profile profile) => new() { Profiles = new() { ["default"] = profile } };

    [Test]
    public async Task A_profile_records_only_for_the_server_the_daemon_started_with() {
        var own = Resolutions.Of(Recording("https://acme.kcap.ai"));

        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Ok, Config(Recording("https://acme.kcap.ai/")), own) == true).IsTrue();
        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Ok, Config(Recording("https://acme.kcap.ai", record: false)), own) == false).IsTrue();
        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Ok, Config(Recording("https://acme.kcap.ai", record: null)), own) == false).IsTrue();
        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Ok, Config(Recording("https://other.kcap.ai")), own) == false).IsTrue();
        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Ok, new ProfileConfig(), own) == false).IsTrue();
        await Assert.That(GrokBotWatcherService.Records(ProfileConfigLoad.Unreadable, Config(Recording("https://acme.kcap.ai")), own)).IsNull();
    }

    static async Task WaitUntilAsync(Func<bool> condition) {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!condition()) await Task.Delay(5, limit.Token);
    }
}
