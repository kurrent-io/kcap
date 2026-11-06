using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>What a hook does for each holder of a source: a watcher, a daemon with a fresh or stale claim on a terminal
/// or hosted session, or nobody. With no daemon claim a hook acts as it always has, so those tests also pin that the
/// daemon is never asked.</summary>
public class WatcherManagerClaimTests : IDisposable {
    const string Key = "session-1";
    const string Line = """{"type":"user","message":{"content":"Summarise the README"}}""";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    readonly FakeDaemon _daemon = new();
    readonly FakeWatcherSpawner _spawner = new();

    public void Dispose() => _server.Stop();

    WatcherPaths Paths => field ??= new(Config.CreateDir("watchers"));

    RecorderClaims Claims => field ??= new(Paths.Directory, _time);

    WatcherManager Watchers => field ??= Manager(Resolutions.At(_server.Url!, Config.Root));

    WatcherManager Manager(ProfileContext profiles) => new(
        Config.Root, profiles, new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, _spawner, _time, _daemon) { DrainCommits = new CommitObservation.Uncovered() };

    string NewTranscript() => Config.CreateFile("t.jsonl", $"{Line}\n");

    void GoStale() => _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

    void StubServer() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{Key}/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"last_line_number": -1}"""));

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    int Posts => _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost()).Count;

    /// <summary>A real process behind a pid file, so a signal would show in whether it is still alive.</summary>
    async Task<DummyProcess> ProcessBehindPidFile() {
        var process = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(Paths.PidFile(Key), $"{process.Pid}\n{PidIdentity.Capture(process.Pid)}");

        return process;
    }

    [Test]
    public async Task Watcher_held_source_stops_and_drains_as_today() {
        StubServer();
        using var watcher = await ProcessBehindPidFile();

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsTrue();
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(File.Exists(Paths.PidFile(Key))).IsFalse();

        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(Posts).IsEqualTo(1);
        await Assert.That(_daemon.Asked).IsEmpty();
    }

    /// <summary>Neither answer lets a hook drain: an unstamped drain that wins would make the server discard the
    /// daemon's stamped copy.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_fresh_daemon_claim_signals_nothing_and_asks_the_daemon_to_finish(bool answer) {
        StubServer();
        _daemon.Answer = answer;
        Claims.TryTake(Key, "daemon", isHosted: false);
        using var process = await ProcessBehindPidFile();
        var identity = PidIdentity.Capture(process.Pid);

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsFalse();
        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(PidIdentity.IsGone(process.Pid, identity)).IsFalse();
        await Assert.That(_daemon.Asked).IsEquivalentTo([Key]);
        await Assert.That(Posts).IsEqualTo(0);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    /// <summary>A subagent's source is its own: finishing it leaves its parent's alone.</summary>
    [Test]
    public async Task A_fresh_claim_on_a_subagent_finishes_only_that_source() {
        StubServer();
        Claims.TryTake("session-1-agent-1", "daemon", isHosted: false);

        await Watchers.InlineDrainAsync("session-1", NewTranscript(), agentId: "agent-1");

        await Assert.That(_daemon.Asked).IsEquivalentTo(["session-1-agent-1"]);
        await Assert.That(Posts).IsEqualTo(0);
    }

    /// <summary>The daemon still finishes: the hook only asked. What runs out is the hook's own wait, and a client
    /// that ignores its token can't take that from it.</summary>
    [Test]
    public async Task A_daemon_that_outlasts_the_budget_lets_the_hook_go_without_a_drain() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: false);
        _daemon.Hangs = true;

        var drain = Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        _time.Advance(TimeSpan.FromSeconds(5));
        await drain.WithinDeadlineAsync("the hook's budget to run out");

        await Assert.That(_daemon.Asked).IsEquivalentTo([Key]);
        await Assert.That(Posts).IsEqualTo(0);
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    [Test]
    public async Task A_daemon_client_that_throws_does_not_fail_the_hook() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: false);
        _daemon.Throws = true;

        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(_daemon.Asked).IsEquivalentTo([Key]);
        await Assert.That(Posts).IsEqualTo(0);
    }

    [Test]
    public async Task A_stale_claim_on_a_terminal_session_is_taken_and_drained() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: false);
        GoStale();
        using var process = await ProcessBehindPidFile();
        var identity = PidIdentity.Capture(process.Pid);

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsFalse();
        await Assert.That(PidIdentity.IsGone(process.Pid, identity)).IsFalse();
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);

        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(Posts).IsEqualTo(1);
        await Assert.That(_daemon.Asked).IsEmpty();
    }

    /// <summary>A hook that can't post can't drain, so the claim stays for the daemon rather than end up with nobody.</summary>
    [Test]
    public async Task A_hook_with_no_server_to_post_to_leaves_a_stale_claim_alone() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: false);
        GoStale();

        await Manager(Resolutions.None(Config.Root)).InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
        await Assert.That(Posts).IsEqualTo(0);
    }

    /// <summary>Another hook holding the spawn lock is already taking this claim, or spawning over it.</summary>
    [Test]
    public async Task A_hook_that_loses_the_spawn_lock_leaves_the_drain_to_the_winner() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: false);
        GoStale();

        using (new FileStream(Paths.SpawnLockFile(Key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
        await Assert.That(Posts).IsEqualTo(0);
    }

    [Test]
    public async Task A_stale_claim_on_a_hosted_session_does_nothing() {
        StubServer();
        Claims.TryTake(Key, "daemon", isHosted: true);
        GoStale();
        var claim    = Claims.ClaimFile(Key);
        var content  = await File.ReadAllTextAsync(claim);
        var lastBeat = File.GetLastWriteTimeUtc(claim);

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsFalse();
        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(_daemon.Asked).IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(claim)).IsEqualTo(content);
        await Assert.That(File.GetLastWriteTimeUtc(claim)).IsEqualTo(lastBeat);
    }

    [Test]
    public async Task An_unclaimed_source_drains_as_today() {
        StubServer();

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsFalse();
        await Watchers.InlineDrainAsync(Key, NewTranscript(), agentId: null);

        await Assert.That(Posts).IsEqualTo(1);
        await Assert.That(_daemon.Asked).IsEmpty();
    }

    /// <summary>The worst case: a pid file naming the daemon's own pid, under a key the daemon holds. A claim says who
    /// records the source, and <c>KillWatcher</c> reads nothing else first, so the process can't be reached.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task The_claim_never_touches_the_pid_file(bool stale) {
        using var daemon = DummyProcess.StartSleep(30);
        var identity = PidIdentity.Capture(daemon.Pid);

        Claims.TryTake(Key, "daemon", isHosted: false);
        Claims.Beat(Key);
        await Assert.That(File.Exists(Paths.PidFile(Key))).IsFalse();

        var pidFile = $"{daemon.Pid}\n{identity}";
        await File.WriteAllTextAsync(Paths.PidFile(Key), pidFile);
        if (stale) GoStale();

        await Assert.That(await Watchers.KillWatcher(Key, TextWriter.Null)).IsFalse();
        await Assert.That(await Watchers.TryStopWatcher(Key, TextWriter.Null)).IsFalse();

        await Assert.That(PidIdentity.IsGone(daemon.Pid, identity)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Paths.PidFile(Key))).IsEqualTo(pidFile);
    }

    [Test]
    public async Task Cleanup_sweeps_a_watchers_files_and_leaves_a_claim() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        Watchers.PurgeAuxiliaryFiles();

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    [Test]
    public async Task EnsureWatcherRunning_spawns_nothing_for_a_fresh_claim() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        await Watchers.EnsureWatcherRunning(Key, NewTranscript(), agentId: "child");

        await Assert.That(_spawner.Spawned).IsEmpty();
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A hosted session on a watcher would go unstamped for the rest of its life, and the daemon's catch-up
    /// resumes it anyway.</summary>
    [Test]
    public async Task EnsureWatcherRunning_spawns_nothing_for_a_stale_hosted_claim() {
        Claims.TryTake(Key, "daemon", isHosted: true);
        GoStale();

        await Watchers.EnsureWatcherRunning(Key, NewTranscript(), agentId: "child");

        await Assert.That(_spawner.Spawned).IsEmpty();
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
    }

    /// <summary>A daemon gone past the grace hands a terminal session to a watcher, and it stays there.</summary>
    [Test]
    public async Task EnsureWatcherRunning_takes_a_stale_terminal_claim_and_spawns() {
        Claims.TryTake(Key, "daemon", isHosted: false);
        GoStale();

        await Watchers.EnsureWatcherRunning(Key, NewTranscript(), agentId: "child");

        await Assert.That(_spawner.Keys).IsEquivalentTo([Key]);
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task EnsureWatcherRunning_spawns_as_today_with_no_claim() {
        await Watchers.EnsureWatcherRunning(Key, NewTranscript(), agentId: "child");

        await Assert.That(_spawner.Keys).IsEquivalentTo([Key]);
    }

    /// <summary>A laptop sleeps, a hook starts a watcher on wake, and the daemon's recorder ticks after it. Nothing it
    /// holds may reach the server from then on: not the rest of the loop, and not a final drain beside the watcher.</summary>
    [Test]
    public async Task A_recorder_that_lost_its_claim_stops_sending() {
        var path      = NewTranscript();
        var source    = new RecordingSource(Key, "child", path, "claude", null);
        var transport = new FakeRecorderTransport { IsConnected = true };
        var server    = new CapacitorServer("http://127.0.0.1:1", Config.Root, Resolutions.None(Config.Root));
        var context   = new RecorderContext(server, new FakeRecorderHousekeeping(), _time, _ => { }, () => { });
        Claims.TryTake(source.Key, "daemon", isHosted: false);
        using var recorder = new TranscriptRecorder(source, transport, new NoStamps(), _time, context, Claims);

        var run = recorder.RunAsync(CancellationToken.None);
        await Ticking(() => transport.Batches.Count > 0, "the recorder's first line");

        GoStale();
        await Watchers.EnsureWatcherRunning(source.Key, path, agentId: "child");
        await Assert.That(_spawner.Keys).IsEquivalentTo([source.Key]);

        await Ticking(() => run.IsCompleted, "the recorder to stop at its next tick");
        await run;

        var sent = transport.Batches.Count;
        await File.AppendAllTextAsync(path, $"{Line}\n");
        await recorder.FinishAsync(CancellationToken.None);

        await Assert.That(recorder.LostClaim).IsTrue();
        await Assert.That(transport.Batches.Count).IsEqualTo(sent);
        await Assert.That(transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>Moves the clock until <paramref name="condition"/> holds, so a recorder's ticks come round.</summary>
    async Task Ticking(Func<bool> condition, string what) {
        var deadline = DateTimeOffset.UtcNow + BoundedWait.Deadline;

        while (!condition()) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Never saw: {what}");

            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
    }

    sealed class FakeDaemon : IDaemonRecordingClient {
        readonly List<string> _asked = [];

        public bool Answer { get; set; }

        public bool Hangs { get; set; }

        public bool Throws { get; set; }

        public IReadOnlyList<string> Asked {
            get {
                lock (_asked) return [.._asked];
            }
        }

        public async Task<bool> FinishSourceAsync(string key, CancellationToken ct) {
            lock (_asked) _asked.Add(key);

            if (Throws) throw new InvalidOperationException("The daemon went away.");
            if (Hangs) await new TaskCompletionSource().Task;

            return Answer;
        }
    }
}
