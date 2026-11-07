using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A hosted session on a watcher goes unstamped for the rest of its life, so a hook hands it to the daemon when
/// the daemon will have it, and only then does not spawn. Every other answer spawns as today: a watcher costs stamps, and
/// no recorder costs the transcript.</summary>
public class WatcherManagerHostedRecordingTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    readonly FakeDaemon _daemon = new();
    readonly FakeWatcherSpawner _spawner = new();

    WatcherPaths Paths => field ??= new(Config.CreateDir("watchers"));

    RecorderClaims Claims => field ??= new(Paths.Directory, _time);

    WatcherManager Watchers => field ??= new(
        Config.Root, Resolutions.At("http://127.0.0.1:1", Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, _spawner, _time, _daemon);

    [Test]
    public async Task A_hosted_hook_spawns_no_watcher_on_a_daemon_with_recording() {
        _daemon.Answer = true;

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null, cwd: "/repo", skipTitle: true);

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(_daemon.Asked).IsEquivalentTo([(new RecordingSource(Session, null, "/t.jsonl", "claude", "/repo"), true)]);
    }

    /// <summary>An older daemon answers no, as does one below the server's floor or with recording off.</summary>
    [Test]
    public async Task A_hosted_hook_spawns_a_watcher_on_an_older_daemon() {
        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
    }

    [Test]
    public async Task A_subagent_goes_to_the_daemon_as_its_own_source() {
        _daemon.Answer = true;
        var key = $"{Session}-agent-1";

        await Watchers.EnsureWatcherRunning(key, "/agent.jsonl", "agent-1", Session);

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(_daemon.Asked.Single().Source.Key).IsEqualTo(key);
    }

    /// <summary>The budget is the hook's own: a daemon that keeps it waiting has still been asked, and takes the spawn
    /// lock and the claim's check behind whatever the hook starts.</summary>
    [Test]
    public async Task A_daemon_that_outlasts_the_budget_gets_a_watcher_beside_it() {
        _daemon.Hangs = true;

        var hook = Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);
        await _daemon.Reached.Task.WithinDeadlineAsync("the hook to ask the daemon");
        _time.Advance(TimeSpan.FromSeconds(5));
        await hook.WithinDeadlineAsync("the hook's way past the daemon");

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
    }

    [Test]
    public async Task A_daemon_that_fails_gets_a_watcher_beside_it() {
        _daemon.Throws = true;

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
    }

    /// <summary>A claim already answers the question, on every hook of the session.</summary>
    [Test]
    public async Task A_claimed_source_asks_the_daemon_nothing() {
        Claims.TryTake(Session, "daemon", isHosted: true);

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_daemon.Asked).IsEmpty();
        await Assert.That(_spawner.Keys).IsEmpty();
    }

    sealed class FakeDaemon : IDaemonRecordingClient {
        readonly List<(RecordingSource Source, bool SkipTitle)> _asked = [];

        public bool Answer { get; set; }

        public bool Hangs { get; set; }

        public bool Throws { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(RecordingSource Source, bool SkipTitle)> Asked {
            get {
                lock (_asked) return [.._asked];
            }
        }

        public async Task<bool> TryRecordAsync(RecordingSource source, bool skipTitle, CancellationToken ct) {
            lock (_asked) _asked.Add((source, skipTitle));
            Reached.TrySetResult();

            if (Throws) throw new InvalidOperationException("The daemon went away.");
            if (Hangs) await new TaskCompletionSource().Task;

            return Answer;
        }

        public Task<bool> FinishSourceAsync(string key, CancellationToken ct) => Task.FromResult(false);
    }
}
