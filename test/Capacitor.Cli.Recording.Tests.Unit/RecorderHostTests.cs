using System.Collections.Concurrent;
using Capacitor.Cli.Core;
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

    public RecorderHostTests() => _time.Attempts = () => _transport.ConnectAttempts;

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    RecorderClaims Claims => field ??= new(Tmp.CreateDir("watchers"), _time);

    RecorderHost NewHost(Func<string, FileStream>? open = null, Action<string>? factoryHook = null, RecorderClaims? claims = null) => new(
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
            }, () => {
                if (_beatThrows.ContainsKey(source.Key)) throw new InvalidOperationException("the beat is broken");

                _beats.AddOrUpdate(source.Key, 1, (_, n) => n + 1);
            });
        },
        _time, open, claims);

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
        await Eventually(() => _transport.ConnectAttempts == 1, "the failed attempt");
        _transport.ConnectHangs = true;
        var finish = host.FinishAsync(source.Key, CancellationToken.None);
        await Eventually(() => _transport.ConnectAttempts == 2, "the final drain's connect");

        await Eventually(() => finish.IsCompleted, "the connect to time out", TranscriptRecorder.DrainOnceConnectBudget);
        await finish;

        var spool = new TranscriptSpool(Config, _time, "http://127.0.0.1:1");
        await Assert.That(spool.NeedsImport(source.SessionId)).IsTrue();
        await Assert.That(host.ActiveKeys).IsEmpty();
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
