using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class RecorderHostTests {
    const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
    const string Prompt = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply  = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";
    const string Later  = """{"type":"assistant","message":{"content":[{"type":"text","text":"And more."}]}}""";
    const string Held   = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"key " + Secret + "\"}]}}";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeRecorderTransport _transport = new() { IsConnected = true };
    readonly RecordingTime _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    readonly ConcurrentDictionary<string, int> _ticks = new();
    readonly ConcurrentDictionary<string, Func<Task>?> _beforeDrain = new();
    readonly ConcurrentDictionary<string, TimeProvider> _clocks = new();
    readonly ConcurrentDictionary<string, Func<Task>?> _prepare = new();
    readonly ConcurrentQueue<string> _log = new();
    readonly ConcurrentDictionary<string, int> _beats = new();
    readonly ConcurrentDictionary<string, Func<Task>?> _afterFinalDrain = new();
    readonly ConcurrentDictionary<string, bool> _logThrows = new();
    readonly ConcurrentDictionary<string, bool> _beatThrows = new();
    readonly ConcurrentQueue<IReadOnlyCollection<string>> _attemptChildren = new();
    readonly ConcurrentQueue<RecorderAttemptEnded> _ended = new();
    readonly ConcurrentDictionary<string, Func<string, Task<ClaudeCliResult?>>> _titles = new();
    readonly ConcurrentDictionary<string, Action> _handOffs = new();
    readonly ConcurrentDictionary<string, Action<string>> _logHooks = new();

    public RecorderHostTests() => _time.Attempts = () => _transport.ConnectAttempts;

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    RecorderClaims Claims => field ??= new(Tmp.CreateDir("watchers"), _time);

    RecorderHost NewHost(Func<string, FileStream>? open = null, Action<string>? factoryHook = null, RecorderClaims? claims = null, Action<RecorderAttemptEnded>? attemptEnded = null) => new(
        _transport,
        _ => {
            factoryHook?.Invoke("stamper");

            return new NoStamps();
        },
        source => {
            factoryHook?.Invoke("context");

            return new RecorderContext(
            new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)),
            new FakeRecorderHousekeeping {
                SpawnedChildKeys = _attemptChildren.TryDequeue(out var children) ? children : [],
                OnGenerateTitle = _titles.GetValueOrDefault(source.Key),
                OnHandOffTitle = _handOffs.GetValueOrDefault(source.Key),
                OnAfterFinalDrain = () => _afterFinalDrain.TryGetValue(source.Key, out var hook) && hook is not null ? hook() : Task.CompletedTask,
                OnPrepare = () => _prepare.TryGetValue(source.Key, out var hook) && hook is not null ? hook() : Task.CompletedTask,
                BeforeDrain = () => {
                    _ticks.AddOrUpdate(source.Key, 1, (_, n) => n + 1);

                    return _beforeDrain.TryGetValue(source.Key, out var hook) && hook is not null ? hook() : Task.CompletedTask;
                },
            },
            _clocks.GetValueOrDefault(source.Key, TimeProvider.System), message => {
                if (_logThrows.ContainsKey(source.Key)) throw new InvalidOperationException("the log is broken");

                _log.Enqueue($"{source.Key}: {message}");
                if (_logHooks.TryGetValue(source.Key, out var hook)) hook(message);
            }, () => {
                if (_beatThrows.ContainsKey(source.Key)) throw new InvalidOperationException("the beat is broken");

                _beats.AddOrUpdate(source.Key, 1, (_, n) => n + 1);
            });
        },
        _time, open, claims, attemptEnded ?? _ended.Enqueue);

    static RecordingSource Source(string path, string? agentId = null, string? sessionId = null) =>
        new(sessionId ?? Guid.NewGuid().ToString("N"), agentId, path, "claude", null);

    int Ticks(string key) => _ticks.GetValueOrDefault(key);

    int LinesSent(string sessionId) => _transport.Batches.Where(batch => batch.SessionId == sessionId).Sum(batch => batch.Lines.Length);

    /// <summary>Waits for <paramref name="condition"/>, moving the clock by <paramref name="step"/> on each look.</summary>
    async Task Eventually(Func<bool> condition, string what, TimeSpan? step = null) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition()) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Never saw: {what}");

            if (step is { } advance) _time.Advance(advance);

            await Task.Delay(5);
        }
    }

    /// <summary>Spends a shutdown's wait for its sends and not a moment of its bound: stepping the clock past this would
    /// reach the bound however quickly a cut drain spools, and a test of the spool would be reading the bound's mark.</summary>
    async Task ElapseTheWaitForSends(TimeSpan wait) {
        await Eventually(() => _time.Timers.Any(timer => timer.Due == wait), "the wait for the sends");
        _time.Advance(wait);
    }

    /// <summary>Past the grace with the claim still fresh means a beat landed after the take, and the only source of
    /// one is the host's backoff: every connect fails, so no recorder ever reaches its loop.</summary>
    [Test]
    public async Task A_source_in_a_backoff_keeps_its_claim_fresh() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var taken = _time.GetUtcNow();
        var host  = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(
            () => _time.GetUtcNow() - taken > RecorderClaims.Grace * 2 && Claims.Read(source.Key) is RecorderClaimState.DaemonFresh,
            "a beat past the grace", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.Connects).IsEqualTo(0);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>Each chunk gets a send budget of its own, so a batch of slow chunks can outlast the grace with the source
    /// never going round its loop. Here each one uses most of the grace, and the last still finds the claim fresh.</summary>
    [Test]
    public async Task A_long_batch_beats_its_claim_between_chunks() {
        var source = Source(Tmp.CreateFile("t.jsonl", string.Concat(Enumerable.Repeat($"{Prompt}\n", 250))), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var states = new ConcurrentQueue<RecorderClaimState>();
        _transport.OnSend = _ => {
            states.Enqueue(Claims.Read(source.Key));
            _time.Advance(RecorderClaims.Grace * 0.8);

            return Task.CompletedTask;
        };

        var host = NewHost(claims: Claims);
        host.TryAdd(source);

        await Eventually(() => states.Count >= 3, "three chunks", TimeSpan.FromSeconds(1));

        await Assert.That(states.All(state => state is RecorderClaimState.DaemonFresh)).IsTrue();
    }

    [Test]
    public async Task A_running_source_keeps_its_claim_fresh() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var taken = _time.GetUtcNow();
        var host  = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(
            () => _time.GetUtcNow() - taken > RecorderClaims.Grace * 2 && Claims.Read(source.Key) is RecorderClaimState.DaemonFresh,
            "a beat past the grace", TimeSpan.FromSeconds(1));

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>The heartbeat file and the claim fail apart, so the broken one must not take the other with it.</summary>
    [Test]
    public async Task A_failing_heartbeat_still_beats_the_claim() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        _beatThrows[source.Key] = true;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var taken = _time.GetUtcNow();
        var host  = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(
            () => _time.GetUtcNow() - taken > RecorderClaims.Grace * 2 && Claims.Read(source.Key) is RecorderClaimState.DaemonFresh,
            "a beat past the grace", TimeSpan.FromSeconds(1));
    }

    /// <summary>A file the system won't let anyone stamp makes the claim's beat throw for real, and only macOS lets an
    /// unprivileged test do that. The beat failing must cost the source its staleness at worst, never its recording.</summary>
    [Test, RunOn(OS.MacOs)]
    public async Task A_claim_file_that_refuses_a_beat_does_not_stop_the_recording() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var claim = Claims.ClaimFile(source.Key);
        await ChangeFlags("uchg", claim);

        try {
            await Assert.That(() => Claims.Beat(source.Key)).Throws<UnauthorizedAccessException>();

            var host = NewHost(claims: Claims);
            host.TryAdd(source);
            await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

            await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
            await Eventually(() => LinesSent(source.SessionId) == 2, "the next line", TimeSpan.FromSeconds(1));

            await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        } finally {
            await ChangeFlags("nouchg", claim);
        }
    }

    static async Task ChangeFlags(string flag, string path) {
        using var chflags = Process.Start("chflags", [flag, path]);
        await chflags.WaitForExitAsync().WithinDeadlineAsync("chflags");
    }

    /// <summary>The hook that took the claim owns the source. A final drain here would run beside its watcher, and a
    /// retry would register over it.</summary>
    [Test]
    public async Task A_source_that_lost_its_claim_ends_with_no_drain_and_no_retry() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        Claims.Release(source.Key);
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave", TimeSpan.FromSeconds(1));

        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(_transport.ConnectAttempts).IsEqualTo(1);
        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsFalse();
    }

    [Test]
    public async Task A_source_that_lost_its_claim_in_a_backoff_never_connects_again() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");

        Claims.Release(source.Key);
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.ConnectAttempts).IsEqualTo(1);
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(0);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    [Test]
    public async Task Finishing_a_source_that_lost_its_claim_in_a_backoff_drains_nothing() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");

        Claims.Release(source.Key);
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.ConnectAttempts).IsEqualTo(1);
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(0);
        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>The claim is the host's owner's to give up: a source that ends on its own keeps it, so the taker
    /// decides what ending means. A daemon restarting wants it kept, and a finished session released.</summary>
    [Test]
    public async Task A_finished_source_leaves_its_claim_to_whoever_took_it() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A stale-session sweep can't reap a key its connection still holds, and on a daemon's connection that is
    /// for good. A source that lost its claim sends nothing else, but its registration still has to go.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_source_that_ends_by_itself_detaches_from_the_server(bool lostClaim) {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await Assert.That(_transport.Detached).IsEmpty();

        if (lostClaim) Claims.Release(source.Key);

        await Eventually(() => host.ActiveKeys.Count == 0, "the source to end", lostClaim ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(7));

        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_finished_source_detaches_once() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_source_that_never_connected_detaches_too() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
    }

    /// <summary>A retry keeps its registration: the source is still this connection's, and a server stop has to find it.</summary>
    [Test]
    public async Task A_failed_attempt_does_not_detach() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the source to record after its backoff", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.Detached).IsEmpty();
    }

    /// <summary>On a shared connection a detach landing behind the next recorder's connect would remove that recorder's
    /// registration, so it has to be on its way while the key is still refused.</summary>
    [Test]
    public async Task A_detach_starts_before_the_key_can_be_taken_again() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host    = NewHost();
        var refused = false;
        _transport.OnDetach = _ => {
            refused = !host.TryAdd(source);

            return Task.CompletedTask;
        };

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(refused).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_detach_that_fails_harms_nothing(bool hangs) {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.OnDetach = _ => hangs ? new TaskCompletionSource().Task : throw new InvalidOperationException("the hub is down");
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsFalse();

        _transport.OnDetach = null;
        await Assert.That(host.TryAdd(source)).IsTrue();
        await Eventually(() => _transport.Connects == 2, "the source to record again");
    }

    /// <summary>The host disposes each attempt's recorder, so this is all its owner gets of it, and the next attempt
    /// has no idea what the failed one started.</summary>
    [Test]
    public async Task The_owner_hears_of_every_attempt_ending() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var thrown = 0;
        _attemptChildren.Enqueue(["started-by-the-failed-attempt"]);
        _attemptChildren.Enqueue(["started-by-the-last-attempt"]);
        _beforeDrain[source.Key] = () => Interlocked.Increment(ref thrown) == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the retry's line", TimeSpan.FromSeconds(1));
        await Assert.That(_ended.Count).IsEqualTo(1);

        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var attempts = _ended.ToArray();
        await Assert.That(attempts.Length).IsEqualTo(2);

        await Assert.That(attempts[0].Source).IsEqualTo(source);
        await Assert.That(attempts[0].SourceEnded).IsFalse();
        await Assert.That(attempts[0].Failure?.Message).IsEqualTo("boom");
        await Assert.That(attempts[0].SpawnedChildKeys).IsEquivalentTo(["started-by-the-failed-attempt"]);

        await Assert.That(attempts[1].SourceEnded).IsTrue();
        await Assert.That(attempts[1].Failure).IsNull();
        await Assert.That(attempts[1].SpawnedChildKeys).IsEquivalentTo(["started-by-the-last-attempt"]);
        await Assert.That(attempts[1].LostClaim).IsFalse();
        await Assert.That(attempts[1].EndedIdle).IsFalse();
    }

    [Test]
    public async Task The_owner_hears_a_source_went_idle() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await Eventually(() => host.ActiveKeys.Count == 0, "the idle end", TimeSpan.FromHours(7));

        var ended = _ended.Single();
        await Assert.That(ended.EndedIdle).IsTrue();
        await Assert.That(ended.SourceEnded).IsTrue();
    }

    [Test]
    public async Task The_owner_hears_a_source_lost_its_claim() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        Claims.Release(source.Key);
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave", TimeSpan.FromSeconds(1));

        var ended = _ended.Single();
        await Assert.That(ended.LostClaim).IsTrue();
        await Assert.That(ended.SourceEnded).IsTrue();
    }

    [Test]
    public async Task The_owner_hears_a_final_drain_failed() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _afterFinalDrain[source.Key] = () => throw new InvalidOperationException("boom");
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var ended = _ended.Single();
        await Assert.That(ended.Failure?.Message).IsEqualTo("boom");
        await Assert.That(ended.SourceEnded).IsTrue();
    }

    [Test]
    public async Task The_owner_hears_of_an_attempt_that_could_not_be_prepared() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var contexts = 0;
        var host = NewHost(factoryHook: kind => {
            if (kind == "context" && Interlocked.Increment(ref contexts) == 2) throw new InvalidOperationException("no context");
        });

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the source to record past the failed factory", TimeSpan.FromSeconds(1));

        var attempts = _ended.ToArray();
        await Assert.That(attempts.Length).IsEqualTo(2);
        await Assert.That(attempts[0].Failure?.Message).IsEqualTo("The hub is down.");
        await Assert.That(attempts[1].Failure?.Message).IsEqualTo("no context");
        await Assert.That(attempts[1].SourceEnded).IsFalse();
    }

    /// <summary>The first attempt never connected, so the host drains with a fresh recorder, and that is an attempt too.</summary>
    [Test]
    public async Task The_owner_hears_of_the_drain_a_finish_makes_during_a_backoff() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var attempts = _ended.ToArray();
        await Assert.That(attempts.Length).IsEqualTo(2);
        await Assert.That(attempts[0].SourceEnded).IsFalse();
        await Assert.That(attempts[1].SourceEnded).IsTrue();
        await Assert.That(attempts[1].Failure).IsNull();
    }

    [Test]
    public async Task The_owner_hears_of_a_drain_that_could_not_connect() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var last = _ended.ToArray()[^1];
        await Assert.That(last.SourceEnded).IsTrue();
        await Assert.That(last.Failure?.Message).IsEqualTo("The hub is down.");
    }

    [Test]
    public async Task An_owner_that_throws_harms_nothing() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost(attemptEnded: _ => throw new InvalidOperationException("the owner is broken"));

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the retry's line", TimeSpan.FromSeconds(1));
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task A_finished_codex_source_retires_its_ack_count() {
        var source = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "codex", null);
        var cursor = new WatchAckCursor(Config);
        _transport.ResumeFrom = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => cursor.TryRead(source.SessionId) == 1, "the published count");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(cursor.TryRead(source.SessionId)).IsNull();
    }

    /// <summary>The hook that took the source has a recorder of its own now, and the count is that recorder's.</summary>
    [Test]
    public async Task A_codex_source_that_lost_its_claim_leaves_the_ack_count() {
        var source = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "codex", null);
        var cursor = new WatchAckCursor(Config);
        _transport.ResumeFrom = 1;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => cursor.TryRead(source.SessionId) == 1, "the published count");
        Claims.Release(source.Key);
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave", TimeSpan.FromSeconds(1));

        await Assert.That(cursor.TryRead(source.SessionId)).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_attempt_leaves_the_ack_count_for_the_next() {
        var source = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "codex", null);
        var cursor = new WatchAckCursor(Config);
        var thrown = 0;
        _transport.ResumeFrom = 1;
        _beforeDrain[source.Key] = () => Interlocked.Increment(ref thrown) == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");

        await Assert.That(cursor.TryRead(source.SessionId)).IsEqualTo(1);
    }

    [Test]
    public async Task A_redaction_hold_stalls_only_its_own_source() {
        var stalled = new SwitchableRedactionClock { Exhausted = false };
        var heldSource = Source(Tmp.CreateFile("held.jsonl", $"{Prompt}\n"), "a1");
        var freeSource = Source(Tmp.CreateFile("free.jsonl", $"{Prompt}\n"), "a2");
        _clocks[heldSource.Key] = stalled;
        var host = NewHost();

        host.TryAdd(heldSource);
        host.TryAdd(freeSource);
        await Eventually(() => LinesSent(heldSource.SessionId) == 1 && LinesSent(freeSource.SessionId) == 1, "the first line of each");

        stalled.Exhausted = true;
        await File.AppendAllTextAsync(heldSource.TranscriptPath, $"{Held}\n{Later}\n");
        await File.AppendAllTextAsync(freeSource.TranscriptPath, $"{Reply}\n{Later}\n");
        await Eventually(() => LinesSent(freeSource.SessionId) == 3, "the free source's lines", TimeSpan.FromSeconds(1));
        var ticksWhenFreeCaughtUp = Ticks(heldSource.Key);
        await Eventually(() => Ticks(heldSource.Key) >= ticksWhenFreeCaughtUp + 3, "ticks while held", TimeSpan.FromSeconds(1));

        await Assert.That(LinesSent(heldSource.SessionId)).IsEqualTo(1);

        stalled.Exhausted = false;
        await Eventually(() => LinesSent(heldSource.SessionId) == 3, "the held source to catch up", TimeSpan.FromMinutes(5));

        var heldText = string.Concat(_transport.Batches.Where(batch => batch.SessionId == heldSource.SessionId).SelectMany(batch => batch.Lines));
        await Assert.That(heldText).DoesNotContain(Secret);
    }

    TranscriptSpool Spool => new(Config, _time, "http://127.0.0.1:1");

    /// <summary>A final drain would send this line, and its person has been told their data is gone.</summary>
    [Test]
    public async Task An_abandoned_source_makes_no_final_drain() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
        await Assert.That(_ended.Last() is { SourceEnded: true, LostClaim: true }).IsTrue();
        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>A claim that can't be deleted stays in place, and that mustn't leave the source free to drain.</summary>
    [Test]
    public async Task An_abandoned_source_ends_with_its_claim_still_in_place() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(source.SessionId)).IsFalse();
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>The drain is past its first check when this lands. A tail spooled here goes out on the next drain pass and
    /// a mark invites the import to send it again, so neither is left, and nothing waits for the send it interrupted.</summary>
    [Test]
    public async Task A_final_drain_already_running_stops_with_nothing_spooled_or_marked() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        _transport.OnSend = async ct => {
            sending.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await sending.Task.WithinDeadlineAsync("the final drain's send");

        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");
        await finish.WithinDeadlineAsync("the finish");

        await Assert.That(Spool.HasBacklog(source.SessionId)).IsFalse();
        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>An older <c>kcap disable</c> sends no frame and leaves the claim where it is, then prints that recording
    /// stopped. The marker is all this recorder hears of that.</summary>
    [Test]
    public async Task A_marker_stops_a_recorder_holding_its_claim() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        DisabledSessions.Mark(source.SessionId, Config);
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        await Eventually(() => host.ActiveKeys.Count == 0, "the recorder to stop", TimeSpan.FromSeconds(1));

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(_ended.Last() is { SourceEnded: true, LostClaim: true }).IsTrue();
        await Assert.That(Spool.HasBacklog(source.SessionId)).IsFalse();
        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>A recorder can stall anywhere in a tick, and one waking past the tick's check would otherwise send what it
    /// read. A stale claim nobody answers for is a clean stop on the strength of this.</summary>
    [Test]
    public async Task A_recorder_woken_past_its_tick_sends_nothing() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeDrain[source.Key] = () => {
            entered.TrySetResult();

            return release.Task;
        };
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await entered.Task.WithinDeadlineAsync("the recorder to stall");

        DisabledSessions.Mark(source.SessionId, Config);
        release.SetResult();
        await Eventually(() => host.ActiveKeys.Count == 0, "the recorder to stop", TimeSpan.FromSeconds(1));

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(0);
    }

    [Test]
    public async Task An_excluded_session_keeps_recording() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost(claims: Claims);
        Claims.TryTake(source.Key, "kcap", isHosted: true);
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        DisabledSessions.Exclude(source.SessionId, Config);
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        await Eventually(() => LinesSent(source.SessionId) == 2, "the next line", TimeSpan.FromSeconds(1));
    }

    /// <summary>A watcher holds no claims and <c>kcap disable</c> stops it by signal, with its final drain. Reading the
    /// marker there would change what a watcher does at no gain.</summary>
    [Test]
    public async Task A_recorder_with_no_claims_reads_no_marker() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();
        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first line");

        DisabledSessions.Mark(source.SessionId, Config);
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        await Eventually(() => LinesSent(source.SessionId) == 2, "the next line", TimeSpan.FromSeconds(1));
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
    }

    /// <summary>A generated title is cut off here, and handing it over starts a process that posts it on its own.</summary>
    [Test]
    public async Task A_title_cut_off_by_an_abandon_is_handed_to_nobody() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n{Later}\n"));
        var titling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never   = new TaskCompletionSource<ClaudeCliResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handed  = 0;
        _titles[source.Key] = _ => {
            titling.TrySetResult();

            return never.Task;
        };
        _handOffs[source.Key] = () => Interlocked.Increment(ref handed);
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => titling.Task.IsCompleted, "the title to start generating", TimeSpan.FromSeconds(1));
        _ = host.FinishAsync(source.Key, CancellationToken.None);

        await Eventually(() => _log.Any(line => line.Contains("Waiting up to", StringComparison.Ordinal)), "the drain to wait for the title");
        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");

        await Assert.That(handed).IsEqualTo(0);
        never.SetResult(null);
    }

    /// <summary>Nothing is listening between attempts, and a backoff can run for half a minute.</summary>
    [Test]
    public async Task An_abandoned_source_in_its_backoff_ends_at_once() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = int.MaxValue;
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);
        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");

        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(0);
        await Assert.That(_ended.Last() is { SourceEnded: true, LostClaim: true }).IsTrue();
        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>A failed attempt's drain is the one place a host builds a recorder after a stop, and one that can't be
    /// built marks the session. Here that must not happen.</summary>
    [Test]
    public async Task An_abandon_marks_nothing_when_the_last_recorder_cannot_be_built() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var abandon = false;
        _transport.ConnectFailures = int.MaxValue;
        var host = NewHost(claims: Claims, factoryHook: part => {
            if (Volatile.Read(ref abandon) && part == "context") throw new InvalidOperationException("no context");
        });
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");

        Volatile.Write(ref abandon, true);
        await host.AbandonAsync(source.Key).WithinDeadlineAsync("the source to end");

        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>A shutdown marks a source it can't finish, and this one's person asked for it to be gone.</summary>
    [Test]
    public async Task A_shutdown_marks_no_abandoned_source_stuck_where_no_token_reaches() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var stuck   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _prepare[source.Key] = () => {
            entered.TrySetResult();

            return stuck.Task;
        };
        var host = NewHost();
        host.TryAdd(source);
        await entered.Task.WithinDeadlineAsync("the source to get stuck");

        _ = host.AbandonAsync(source.Key);
        await host.StopAllAsync(TimeSpan.Zero, new CancellationToken(canceled: true)).WithinDeadlineAsync("the shutdown");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(Spool.NeedsImport(source.SessionId)).IsFalse();
        stuck.SetResult();
    }

    [Test]
    public async Task A_throwing_source_does_not_stop_the_others() {
        var failing = Source(Tmp.CreateFile("failing.jsonl", $"{Prompt}\n"), "a1");
        var healthy = Source(Tmp.CreateFile("healthy.jsonl", $"{Prompt}\n"), "a2");
        var thrown  = 0;
        _beforeDrain[failing.Key] = () => {
            if (Interlocked.Increment(ref thrown) == 1) throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        };
        var host = NewHost();

        host.TryAdd(failing);
        host.TryAdd(healthy);
        await Eventually(() => _log.Any(line => line.Contains("boom", StringComparison.Ordinal)) && LinesSent(healthy.SessionId) == 1, "the failure and the healthy source's first line");

        // The clock has not moved, so the failed source is still in its backoff.
        await Assert.That(host.ActiveKeys).IsEquivalentTo([failing.Key, healthy.Key]);
        await Assert.That(host.TryAdd(failing)).IsFalse();
        await Assert.That(LinesSent(failing.SessionId)).IsEqualTo(0);

        await File.AppendAllTextAsync(healthy.TranscriptPath, $"{Reply}\n");
        await Eventually(() => LinesSent(healthy.SessionId) == 2 && LinesSent(failing.SessionId) == 1, "both sources to record", TimeSpan.FromSeconds(1));
    }

    /// <summary>The reconnect is what reads the claim here, while the tick that is about to fail is held.</summary>
    [Test]
    public async Task A_failed_attempt_that_lost_its_claim_does_not_say_it_will_retry() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        _beforeDrain[source.Key] = async () => {
            if (Interlocked.Increment(ref calls) != 2) return;

            entered.SetResult();
            await release.Task;

            throw new InvalidOperationException("boom");
        };
        Claims.TryTake(source.Key, "daemon", isHosted: true);
        var host = NewHost(claims: Claims);

        host.TryAdd(source);
        await Eventually(() => entered.Task.IsCompleted, "the recorder entering its second tick", TimeSpan.FromSeconds(1));
        Claims.Release(source.Key);
        await _transport.RaiseReconnectedAsync();
        release.SetResult();
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave");

        var failure = _log.Single(line => line.Contains("boom", StringComparison.Ordinal));
        await Assert.That(failure).Contains("lost its claim");
        await Assert.That(failure).DoesNotContain("retry");
    }

    [Test]
    public async Task A_failed_source_is_recorded_again_after_a_backoff() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 2;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the first attempt");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(host.TryAdd(source)).IsFalse();
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(0);

        await Eventually(() => LinesSent(source.SessionId) == 1, "the source to record after its backoffs", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.ConnectAttempts).IsEqualTo(3);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A send the transport gave up on is a failed send: the same lines go again, and a first copy that lands
    /// late is dropped by the server, which takes nothing at or below its mark.</summary>
    [Test]
    public async Task A_batch_that_timed_out_is_sent_again() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        var sends  = 0;
        _transport.OnSend = _ => Interlocked.Increment(ref sends) == 1 ? throw new TimeoutException("No answer.") : Task.CompletedTask;
        var host = NewHost();

        host.TryAdd(source);

        await Eventually(() => LinesSent(source.SessionId) == 2, "the lines to go again", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.Batches[0].LineNumbers).IsEquivalentTo([0, 1]);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>With no registration the server has no stop path to this source, and the next reconnect can be days off.
    /// A rollout makes every source fail this at once, so each goes back through its own retry.</summary>
    [Test]
    public async Task A_failed_re_registration_fails_the_attempt_and_registers_again() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the first attempt", TimeSpan.FromSeconds(1));

        _transport.ConnectFailures = 1;
        await _transport.RaiseReconnectedAsync();

        await Eventually(() => _transport.Connects == 2, "the source to register again", TimeSpan.FromSeconds(1));

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(_ended.Count(ended => ended.Failure is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task A_source_that_ends_cleanly_is_not_retried() {
        var stopped = Source(Tmp.CreateFile("stopped.jsonl", $"{Prompt}\n"), "a1");
        var host    = NewHost();

        host.TryAdd(stopped);
        await Eventually(() => _transport.Connects == 1, "the source to register");
        await _transport.RaiseStopRequestedAsync(stopped.Key, "the server asked");
        await Eventually(() => host.ActiveKeys.Count == 0, "the source to leave");

        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        await Assert.That(_transport.ConnectAttempts).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task Finishing_a_source_that_never_connected_still_drains_it() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n{Later}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepares = 0;
        _prepare[source.Key] = () => {
            if (Interlocked.Increment(ref prepares) > 1) return Task.CompletedTask;

            entered.SetResult();

            return release.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await entered.Task.WithinDeadlineAsync("the recorder entering its preparation");
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        release.SetResult();
        await finish.WithinDeadlineAsync("the finish");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(3);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task Finishing_during_a_backoff_drains_once() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(2);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(_transport.Connects).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task A_source_that_cannot_connect_at_finish_is_marked_for_import() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task Cancelling_a_finish_ends_the_wait_and_not_the_drain() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.OnDrainComplete = ct => {
            entered.TrySetResult(ct);

            return release.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        using var cancel = new CancellationTokenSource();
        var finish = host.FinishAsync(source.Key, cancel.Token);
        var drainToken = await entered.Task.WithinDeadlineAsync("the drain reaching its completion signal");

        await cancel.CancelAsync();
        await Assert.That(async () => await finish.WithinDeadlineAsync("the cancelled wait")).Throws<OperationCanceledException>();

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(drainToken.IsCancellationRequested).IsFalse();

        release.SetResult();
        await Eventually(() => host.ActiveKeys.Count == 0, "the drain to end the source");
    }

    [Test]
    public async Task A_finish_cancelled_on_entry_still_drains_the_tail() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 2, "its lines");
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Later}\n");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await host.FinishAsync(source.Key, cancelled.Token)).Throws<OperationCanceledException>();
        await Eventually(() => host.ActiveKeys.Count == 0, "the drain to end the source");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(3);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_connect_that_never_returns_at_finish_is_marked_for_import() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHangs = true;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");

        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await finish;

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    /// <summary>A cut run on the shutdown's own loop would leave every source behind this one uncut. Whichever send is
    /// cut first has a token callback that blocks, and both drains have to finish regardless.</summary>
    [Test]
    public async Task A_shutdown_cuts_every_drain_though_one_cut_blocks() {
        var first  = Source(Tmp.CreateFile("first.jsonl", $"{Prompt}\n"), "a1");
        var second = Source(Tmp.CreateFile("second.jsonl", $"{Prompt}\n"), "a2");
        using var release = new ManualResetEventSlim();
        var cut     = 0;
        var sending = 0;
        var host    = NewHost();

        host.TryAdd(first);
        host.TryAdd(second);
        await Eventually(() => LinesSent(first.SessionId) == 1 && LinesSent(second.SessionId) == 1, "both first lines");

        _transport.OnSend = ct => {
            ct.Register(() => {
                if (Interlocked.Increment(ref cut) == 1) release.Wait();
            });
            Interlocked.Increment(ref sending);

            return Task.Delay(Timeout.Infinite, ct);
        };
        await File.AppendAllTextAsync(first.TranscriptPath, $"{Reply}\n");
        await File.AppendAllTextAsync(second.TranscriptPath, $"{Reply}\n");

        var stopping = Task.Run(() => host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        await Eventually(() => Volatile.Read(ref sending) == 2, "both final sends");

        try {
            _ = Task.Run(() => ElapseTheWaitForSends(TimeSpan.FromSeconds(8)));
            await Eventually(() => host.ActiveKeys.Count == 0, "both drains to end");
        } finally {
            release.Set();
        }

        await stopping.WithinDeadlineAsync("the shutdown");
    }

    /// <summary>Cursor's final drain takes the rewind gate first, and that wait refuses a token already cut. The spool
    /// still has to get the line, and a spooled source needs no import.</summary>
    [Test]
    public async Task A_cursor_drain_that_starts_cut_off_still_spools() {
        var source  = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "cursor", null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeDrain[source.Key] = () => {
            if (LinesSent(source.SessionId) == 0) return Task.CompletedTask;

            entered.TrySetResult();

            return release.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => entered.Task.IsCompleted, "the recorder held in its second tick", TimeSpan.FromSeconds(1));
        _transport.OnSend = ct => Task.Delay(Timeout.Infinite, ct);
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Later}\n");

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await Eventually(() => _time.Timers.Any(timer => timer.Due == TimeSpan.FromSeconds(2)), "the wait for the spool");

        release.SetResult();
        await stopping.WithinDeadlineAsync("the shutdown");

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.HasBacklog(source.SessionId)).IsTrue();
        await Assert.That(spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>The clock stands still, so no bound elapses: a healthy shutdown takes as long as its drains.</summary>
    [Test]
    public async Task Shutdown_finishes_every_source_within_its_bound() {
        var running   = Source(Tmp.CreateFile("running.jsonl", $"{Prompt}\n"), "a1");
        var unsent    = Source(Tmp.CreateFile("unsent.jsonl", $"{Prompt}\n"), "a2");
        var backedOff = Source(Tmp.CreateFile("backed-off.jsonl", $"{Prompt}\n"), "a3");
        var host      = NewHost();

        host.TryAdd(running);
        host.TryAdd(unsent);
        await Eventually(() => LinesSent(running.SessionId) == 1 && LinesSent(unsent.SessionId) == 1, "both first lines");
        await File.AppendAllTextAsync(unsent.TranscriptPath, $"{Reply}\n");

        _transport.ConnectFailures = 1;
        host.TryAdd(backedOff);
        await Eventually(() => _ended.Count == 1, "the failed attempt");

        await host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None).WithinDeadlineAsync("the shutdown");

        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(LinesSent(unsent.SessionId)).IsEqualTo(2);
        await Assert.That(LinesSent(backedOff.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(3);
        await Assert.That(_transport.Detached).IsEquivalentTo([running.Key, unsent.Key, backedOff.Key]);
    }

    [Test]
    public async Task A_source_cannot_be_added_once_shutdown_has_begun() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        await host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None).WithinDeadlineAsync("the shutdown");

        await Assert.That(host.TryAdd(source)).IsFalse();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    /// <summary>A send honouring its token is cut off and its lines go to the spool. One that ignores its token leaves
    /// the source stuck where nothing can reach it, so at the bound the host marks it and leaves it behind.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_send_that_hangs_at_shutdown_spools_or_marks_its_source(bool honoursItsToken) {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host    = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        _transport.OnSend = ct => {
            entered.TrySetResult();

            return honoursItsToken ? Task.Delay(Timeout.Infinite, ct) : new TaskCompletionSource().Task;
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await entered.Task.WithinDeadlineAsync("the final drain's send");
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));

        if (!honoursItsToken) {
            await Eventually(() => _time.Timers.Any(timer => timer.Due == TimeSpan.FromSeconds(2)), "the wait for the spool");
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        await stopping.WithinDeadlineAsync("the shutdown");

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);

        if (honoursItsToken) {
            await Assert.That(spool.HasBacklog(source.SessionId)).IsTrue();
            await Assert.That(spool.NeedsImport(source.SessionId)).IsFalse();
            await Assert.That(host.ActiveKeys).IsEmpty();
        } else {
            await Assert.That(spool.HasBacklog(source.SessionId)).IsFalse();
            await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
        }
    }

    /// <summary>A finish already under way is what a shutdown overtakes: its drain has to take the same cut.</summary>
    [Test]
    public async Task A_shutdown_cuts_a_finish_that_is_hanging_in_a_send() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.OnSend = ct => {
            entered.TrySetResult();

            return Task.Delay(Timeout.Infinite, ct);
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await entered.Task.WithinDeadlineAsync("the final drain's send");

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await stopping.WithinDeadlineAsync("the shutdown");
        await finish.WithinDeadlineAsync("the finish");

        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").HasBacklog(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    /// <summary>No server cursor means no way to tell what to spool, so it is the mark that keeps these lines.</summary>
    [Test]
    public async Task A_connect_that_hangs_at_shutdown_marks_its_source() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHangs = true;

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await stopping.WithinDeadlineAsync("the shutdown");

        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    /// <summary>The token brings the bound forward: what hasn't finished is cut and marked there and then, because a
    /// caller that cancels isn't going to wait for a spool.</summary>
    [Test]
    public async Task Cancelling_a_shutdown_marks_what_it_could_not_finish() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host    = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        _transport.OnSend = _ => {
            entered.TrySetResult();

            return new TaskCompletionSource().Task;
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        using var cancel = new CancellationTokenSource();
        var stopping = host.StopAllAsync(TimeSpan.FromMinutes(10), cancel.Token);
        await entered.Task.WithinDeadlineAsync("the final drain's send");
        await cancel.CancelAsync();
        await stopping.WithinDeadlineAsync("the shutdown");

        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
    }

    /// <summary>A shutdown's drain has no time for a title: it goes to whatever outlives the host, and the drain ends
    /// inside the bound.</summary>
    [Test]
    public async Task A_title_still_generating_at_shutdown_is_handed_off() {
        var source     = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "claude", null);
        var generating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked    = new TaskCompletionSource<ClaudeCliResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _titles[source.Key] = _ => {
            generating.TrySetResult();

            return blocked.Task;
        };
        _handOffs[source.Key] = () => handed.TrySetResult();
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 2, "its lines");

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        await generating.Task.WithinDeadlineAsync("the title to start");
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(2));
        await stopping.WithinDeadlineAsync("the shutdown");

        await Assert.That(handed.Task.IsCompleted).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>The recorder is held until the shutdown's second wait has begun, so its drain starts already cut and
    /// never reads, and the tail spool leaves an incomplete line out. The bound hasn't passed: the mark is the drain's.</summary>
    [Test]
    public async Task A_line_still_incomplete_when_a_drain_starts_cut_off_marks_the_source() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{{\"type\":\"assistant"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeDrain[source.Key] = () => {
            if (LinesSent(source.SessionId) == 0) return Task.CompletedTask;

            entered.TrySetResult();

            return release.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => entered.Task.IsCompleted, "the recorder held in its second tick", TimeSpan.FromSeconds(1));

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await Eventually(() => _time.Timers.Any(timer => timer.Due == TimeSpan.FromSeconds(2)), "the wait for the spool");

        release.SetResult();
        await stopping.WithinDeadlineAsync("the shutdown");

        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
    }

    /// <summary>What the final drain's housekeeping does with a cut token must not read as a failed drain: the lines are
    /// safe by then, and a false mark sends someone to an import with nothing in it.</summary>
    [Test]
    public async Task Housekeeping_cut_off_at_shutdown_does_not_mark_a_source_that_spooled() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host    = NewHost();
        _afterFinalDrain[source.Key] = () => throw new OperationCanceledException();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        _transport.OnSend = ct => {
            entered.TrySetResult();

            return Task.Delay(Timeout.Infinite, ct);
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await entered.Task.WithinDeadlineAsync("the final drain's send");
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await stopping.WithinDeadlineAsync("the shutdown");

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.HasBacklog(source.SessionId)).IsTrue();
        await Assert.That(spool.NeedsImport(source.SessionId)).IsFalse();
    }

    /// <summary>On a daemon's connection a registration nobody detaches stays until the connection drops, and a session
    /// end then waits out the server's 10 seconds for a source nobody is recording.</summary>
    [Test]
    public async Task A_timed_out_connect_that_lands_late_is_detached() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");
        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await Eventually(() => _transport.Detached.Count == 1, "the end of the source");

        landed.SetResult();

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
        await Assert.That(_transport.Connects).IsEqualTo(1);
    }

    /// <summary>A connect that fails has told nobody what the server did with it. Detaching a key nothing registered is
    /// a no-op there.</summary>
    [Test]
    public async Task A_timed_out_connect_that_fails_late_is_detached_too() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");
        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await Eventually(() => _transport.Detached.Count == 1, "the end of the source");

        landed.SetException(new OperationCanceledException());

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
    }

    /// <summary>The server's detach goes by key and connection, so on a shared connection this one would take the
    /// registration of the recorder that took the key back.</summary>
    [Test]
    public async Task A_late_connect_leaves_a_source_that_is_recording_again_alone() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");
        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await Eventually(() => _transport.Detached.Count == 1, "the end of the source");

        _transport.ConnectHold = null;
        host.TryAdd(source);
        await Eventually(() => _transport.Connects == 1, "the source to record again");

        landed.SetResult();

        await Eventually(() => _log.Any(line => line.Contains("while it is being recorded", StringComparison.Ordinal)), "the late connect to be left alone");
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A transport keeps a connect open until the server answers, so a stop must not wait for it: the server
    /// holds this one, and the finish goes ahead with a fresh connect.</summary>
    [Test]
    public async Task A_stop_during_a_connect_the_server_holds_returns_at_once() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectHold = new TaskCompletionSource().Task;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectsHeld == 1, "the connect to reach the server");
        _transport.ConnectHold = null;
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_connect_left_behind_by_a_stop_is_detached_when_it_lands() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectHold = landed.Task;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectsHeld == 1, "the connect to reach the server");
        _transport.ConnectHold = null;
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);

        landed.SetResult();

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
    }

    /// <summary>A reconnect's re-registration lands on the same connection a source that has since ended detached from.</summary>
    [Test]
    public async Task A_reconnect_left_behind_by_a_stop_is_detached_when_it_lands() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        _transport.ConnectHold = landed.Task;
        var reconnecting = _transport.RaiseReconnectedAsync();
        await Eventually(() => _transport.ConnectsHeld == 1, "the re-registration to reach the server");
        _transport.ConnectHold = null;

        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
        await reconnecting.WithinDeadlineAsync("the handler to give up");
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);

        landed.SetResult();

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
    }

    /// <summary>The attempt's failure disposes the recorder with no stop, and a wait on a token nothing can cancel any more
    /// would park this handler, and every handler behind it on a shared raiser, for as long as the server takes.</summary>
    [Test]
    public async Task A_reconnect_in_flight_when_its_attempt_fails_is_detached_when_it_lands() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        _beforeDrain[source.Key] = async () => {
            if (Interlocked.Increment(ref calls) != 2) return;

            entered.SetResult();
            await release.Task;

            throw new InvalidOperationException("boom");
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => entered.Task.IsCompleted, "the recorder entering its second tick", TimeSpan.FromSeconds(1));

        _transport.ConnectHold = landed.Task;
        var reconnecting = _transport.RaiseReconnectedAsync();
        await Eventually(() => _transport.ConnectsHeld == 1, "the re-registration to reach the server");
        _transport.ConnectHold = null;

        release.SetResult();
        await reconnecting.WithinDeadlineAsync("the handler to give up with its recorder");

        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);

        landed.SetResult();

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
    }

    /// <summary>The late connect leaves the registration to the entry holding the key, and this one never gets as far as
    /// recording: the add fails, so it has to detach on its way out as any other source does.</summary>
    [Test]
    public async Task An_add_that_fails_detaches_a_connect_that_landed_during_it() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adding  = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _transport.ConnectFailures = 1;
        var host = NewHost(factoryHook: kind => {
            if (kind != "context" || !adding.IsSet) return;

            entered.Set();
            release.Wait();

            throw new InvalidOperationException("no context");
        });

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectsHeld == 1, "the final drain's connect");
        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);

        adding.Set();
        var add = Task.Run(() => host.TryAdd(source));
        await Eventually(() => entered.IsSet, "the add to reach its factory");

        landed.SetResult();
        await Eventually(() => _log.Any(line => line.Contains("while it is being recorded", StringComparison.Ordinal)), "the late connect to leave it to the add");

        release.Set();
        await Assert.That(async () => await add).Throws<InvalidOperationException>();

        await Assert.That(_transport.Detached.Count).IsEqualTo(2);
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    /// <summary>The same source is still draining over a later connect, and its own end detaches it after this landing.
    /// A detach here would pull the registration out from under that drain.</summary>
    [Test]
    public async Task A_connect_that_lands_during_the_sources_own_drain_leaves_it_registered() {
        var source   = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectHold = landed.Task;
        _transport.OnDrainComplete = _ => {
            entered.TrySetResult();

            return draining.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.ConnectsHeld == 1, "the connect to reach the server");
        _transport.ConnectHold = null;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await entered.Task.WithinDeadlineAsync("the drain to reach its completion signal");

        landed.SetResult();
        await Eventually(() => _log.Any(line => line.Contains("while it is being recorded", StringComparison.Ordinal)), "the late connect to be left alone");
        await Assert.That(_transport.Detached).IsEmpty();

        draining.SetResult();
        await finish.WithinDeadlineAsync("the finish");

        await Assert.That(_transport.Detached).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_connect_cut_off_at_shutdown_that_lands_late_is_detached() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;

        var stopping = host.StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Eventually(() => _transport.ConnectsHeld == 1, "the final drain's connect");
        await ElapseTheWaitForSends(TimeSpan.FromSeconds(8));
        await stopping.WithinDeadlineAsync("the shutdown");
        await Assert.That(_transport.Detached.Count).IsEqualTo(1);

        landed.SetResult();

        await Eventually(() => _transport.Detached.Count == 2, "the late registration to go");
    }

    /// <summary>The admission lock guards every add on the host, so nothing a transport or a logger does may run under
    /// it. A transport can throw before its first await, which lands the failure's log there.</summary>
    [Test]
    public async Task A_late_detach_that_throws_logs_outside_the_admission_lock() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var other  = Source(Tmp.CreateFile("other.jsonl", $"{Prompt}\n"), "a2");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        _transport.ConnectHold = landed.Task;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectsHeld == 1, "the final drain's connect");
        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);

        _transport.ConnectHold = null;
        _transport.OnDetach = _ => throw new InvalidOperationException("the hub is down");
        _logHooks[source.Key] = message => {
            if (message.StartsWith("Detaching", StringComparison.Ordinal)) logged.TrySetResult(Task.Run(() => host.TryAdd(other)).Wait(TimeSpan.FromSeconds(5)));
        };

        landed.SetResult();

        await Assert.That(await logged.Task.WithinDeadlineAsync("the failed detach's log")).IsTrue();
    }

    [Test]
    public async Task Backoff_doubles_to_its_cap_and_resets_after_a_long_run() {
        var capped = Source(Tmp.CreateFile("capped.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 100;
        var host = NewHost();

        host.TryAdd(capped);
        await Eventually(() => _transport.ConnectAttempts == 8, "eight attempts", TimeSpan.FromSeconds(1));

        var delays = Enumerable.Range(1, 7).Select(attempt => _time.Timers.Where(t => t.Attempts == attempt).Sum(t => t.Due.TotalSeconds)).ToList();
        await Assert.That(delays.SequenceEqual([1.0, 2, 4, 8, 16, 30, 30])).IsTrue().Because(string.Join(",", delays));
        await Assert.That(_time.Timers.Max(t => t.Due)).IsLessThanOrEqualTo(TranscriptRecorder.HeartbeatSlice);
        await Assert.That(_beats[capped.Key]).IsGreaterThanOrEqualTo(21);
    }

    [Test]
    public async Task A_long_run_resets_the_backoff() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 2;
        var calls  = 0;
        var firstAt = DateTimeOffset.MinValue;
        var timersAtFailure = -1;
        _beforeDrain[source.Key] = () => {
            if (Interlocked.Increment(ref calls) == 1) {
                firstAt = _time.GetUtcNow();
            } else if (timersAtFailure < 0 && _time.GetUtcNow() - firstAt >= TimeSpan.FromSeconds(30)) {
                timersAtFailure = _time.Timers.Count;

                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => timersAtFailure >= 0 && _transport.ConnectAttempts == 4, "the failure and the next attempt", TimeSpan.FromSeconds(31));

        var delay = _time.Timers.Skip(timersAtFailure).Where(t => t.Attempts == 3).Sum(t => t.Due.TotalSeconds);
        await Assert.That(delay).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_connected_recorder_is_drained_once_when_a_finish_arrives() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        _beforeDrain[source.Key] = async () => {
            if (Interlocked.Increment(ref calls) != 2) return;

            entered.SetResult();
            await release.Task;

            throw new InvalidOperationException("boom");
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 2, "its lines");
        await Eventually(() => entered.Task.IsCompleted, "the recorder entering its second drain", TimeSpan.FromSeconds(1));
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        _transport.ResumeFrom = LinesSent(source.SessionId);
        release.SetResult();
        await finish.WithinDeadlineAsync("the finish");

        await Assert.That(_transport.Connects).IsEqualTo(2);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(2);
    }

    [Test]
    public async Task A_final_drain_that_fails_marks_the_source_for_import() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _afterFinalDrain[source.Key] = () => throw new InvalidOperationException("boom");
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_log_or_beat_that_always_throws_does_not_stop_a_source_recording(bool log) {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        if (log) _logThrows[source.Key] = true;
        else _beatThrows[source.Key] = true;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 2, "its lines");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(host.ActiveKeys).IsEmpty();
        await Assert.That(_transport.Connects).IsEqualTo(1);
    }

    [Test]
    public async Task A_server_stop_before_the_source_connects_still_drains_it() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepares = 0;
        _prepare[source.Key] = () => {
            if (Interlocked.Increment(ref prepares) > 1) return Task.CompletedTask;

            entered.SetResult();

            return release.Task;
        };
        var host = NewHost();

        host.TryAdd(source);
        await entered.Task.WithinDeadlineAsync("the recorder entering its preparation");
        await _transport.RaiseStopRequestedAsync(source.Key, "the server asked");
        release.SetResult();

        await Eventually(() => host.ActiveKeys.Count == 0, "the stopped source to leave without a backoff");
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(2);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_server_stop_followed_by_a_failed_run_drains_without_a_backoff() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        _beforeDrain[source.Key] = async () => {
            if (Interlocked.Increment(ref calls) != 2) return;

            entered.SetResult();
            await release.Task;

            throw new InvalidOperationException("boom");
        };
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 2, "its lines");
        await Eventually(() => entered.Task.IsCompleted, "the recorder entering its second drain", TimeSpan.FromSeconds(1));
        await _transport.RaiseStopRequestedAsync(source.Key, "the server asked");
        _transport.ResumeFrom = LinesSent(source.SessionId);
        release.SetResult();

        await Eventually(() => host.ActiveKeys.Count == 0, "the stopped source to leave without a backoff");
        await Assert.That(_transport.Connects).IsEqualTo(2);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);

        var failure = _log.Single(line => line.Contains("boom", StringComparison.Ordinal));
        await Assert.That(failure).Contains("drains once");
        await Assert.That(failure).DoesNotContain("retry");
    }

    // The host's own read of the lines just appended holds the file shared for a moment, and an exclusive open fails
    // while it does.
    static async Task<FileStream> HoldExclusivelyAsync(string path) {
        for (var attempt = 0;; attempt++) {
            try {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) when (attempt < 200) {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>An exclusive holder makes every shared open fail with an IOException, as a failing disk would. Neither
    /// read can say what is left, so only a mark stands between those lines and silence.</summary>
    [Test]
    public async Task A_final_read_that_fails_twice_marks_the_source_for_import() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        using var unreadable = await HoldExclusivelyAsync(source.TranscriptPath);
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => finish.IsCompleted, "the finish", TimeSpan.FromSeconds(1));

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(spool.HasBacklog(source.SessionId)).IsFalse();
        await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
    }

    /// <summary>The same two failures, reached past a third: the read that decides whether anything needs sending.</summary>
    [Test]
    public async Task A_read_that_fails_below_the_threshold_marks_the_source_for_import() {
        var source = Source(Tmp.CreateFile("t.jsonl", "{\"type\":\"system\"}\n"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeDrain[source.Key] = () => {
            entered.TrySetResult();

            return release.Task;
        };
        var host   = NewHost();

        try {
            host.TryAdd(source);
            await entered.Task.WithinDeadlineAsync("the recorder reaching its first drain");
            await File.AppendAllTextAsync(source.TranscriptPath, $"{Prompt}\n{Reply}\n");

            using var unreadable = await HoldExclusivelyAsync(source.TranscriptPath);
            var finish = host.FinishAsync(source.Key, CancellationToken.None);
            release.SetResult();
            await Eventually(() => finish.IsCompleted, "the finish", TimeSpan.FromSeconds(1));
            await finish;

            await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
        } finally {
            release.TrySetResult();
        }
    }

    /// <summary>A file pending delete on Windows refuses a read this way, and a refusal says no more about what is left
    /// than a failed read does.</summary>
    [Test]
    public async Task A_read_refused_below_the_threshold_marks_the_source_for_import() {
        Skip.Unless(!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess, "needs POSIX permissions and an unprivileged user");
        if (OperatingSystem.IsWindows()) return;

        var source = Source(Tmp.CreateFile("t.jsonl", "{\"type\":\"system\"}\n"));
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => Ticks(source.Key) >= 1, "the first tick");
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Prompt}\n{Reply}\n");

        File.SetUnixFileMode(source.TranscriptPath, UnixFileMode.None);
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => finish.IsCompleted, "the finish", TimeSpan.FromSeconds(1));

        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
    }

    /// <summary>The drain goes on to its completion signal, so a refused read costs the server no wait, and a watcher
    /// reaches its session end.</summary>
    [Test]
    public async Task A_final_read_that_is_refused_marks_the_source_and_signals_the_drain() {
        Skip.Unless(!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess, "needs POSIX permissions and an unprivileged user");
        if (OperatingSystem.IsWindows()) return;

        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");

        File.SetUnixFileMode(source.TranscriptPath, UnixFileMode.None);
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => finish.IsCompleted, "the finish", TimeSpan.FromSeconds(1));

        await Assert.That(new TranscriptSpool(Config, _time, "http://127.0.0.1:1").NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>The spool's directory is a file here, so no marker can be written. A host runs many recorders in
    /// a process whose stderr is nobody's, and the only sign of a lost marker has to reach this source's log.</summary>
    [Test]
    public async Task A_recorder_reports_a_failed_marker_write_to_its_own_log() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{{\"type\":\"assistant"), "a1");
        Tmp.CreateFile("config/transcript-spool", "");
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its whole line");
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => finish.IsCompleted, "the finish", TimeSpan.FromSeconds(1));

        await Assert.That(_log.Any(line => line.Contains("failed to write needs-import marker", StringComparison.Ordinal))).IsTrue();
        await Assert.That(_log.Any(line => line.Contains($"Could not mark {source.Key} for import", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_recorder_reports_a_failed_quarantine_to_its_own_log() {
        var source = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "cursor", null);
        Tmp.CreateFile("config/cursor-quarantine", "");
        _transport.ResumeFrom = 99;
        var host = NewHost();

        host.TryAdd(source);

        await Eventually(() => _log.Any(line => line.Contains("cursor_quarantine_marker_write_failed", StringComparison.Ordinal)), "the failed write in its log");
    }

    /// <summary>A backoff has no recorder to hear the stop. The server waits 10 seconds for a drain against a backoff
    /// of up to 30, so the clock stands still here: only the stop can end the wait.</summary>
    [Test]
    public async Task A_server_stop_during_a_backoff_drains_at_once() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        await _transport.RaiseStopRequestedAsync(source.Key, "the server asked");

        await Eventually(() => host.ActiveKeys.Count == 0, "the drain");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(2);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(_transport.Connects).IsEqualTo(1);
    }

    [Test]
    public async Task A_server_stop_for_another_key_leaves_a_backoff_alone() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        await _transport.RaiseStopRequestedAsync("another-key", "the server asked");

        await Eventually(() => LinesSent(source.SessionId) == 1, "the source to record after its backoff", TimeSpan.FromSeconds(1));
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        await Eventually(() => LinesSent(source.SessionId) == 2, "the source to go on recording", TimeSpan.FromSeconds(1));

        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>The raiser is a transport's dispatch, which every other source's stop waits behind, so what a stop sets
    /// off has to run elsewhere. A callback on the send's token is the work that would land there: a delay's and a
    /// <c>WaitAsync</c>'s continuations are queued by the runtime, so those can't show it.</summary>
    [Test]
    public async Task A_server_stop_does_not_run_on_the_transports_dispatch() {
        var source  = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        _transport.OnSend = ct => {
            ct.Register(release.Wait);
            entered.TrySetResult();

            return Task.Delay(Timeout.Infinite, ct);
        };
        await File.AppendAllTextAsync(source.TranscriptPath, $"{Reply}\n");
        await Eventually(() => entered.Task.IsCompleted, "the send", TimeSpan.FromSeconds(1));
        _transport.OnSend = null;

        try {
            await Task.Run(() => _transport.RaiseStopRequestedAsync(source.Key, "the server asked")).WithinDeadlineAsync("the stop to return while its work blocks");
        } finally {
            release.Set();
        }

        await Eventually(() => host.ActiveKeys.Count == 0, "the drain");
    }

    /// <summary>The server stopped this source and won't wait long, so a title still generating is handed off, as a
    /// running recorder told to stop hands off its own.</summary>
    [Test]
    public async Task A_drain_after_a_server_stop_does_not_wait_out_a_title() {
        var source  = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "claude", null);
        var handed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<ClaudeCliResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _titles[source.Key]    = _ => blocked.Task;
        _handOffs[source.Key]  = () => handed.TrySetResult();
        _transport.ConnectFailures = 1;
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _ended.Count == 1, "the failed attempt");
        await _transport.RaiseStopRequestedAsync(source.Key, "the server asked");

        await Eventually(() => handed.Task.IsCompleted, "the hand-off", TimeSpan.FromSeconds(1));
        await Eventually(() => host.ActiveKeys.Count == 0, "the drain");
    }

    /// <summary>A hook asking for the finish has only seconds of its own, so a hurried finish doesn't wait out a title
    /// either, on a recorder still tailing as much as on one in a backoff.</summary>
    [Test]
    public async Task A_hurried_finish_does_not_wait_out_a_title() {
        var source  = new RecordingSource(Guid.NewGuid().ToString("N"), null, Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "claude", null);
        var handed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<ClaudeCliResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _titles[source.Key]   = _ => blocked.Task;
        _handOffs[source.Key] = () => handed.TrySetResult();
        var host = NewHost();

        host.TryAdd(source);
        await Eventually(() => _transport.Connects == 1, "the connect", TimeSpan.FromSeconds(1));
        await Eventually(() => LinesSent(source.SessionId) == 2, "the lines", TimeSpan.FromSeconds(1));
        var finish = host.FinishAsync(source.Key, CancellationToken.None, hurried: true);

        await Eventually(() => handed.Task.IsCompleted, "the hand-off", TimeSpan.FromSeconds(1));
        await finish.WithinDeadlineAsync("the finish");
    }

    /// <summary>The stop lands after the failed recorder has gone and before the backoff's wait begins, where no
    /// recorder is listening and a stop heard only during the wait would be lost.</summary>
    [Test]
    public async Task A_server_stop_between_attempts_is_not_lost() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var host = NewHost(attemptEnded: ended => {
            if (!ended.SourceEnded) _transport.RaiseStopRequestedAsync(source.Key, "the server asked").GetAwaiter().GetResult();
        });

        host.TryAdd(source);

        await Eventually(() => host.ActiveKeys.Count == 0, "the drain");

        await Assert.That(LinesSent(source.SessionId)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_factory_that_throws_in_TryAdd_adds_nothing() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"));
        var host   = NewHost(factoryHook: kind => throw new InvalidOperationException($"no {kind}"));

        await Assert.That(() => host.TryAdd(source)).Throws<InvalidOperationException>();

        await Assert.That(host.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task A_factory_that_throws_on_a_retry_counts_as_a_failed_attempt() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"), "a1");
        _transport.ConnectFailures = 1;
        var contexts = 0;
        var host = NewHost(factoryHook: kind => {
            if (kind == "context" && Interlocked.Increment(ref contexts) == 2) throw new InvalidOperationException("no context");
        });

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "the source to record past the failed factory", TimeSpan.FromSeconds(1));

        await Assert.That(_log.Any(line => line.Contains("no context", StringComparison.Ordinal))).IsTrue();
        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task Finishing_a_source_leaves_the_others_running() {
        var sessionId = Guid.NewGuid().ToString("N");
        var parent    = Source(Tmp.CreateFile("parent.jsonl", $"{Prompt}\n{Reply}\n"), sessionId: sessionId);
        var child     = Source(Tmp.CreateFile("child.jsonl", $"{Reply}\n"), "a1", sessionId);
        var host      = NewHost();

        host.TryAdd(parent);
        host.TryAdd(child);
        await Eventually(() => LinesSent(sessionId) == 3, "both sources' lines");

        await host.FinishAsync(child.Key, CancellationToken.None).WithinDeadlineAsync("finishing the child");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([parent.Key]);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);

        await File.AppendAllTextAsync(parent.TranscriptPath, $"{Later}\n");
        await Eventually(() => LinesSent(sessionId) == 4, "the parent still recording", TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Finishing_a_key_nothing_records_is_a_no_op() {
        await NewHost().FinishAsync("nothing", CancellationToken.None).WithinDeadlineAsync("finishing an unknown key");
    }

    [Test]
    public async Task Adding_a_key_twice_is_refused() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"));
        var host   = NewHost();

        await Assert.That(host.TryAdd(source)).IsTrue();
        await Assert.That(host.TryAdd(source)).IsFalse();

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_stop_for_one_source_ends_only_that_source() {
        var stopped = Source(Tmp.CreateFile("stopped.jsonl", $"{Prompt}\n"), "a1");
        var kept    = Source(Tmp.CreateFile("kept.jsonl", $"{Prompt}\n"), "a2");
        var host    = NewHost();

        host.TryAdd(stopped);
        host.TryAdd(kept);
        await Eventually(() => _transport.Connects == 2, "both sources to register");

        await _transport.RaiseStopRequestedAsync(stopped.Key, "the server asked");
        await Eventually(() => !host.ActiveKeys.Contains(stopped.Key), "the stopped source to leave");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([kept.Key]);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_source_that_goes_idle_leaves_and_stops_listening() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        await Eventually(() => !host.ActiveKeys.Contains(source.Key), "the idle end", TimeSpan.FromHours(7));
        await _transport.RaiseReconnectedAsync();

        await Assert.That(_transport.Connects).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_handler_running_after_its_source_ended_does_not_starve_the_next(bool reconnect) {
        var first  = Source(Tmp.CreateFile("first.jsonl", $"{Prompt}\n"), "a1");
        var second = Source(Tmp.CreateFile("second.jsonl", $"{Prompt}\n"), "a2");
        var host   = NewHost();

        host.TryAdd(first);
        await Eventually(() => _transport.Connects == 1, "the first source to register");

        // Raised to a snapshot of the handlers, as the hub does, so the second's handler runs after it has ended.
        Task Probe() => host.FinishAsync(second.Key, CancellationToken.None);
        _transport.StopRequested += (key, _) => key == second.Key ? Probe() : Task.CompletedTask;
        _transport.Reconnected   += Probe;

        host.TryAdd(second);
        await Eventually(() => _transport.Connects == 2, "the second source to register");

        var lastRan = 0;
        _transport.StopRequested += (_, _) => Task.FromResult(Interlocked.Increment(ref lastRan));
        _transport.Reconnected   += () => Task.FromResult(Interlocked.Increment(ref lastRan));

        if (reconnect) await _transport.RaiseReconnectedAsync();
        else await _transport.RaiseStopRequestedAsync(second.Key, "the server asked");

        await Assert.That(lastRan).IsEqualTo(1);
        await Assert.That(host.ActiveKeys).IsEquivalentTo([first.Key]);
    }

    [Test]
    public async Task Fifty_idle_sources_read_a_bounded_amount_on_a_tick() {
        const int Sources = 50;
        const int IdleTicks = 10;
        const int PerTickBudget = 8 * 1024;
        const int TailCompare = 3000;

        var read = new ConcurrentDictionary<string, long>();

        var body    = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 1500) + "\"}]}}\n";
        var sources = Enumerable.Range(0, Sources)
            .Select(i => i == 0
                ? Source(Tmp.CreateFile("s0.jsonl", Prompt + "\n" + string.Concat(Enumerable.Repeat(body, 59))))
                : Source(Tmp.CreateFile($"s{i}.jsonl", string.Concat(Enumerable.Repeat(body, 60))), $"a{i}"))
            .ToList();
        var host = NewHost(path => new CountingFileStream(path, n => read.AddOrUpdate(path, n, (_, total) => total + n)));

        foreach (var source in sources) host.TryAdd(source);

        await Eventually(() => sources.All(source => LinesSent(source.SessionId) == 60), "every source's first read");
        await Eventually(() => sources.All(source => Ticks(source.Key) >= 2), "a tick past the first read", TimeSpan.FromSeconds(1));

        var ticksBefore = sources.ToDictionary(source => source.Key, source => Ticks(source.Key));
        var bytesBefore = sources.ToDictionary(source => source.Key, source => read[source.TranscriptPath]);

        await Eventually(() => sources.All(source => Ticks(source.Key) >= ticksBefore[source.Key] + IdleTicks), "idle ticks", TimeSpan.FromSeconds(1));

        // A read in flight at either count can land on either side of it, so the window is off by up to two reads.
        var perTick = sources.Select(source => (double)(read[source.TranscriptPath] - bytesBefore[source.Key]) / (Ticks(source.Key) - ticksBefore[source.Key])).ToList();

        await Assert.That(new FileInfo(sources[1].TranscriptPath).Length).IsGreaterThan(90_000);
        await Assert.That(perTick.Max()).IsLessThan(PerTickBudget);
        await Assert.That(perTick.Min()).IsGreaterThan(TailCompare);
        await Assert.That(host.ActiveKeys.Count).IsEqualTo(Sources);
    }

    /// <summary>A fake clock that remembers the timers armed on it and how many connects had been tried by then.</summary>
    sealed class RecordingTime(DateTimeOffset start) : FakeTimeProvider(start) {
        readonly ConcurrentQueue<(int Attempts, TimeSpan Due)> _timers = new();

        public Func<int> Attempts { get; set; } = () => 0;

        public IReadOnlyList<(int Attempts, TimeSpan Due)> Timers => [.._timers];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            _timers.Enqueue((Attempts(), dueTime));

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
