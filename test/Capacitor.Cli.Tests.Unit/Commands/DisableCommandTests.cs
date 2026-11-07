using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap disable</c> tells a person recording has stopped and their data is gone, so what it prints is held to
/// what the daemon and the server actually said.</summary>
public class DisableCommandTests : IDisposable {
    const string Session  = "0123456789abcdef0123456789abcdef";
    const string Subagent = $"{Session}-sub-1";
    const string Holder   = "daemon-a";

    const string Clean = $"Session {Session} disabled. Recording stopped and server data deleted.";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    readonly FakeDaemon _daemon = new();
    readonly FakeWatcherSpawner _spawner = new();
    readonly FakeSessions _sessions = new();
    readonly StringWriter _out = new();
    readonly StringWriter _err = new();

    public void Dispose() {
        _out.Dispose();
        _err.Dispose();
    }

    WatcherPaths Paths => field ??= new(Config.CreateDir("watchers"));

    RecorderClaims Claims => field ??= new(Paths.Directory, _time);

    WatcherManager Manager(TimeProvider time) => new(
        Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, _spawner, time, _daemon, HostedAgent.Terminal);

    Task<int> Run(TimeProvider? time = null) => new DisableCommand(Config.Root, Manager(time ?? _time)).Handle(Session, () => _sessions, _out, _err);

    string Printed => _out.ToString().Trim();

    void Claim(string key, string holder = Holder) => Claims.TryTake(key, holder, isHosted: true);

    /// <summary>A real process behind a pid file, so a signal shows in whether it is still alive.</summary>
    async Task<DummyProcess> WatcherOn(string key) {
        var process = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(Paths.PidFile(key), $"{process.Pid}\n{PidIdentity.Capture(process.Pid)}");

        return process;
    }

