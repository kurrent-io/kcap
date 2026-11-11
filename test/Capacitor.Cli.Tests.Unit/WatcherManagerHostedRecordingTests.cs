using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
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
        Config.Root, Resolutions.At("http://127.0.0.1:1", Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, _spawner, _time, _daemon,
        new HostedAgent("agent-1", false, DaemonBridge.None));

    [Test]
    public async Task A_hosted_hook_spawns_no_watcher_on_a_daemon_with_recording() {
        _daemon.Answer = RecordingHandOver.Taken;

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null, cwd: "/repo", skipTitle: true);

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(_daemon.Asked).IsEquivalentTo([(new RecordingSource(Session, null, "/t.jsonl", "claude", "/repo"), true)]);
    }

    /// <summary>An older daemon answers no, as does one whose server can't stop a source or with recording off.</summary>
    [Test]
    public async Task A_hosted_hook_spawns_a_watcher_on_an_older_daemon() {
        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
    }

    [Test]
    public async Task A_subagent_goes_to_the_daemon_as_its_own_source() {
        _daemon.Answer = RecordingHandOver.Taken;
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

    /// <summary>An orphan: nothing beats it and no list entry brings it back, so a daemon that is up has to be told.</summary>
    [Test]
    public async Task A_stale_claim_goes_to_the_daemon_hosting_the_agent() {
        _daemon.Answer = RecordingHandOver.Taken;
        Claims.TryTake(Session, "daemon", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_daemon.Asked.Count).IsEqualTo(1);
        await Assert.That(_spawner.Keys).IsEmpty();
    }

    /// <summary>The daemon is up and said no, so no catch-up is coming, and waiting for one leaves the session with no
    /// recorder. The hook's capture gate has already passed by here, and the daemon has no gate of its own to refuse on.</summary>
    [Test]
    public async Task A_stale_claim_a_reachable_daemon_declines_is_taken_and_watched() {
        _daemon.Answer = RecordingHandOver.Declined;
        Claims.TryTake(Session, "daemon", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>The source isn't this agent's, so the claim on its key can be another agent's, whose catch-up is still owed.</summary>
    [Test]
    public async Task A_stale_claim_on_a_source_the_daemon_disowns_is_left_alone() {
        _daemon.Answer = RecordingHandOver.Disowned;
        Claims.TryTake(Session, "daemon", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.DaemonStale);
    }

    /// <summary>A daemon restarting answers nothing, and its catch-up is on the way.</summary>
    [Test]
    public async Task A_stale_claim_with_no_answer_waits_for_the_catch_up() {
        _daemon.Answer = RecordingHandOver.Unanswered;
        Claims.TryTake(Session, "daemon", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.DaemonStale);
    }

    /// <summary>With no claim, a source the daemon disowns gets a watcher like any other it won't take.</summary>
    [Test]
    public async Task A_disowned_source_with_no_claim_gets_a_watcher() {
        _daemon.Answer = RecordingHandOver.Disowned;

        await Watchers.EnsureWatcherRunning(Session, "/t.jsonl", agentId: null);

        await Assert.That(_spawner.Keys).IsEquivalentTo([Session]);
    }

    sealed class FakeDaemon : IDaemonRecordingClient {
        readonly List<(RecordingSource Source, bool SkipTitle)> _asked = [];

        public RecordingHandOver Answer { get; set; } = RecordingHandOver.Declined;

        public bool Hangs { get; set; }

        public bool Throws { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(RecordingSource Source, bool SkipTitle)> Asked {
            get {
                lock (_asked) return [.._asked];
            }
        }

        public IReadOnlyList<string> Daemons() => [];

        public Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) => Task.FromResult(new RecordingDisableAnswer(RecordingDisable.NoSocket));

        public async Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, int? follows, CancellationToken ct) {
            lock (_asked) _asked.Add((source, skipTitle));
            Reached.TrySetResult();

            if (Throws) throw new InvalidOperationException("The daemon went away.");
            if (Hangs) await new TaskCompletionSource().Task;

            return Answer;
        }

        public Task<RecordingFinish> FinishSourceAsync(string key, string? holder, ProcessIncarnation? from, CancellationToken ct) => Task.FromResult(RecordingFinish.NotHeld);
    }
}