    /// <summary>With no claim there is nothing a daemon could add: a watcher and a subagent's watcher stop, the session is
    /// marked, the server is told, and no daemon is so much as asked. On the real clock, because the watcher's stop waits
    /// for it on a platform with no signal to send.</summary>
    [Test]
    [ParallelLimiter<SubprocessLimit>]
    public async Task Disable_with_no_daemon_behaves_as_today() {
        using var watcher  = await WatcherOn(Session);
        using var subagent = await WatcherOn(Subagent);

        await Assert.That(await Run(TimeProvider.System)).IsEqualTo(0);

        await Assert.That(watcher.HasExited).IsTrue();
        await Assert.That(subagent.HasExited).IsTrue();
        await Assert.That(DisabledSessions.IsDisabled(Session, Config.Root)).IsTrue();
        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
        await Assert.That(_daemon.Asked).IsEmpty();
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    [Test]
    public async Task Disable_with_nothing_found_on_the_server_says_so() {
        _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo($"Session {Session} disabled. No server data found (may have already been deleted).");
    }

    [Test]
    public async Task Disable_with_no_server_is_no_failure() {
        _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo("Session disabled locally (watcher stopped, hooks silenced). Server data not deleted.");
    }

    [Test]
    public async Task Disable_refused_by_the_server_fails() {
        _sessions.Failure = new CapacitorApiException(403, "Forbidden.");

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(_err.ToString()).Contains("Forbidden.");
        await Assert.That(Printed).IsEmpty();
    }

    /// <summary>A hook or a launch racing the command reads the marker, so it has to be down before the daemon is told to
    /// let go, and the server's deletion comes after the stop or a send could land behind it.</summary>
    [Test]
    public async Task Disable_marks_first_and_deletes_last() {
        Claim(Session);
        _daemon.Answer = RecordingDisable.Stopped;
        bool? markedWhenAsked = null;
        bool? askedWhenDeleted = null;
        _daemon.OnAsked = () => markedWhenAsked = DisabledSessions.IsDisabled(Session, Config.Root);
        _sessions.OnDelete = () => askedWhenDeleted = _daemon.Asked.Count > 0;

        await Run();

        await Assert.That(markedWhenAsked).IsTrue();
        await Assert.That(askedWhenDeleted).IsTrue();
    }

    /// <summary>The terminal has no daemon named in its environment: the claim says which one holds the source.</summary>
    [Test]
    public async Task Disable_asks_the_daemon_the_claim_names() {
        Claim(Session);
        _daemon.Answer = RecordingDisable.Stopped;

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(_daemon.Asked).IsEquivalentTo([(Holder, Session)]);
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A pid scan never sees a daemon's child: the claim file is all it leaves here. The parent's source may have
    /// ended long ago, so the child's claim alone has to bring the daemon in.</summary>
    [Test]
    public async Task Disable_finds_a_daemon_held_subagent() {
        Claim(Subagent);
        _daemon.Answer = RecordingDisable.Stopped;

        await Run();

        await Assert.That(_daemon.Asked).IsEquivalentTo([(Holder, Session)]);
        await Assert.That(Claims.Read(Subagent)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task Disable_asks_each_daemon_once() {
        Claim(Session);
        Claim(Subagent);
        Claim($"{Session}-sub-2", "daemon-b");
        _daemon.Answer = RecordingDisable.Stopped;

        await Run();

        await Assert.That(_daemon.Asked).IsEquivalentTo([(Holder, Session), ("daemon-b", Session)]);
    }

    /// <summary>No claim of another session is touched, a longer id with this one as its prefix included.</summary>
    [Test]
    public async Task Disable_leaves_another_sessions_claim_alone() {
        const string other = "fedcba9876543210fedcba9876543210";
        Claim(other);
        Claim($"{other}-sub-1");

        await Run();

        await Assert.That(Claims.Read(other)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.Read($"{other}-sub-1")).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(_daemon.Asked).IsEmpty();
    }

    /// <summary>A claim going is what stops a recorder at its next tick, so it goes whatever the daemon said: an older or
    /// a hung daemon still ends up stopping.</summary>
    [Test]
    [Arguments(RecordingDisable.Stopped)]
    [Arguments(RecordingDisable.SendInFlight)]
    [Arguments(RecordingDisable.NotHeld)]
    [Arguments(RecordingDisable.Unanswered)]
    public async Task Disable_lets_go_of_the_claim_whatever_the_daemon_said(RecordingDisable answer) {
        Claim(Session);
        _daemon.Answer = answer;

        await Run();

        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A claim it holds no recorder behind: a restart that hasn't got to it, which the marker now refuses.</summary>
    [Test]
    public async Task A_daemon_holding_none_of_it_is_a_clean_stop() {
        Claim(Session);
        _daemon.Answer = RecordingDisable.NotHeld;

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A batch the server hadn't answered can land behind the deletion, and "server data deleted" alone would be a
    /// promise nobody can keep.</summary>
    [Test]
    public async Task Disable_says_so_when_a_send_may_still_land() {
        Claim(Session);
        _daemon.Answer = RecordingDisable.SendInFlight;

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Recording stopped and server data deleted.");
        await Assert.That(Printed).Contains("may still reach the server");
        await Assert.That(Printed).Contains($"kcap disable {Session}");
    }

    /// <summary>Nothing here knows recording stopped, so nothing printed may say it did.</summary>
    [Test]
    public async Task Disable_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).DoesNotContain("Recording stopped");
        await Assert.That(Printed).Contains("did not confirm");
        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
    }

    /// <summary>The bound holds with a daemon that takes the frame and goes quiet, and the command still deletes.</summary>
    [Test]
    public async Task Disable_gives_up_on_a_daemon_that_hangs() {
        Claim(Session);
        _daemon.Hangs = true;

        var run = Run();
        await _daemon.Reached.Task.WithinDeadlineAsync("the frame");
        _time.Advance(WatcherManager.DisableBudget);

        await Assert.That(await run.WithinDeadlineAsync("the command")).IsEqualTo(1);
        await Assert.That(Printed).Contains("did not confirm");
    }

    [Test]
    public async Task Disable_without_a_server_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);
        _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");

        await Run();

        await Assert.That(Printed).DoesNotContain("watcher stopped");
        await Assert.That(Printed).Contains("Server data not deleted.");
        await Assert.That(Printed).Contains("did not confirm");
    }

    [Test]
    public async Task Disable_with_nothing_on_the_server_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);
        _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Run();

        await Assert.That(Printed).Contains("No server data found");
        await Assert.That(Printed).Contains("did not confirm");
    }

    /// <summary>A stale claim is a daemon that stopped beating, so with nobody answering nothing is recording. It comes back
    /// to a marker and refuses the source.</summary>
    [Test]
    public async Task A_stale_claim_nobody_answers_for_is_a_clean_stop() {
        Claim(Session);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A corrupt claim is still a claim, and with no name in it there is nobody to ask.</summary>
    [Test]
    public async Task A_claim_that_names_nobody_is_not_a_clean_stop() {
        Claim(Session);
        await File.WriteAllTextAsync(Path.Combine(Paths.Directory, $"{Session}.claim"), "garbage");
        Claims.Beat(Session);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("did not confirm");
        await Assert.That(_daemon.Asked).IsEmpty();
    }

    /// <summary>A hand-off started ahead of the disable runs on by itself. The transcript here is missing, which fails a
    /// generation that goes ahead, so the marker is what ends this one cleanly.</summary>
    [Test]
    public async Task A_title_handed_off_ahead_of_a_disable_is_dropped() {
        using var home = new TempHome();
        DisabledSessions.Mark(Session, Config.Root);
        var command = new GenerateTitleCommand(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(home), new FixedCapacitorHttpClient(), TimeProvider.System);

        await Assert.That(await command.GenerateAsync("http://127.0.0.1:1", Session, "claude", Config.Root.Path("no-such-transcript.jsonl"))).IsEqualTo(0);
    }

    sealed class FakeSessions : ISessionsApi {
        readonly List<string> _deleted = [];

        public DeleteSessionResponse Answer { get; set; } = new DeleteSessionResponse.Deleted();

        public CapacitorApiException? Failure { get; set; }

        public Action? OnDelete { get; set; }

        public IReadOnlyList<string> Deleted => _deleted;

        public Task<DeleteSessionResponse> DeleteSessionAsync(string sessionId, CancellationToken ct = default) {
            OnDelete?.Invoke();
            _deleted.Add(sessionId);

            return Failure is { } failure ? Task.FromException<DeleteSessionResponse>(failure) : Task.FromResult(Answer);
        }

        public Task HideSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SetTitleResult> SetSessionTitleAsync(string sessionId, string title, bool rename, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ErrorsResult> GetErrorsAsync(string sessionId, bool chain, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RecapResult> GetRecapAsync(string sessionId, bool chain, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TurnsResult> GetTurnsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TurnDetailResult> GetTurnAsync(string sessionId, int turnIndex, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PlanArtifactsResult> GetPlanArtifactsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class FakeDaemon : IDaemonRecordingClient {
        readonly List<(string Daemon, string Session)> _asked = [];

        public RecordingDisable Answer { get; set; } = RecordingDisable.Unanswered;

        public bool Hangs { get; set; }

        public Action? OnAsked { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string Daemon, string Session)> Asked {
            get {
                lock (_asked) return [.._asked];
            }
        }

        public Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, CancellationToken ct) => Task.FromResult(RecordingHandOver.Unanswered);

        public Task<bool> FinishSourceAsync(string key, CancellationToken ct) => Task.FromResult(false);

        public async Task<RecordingDisable> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) {
            lock (_asked) _asked.Add((daemonName, sessionId));
            OnAsked?.Invoke();
            Reached.TrySetResult();

            if (Hangs) await new TaskCompletionSource().Task;

            return Answer;
        }
    }
}
