using System.Collections.Concurrent;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Transcripts;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class TranscriptRecorderTests {
    const string Prompt = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply  = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";
    const string Later  = """{"type":"assistant","message":{"content":[{"type":"text","text":"And more."}]}}""";

    static readonly string Sid = Guid.NewGuid().ToString("N");

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeRecorderTransport _transport = new() { IsConnected = true };
    readonly FakeRecorderHousekeeping _housekeeping = new();

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    TranscriptRecorder Recorder(
            string path, string? agentId = null, string vendor = "claude", RecorderClaims? claims = null, FakeRecorderTransport? transport = null) => new(
        new RecordingSource(Sid, agentId, path, vendor, null), transport ?? _transport, new NoStamps(), TimeProvider.System,
        new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)), _housekeeping, TimeProvider.System, _ => { }, () => { }), claims);

    RecorderClaims Claims => field ??= new(Tmp.CreateDir("watchers"), TimeProvider.System);

    async Task LinesSent(int count) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (_transport.Batches.Sum(batch => batch.Lines.Length) < count) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{count} line(s) never reached the transport");

            await Task.Delay(10);
        }
    }

    static async Task CanonicalLinesSent(FakeRecorderTransport transport, int count) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (transport.CanonicalBatches.Sum(batch => batch.Lines.Length) < count) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{count} line(s) never reached the transport as canonical batches");

            await Task.Delay(10);
        }
    }

    static string Uuid(int n) => $"20000000-0000-4000-8000-{n:D12}";

    static string Parent(int n) => n < 0 ? "null" : $"\"{Uuid(n)}\"";

    static string User(int n, int parent) =>
        $$$"""{"type":"user","uuid":"{{{Uuid(n)}}}","parentUuid":{{{Parent(parent)}}},"timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"role":"user","content":"prompt {{{n}}}"}}""";

    static string Assistant(int n, int parent, int usage) =>
        $$$$"""{"type":"assistant","uuid":"{{{{Uuid(n)}}}}","parentUuid":{{{{Parent(parent)}}}},"timestamp":"2026-11-16T08:00:{{{{n:D2}}}}Z","message":{"model":"claude-x","content":[{"type":"text","text":"reply {{{{n}}}}"}],"usage":{"input_tokens":{{{{usage}}}},"output_tokens":1}}}""";

    /// Prompts and replies taking turns, except that line 30 repeats line 29's usage, an echo only to a projection that
    /// saw line 29, and line 43 is a sibling of the unanswered prompt on line 42, which it retracts.
    static readonly string[] Session = [
        .. Enumerable.Range(0, 60).Select(n => n switch {
            30                => Assistant(30, parent: 29, usage: 29),
            43                => User(43, parent: 41),
            _ when n % 2 == 0 => User(n, parent: n - 1),
            _                 => Assistant(n, parent: n - 1, usage: n),
        }),
    ];

    static string Jsonl(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + "\n"));

    static string[] Serialized(IEnumerable<CanonicalBatchEvent> events) =>
        [.. events.Select(e => JsonSerializer.Serialize(e, CapacitorJsonContext.Default.CanonicalBatchEvent))];

    static string[] EventsSent(FakeRecorderTransport transport) => Serialized(transport.CanonicalBatches.SelectMany(batch => batch.Events));

    /// What one route projecting the whole transcript from its first line sends.
    static string[] Projected(string[] lines) =>
        Serialized(CanonicalRoute.For("claude", Sid, null, serverSupports: true)!.Encode(
            new TranscriptBatch { SessionId = Sid, Vendor = "claude", Lines = lines, LineNumbers = [.. Enumerable.Range(0, lines.Length)] }, DateTimeOffset.UtcNow).Events);

    [Test]
    public async Task A_recorder_runs_against_a_fake_transport() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);

        await File.AppendAllTextAsync(path, $"\n{Later}\n");
        await stop.CancelAsync();
        await run;
        await recorder.FinishAsync(CancellationToken.None);

        var lastBatch = _transport.Batches[^1];
        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([0, 1, 3]);
        await Assert.That(_transport.Batches.SelectMany(batch => batch.Lines).ToArray()).IsEquivalentTo([Prompt, Reply, Later]);
        await Assert.That(lastBatch.SessionId).IsEqualTo(Sid);
        await Assert.That(recorder.State.LinesProcessed).IsEqualTo(4);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(_housekeeping.Prepared).IsEqualTo(1);
        await Assert.That(_housekeeping.Connected).IsEqualTo(1);
    }

    /// <summary>Registering would replace the watcher that took the source: the server keeps the newest connection.</summary>
    [Test]
    public async Task A_recorder_whose_claim_has_gone_never_registers() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path, "a1", claims: Claims);

        await recorder.RunAsync(CancellationToken.None).WithinDeadlineAsync("the recorder to stop");

        await Assert.That(recorder.LostClaim).IsTrue();
        await Assert.That(recorder.Stopping.IsCancellationRequested).IsTrue();
        await Assert.That(_transport.ConnectAttempts).IsEqualTo(0);
    }

    [Test]
    public async Task A_drain_once_whose_claim_has_gone_sends_nothing() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path, "a1", claims: Claims);

        await recorder.DrainOnceAsync(CancellationToken.None).WithinDeadlineAsync("the drain");

        await Assert.That(recorder.LostClaim).IsTrue();
        await Assert.That(_transport.ConnectAttempts).IsEqualTo(0);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>A stop can reach the loop before its next tick does, so the final drain looks for itself.</summary>
    [Test]
    public async Task A_stop_that_beats_the_tick_still_sends_no_final_drain() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        var key  = $"{Sid}-a1";
        Claims.TryTake(key, "daemon", isHosted: false);
        using var recorder = Recorder(path, "a1", claims: Claims);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);
        Claims.Release(key);
        await stop.CancelAsync();
        await run.WithinDeadlineAsync("the recorder to stop");

        await File.AppendAllTextAsync(path, $"{Reply}\n");
        await recorder.FinishAsync(CancellationToken.None).WithinDeadlineAsync("the finish");

        await Assert.That(recorder.LostClaim).IsTrue();
        await Assert.That(_transport.Batches.Sum(batch => batch.Lines.Length)).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>A transport reconnects as a laptop wakes, and registering again would take the server's slot from the
    /// watcher already running.</summary>
    [Test]
    public async Task A_reconnect_after_the_claim_has_gone_does_not_register_again() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        var key  = $"{Sid}-a1";
        Claims.TryTake(key, "daemon", isHosted: false);
        using var recorder = Recorder(path, "a1", claims: Claims);

        var run = recorder.RunAsync(CancellationToken.None);
        await LinesSent(1);
        Claims.Release(key);
        await _transport.RaiseReconnectedAsync();

        await Assert.That(_transport.ConnectAttempts).IsEqualTo(1);
        await run.WithinDeadlineAsync("the recorder to stop");
        await Assert.That(recorder.LostClaim).IsTrue();
    }

    /// <summary>Stale is only what a hook may act on. Until one does, the source is still this recorder's, as it is
    /// for a daemon that woke up first.</summary>
    [Test]
    public async Task A_stale_claim_nobody_took_keeps_its_recorder_sending() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        var key  = $"{Sid}-a1";
        Claims.TryTake(key, "daemon", isHosted: false);
        File.SetLastWriteTimeUtc(Claims.ClaimFile(key), DateTime.UtcNow - TimeSpan.FromHours(1));
        await Assert.That(Claims.Read(key)).IsEqualTo(RecorderClaimState.DaemonStale);
        using var recorder = Recorder(path, "a1", claims: Claims);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);
        await stop.CancelAsync();
        await run.WithinDeadlineAsync("the recorder to stop");

        await Assert.That(recorder.LostClaim).IsFalse();
    }

    [Test]
    public async Task A_recorder_resumes_from_the_line_the_server_names() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n");
        _transport.ResumeFrom = 1;
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([1]);
    }

    [Test]
    public async Task A_stop_names_the_source_it_is_for() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path, agentId: "agent-1");

        var run = recorder.RunAsync(CancellationToken.None);
        await LinesSent(1);

        await _transport.RaiseStopRequestedAsync(Sid, "for the parent");
        await Assert.That(run.IsCompleted).IsFalse();

        await _transport.RaiseStopRequestedAsync($"{Sid}-agent-1", "for this one");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Holds the recorder in a vendor call that takes no token, as a repository probe is, until
    /// <paramref name="whileHeld"/> has run. Returns whether the stopping token was cancelled by then, with the
    /// vendor call still blocked.</summary>
    async Task<bool> StoppingWhileAVendorCallIsBlocked(TranscriptRecorder recorder, Task run, Func<Task> whileHeld) {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _housekeeping.BeforeDrain = () => {
            entered.TrySetResult();
            return release.Task;
        };

        await entered.Task.WithinDeadlineAsync("the recorder entering its vendor call");
        await whileHeld();

        var stopping = recorder.Stopping.IsCancellationRequested;
        var blocked  = !run.IsCompleted;

        release.SetResult();
        await run.WithinDeadlineAsync("the run after the vendor call returned");
        await Assert.That(blocked).IsTrue();

        return stopping;
    }

    [Test]
    public async Task A_stop_reaches_the_stopping_token_while_a_vendor_call_is_blocked() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path);

        var run = recorder.RunAsync(CancellationToken.None);

        var stopping = await StoppingWhileAVendorCallIsBlocked(
            recorder, run, () => _transport.RaiseStopRequestedAsync(Sid, "the session ended"));

        await Assert.That(stopping).IsTrue();
    }

    [Test]
    public async Task A_refused_reconnect_reaches_the_stopping_token_while_a_vendor_call_is_blocked() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n{Later}\n");
        using var recorder = Recorder(path, vendor: "cursor");

        var run = recorder.RunAsync(CancellationToken.None);
        await LinesSent(3);

        var stopping = await StoppingWhileAVendorCallIsBlocked(recorder, run, async () => {
            await File.WriteAllTextAsync(path, $"{Prompt}\n");
            _transport.ResumeFrom = 2;
            await _transport.RaiseReconnectedAsync();
        });

        await Assert.That(stopping).IsTrue();
    }

    /// <summary>A watcher has one connection and no retry around it: it carries on, and the next reconnect tries again.</summary>
    [Test]
    public async Task A_bare_recorder_outlives_a_failed_re_registration() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = new TranscriptRecorder(
            new RecordingSource(Sid, null, path, "claude", null), _transport, new NoStamps(), TimeProvider.System,
            new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)), _housekeeping, TimeProvider.System, _ => { }, () => { }));
        using var stop = new CancellationTokenSource();
        var run = recorder.RunAsync(stop.Token);
        await Eventually(() => _transport.Connects == 1);

        _transport.ConnectFailures = 1;
        await _transport.RaiseReconnectedAsync();
        await _transport.RaiseReconnectedAsync();

        await Eventually(() => _transport.Connects == 2);
        await Assert.That(run.IsCompleted).IsFalse();

        await stop.CancelAsync();
        await run.WithinDeadlineAsync("the run");
    }

    [Test]
    public async Task A_log_that_throws_does_not_reach_the_transports_other_subscribers() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        var broken = new bool[1];
        using var recorder = new TranscriptRecorder(
            new RecordingSource(Sid, null, path, "claude", null), _transport, new NoStamps(), TimeProvider.System,
            new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)), _housekeeping, TimeProvider.System,
                _ => {
                    if (Volatile.Read(ref broken[0])) throw new InvalidOperationException("the log is broken");
                }, () => { }));
        var lastRan = 0;
        _transport.StopRequested += (_, _) => Task.FromResult(Interlocked.Increment(ref lastRan));
        _transport.Reconnected   += () => Task.FromResult(Interlocked.Increment(ref lastRan));

        // The broken log fails the run too, which is not what this test is about.
        var run = recorder.RunAsync(CancellationToken.None).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        await Eventually(() => _transport.Connects == 1);
        Volatile.Write(ref broken[0], true);
        _transport.ConnectFailures = 1;

        await _transport.RaiseReconnectedAsync();
        await _transport.RaiseStopRequestedAsync(Sid, "the server asked");

        await Assert.That(lastRan).IsEqualTo(2);
        await run.WithinDeadlineAsync("the run");
    }

    static async Task Eventually(Func<bool> condition) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition()) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("the condition never held");

            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Disposing_a_recorder_cancels_the_title_work_it_started() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        var recorder = Recorder(path);
        using var stop = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);
        await stop.CancelAsync();
        await run;
        var titles = recorder.State.TitleCancellation;
        recorder.Dispose();

        await Assert.That(titles.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task A_reconnect_re_registers_and_rewinds_to_the_server() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);

        _transport.ResumeFrom = 1;
        await _transport.RaiseReconnectedAsync();
        await LinesSent(3);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Connects).IsEqualTo(2);
        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([0, 1, 1]);
    }

    string SentLines => string.Join(",", _transport.Batches.SelectMany(batch => batch.LineNumbers!));

    /// <summary>The server drops a line at or below one it already took, so a line sent ahead of the server's answer
    /// would push the resent gap under it.</summary>
    [Test]
    public async Task A_line_written_while_re_registering_waits_for_the_servers_answer() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);

        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ConnectHold = answer.Task;
        _transport.ResumeFrom  = 1;
        var reconnect = _transport.RaiseReconnectedAsync();
        await Eventually(() => _transport.ConnectsHeld == 1);

        await File.AppendAllTextAsync(path, $"{Later}\n");
        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _housekeeping.BeforeDrain = () => {
            ticked.TrySetResult();
            return Task.CompletedTask;
        };
        await ticked.Task.WithinDeadlineAsync("a drain after the line was written");
        // Long enough for a drain that is not held to have read and sent the line.
        await Task.Delay(200);
        var sentBeforeTheAnswer = SentLines;

        answer.SetResult();
        await reconnect.WithinDeadlineAsync("the re-registration");
        await LinesSent(4);
        await stop.CancelAsync();
        await run;

        await Assert.That(sentBeforeTheAnswer).IsEqualTo("0,1");
        await Assert.That(SentLines).IsEqualTo("0,1,1,2");
    }

    /// <summary>A drain that read the cursor before the rewind would write its own frontier over it, and the gap the
    /// server asked for would never be resent.</summary>
    [Test]
    public async Task A_rewind_waits_for_the_send_in_flight_and_is_kept() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);

        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var landed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.OnSend = _ => {
            sending.TrySetResult();
            return landed.Task;
        };

        await File.AppendAllTextAsync(path, $"{Later}\n");
        await sending.Task.WithinDeadlineAsync("the drain to send the new line");

        _transport.ResumeFrom = 1;
        var reconnect = _transport.RaiseReconnectedAsync();
        await Task.Delay(200);
        var registeredUnderTheSend = _transport.ConnectAttempts;

        _transport.OnSend = null;
        landed.SetResult();
        await reconnect.WithinDeadlineAsync("the re-registration");
        await LinesSent(5);
        await stop.CancelAsync();
        await run;

        await Assert.That(registeredUnderTheSend).IsEqualTo(1);
        await Assert.That(SentLines).IsEqualTo("0,1,2,1,2");
    }

    [Test]
    public async Task A_finished_recorder_stops_listening_to_its_transport() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);
        await stop.CancelAsync();
        await run;
        await recorder.FinishAsync(CancellationToken.None);

        await _transport.RaiseReconnectedAsync();

        await Assert.That(_transport.Connects).IsEqualTo(1);
    }

    [Test]
    public async Task Lines_the_final_drain_cannot_send_go_to_the_spool() {
        var path = Tmp.CreateFile("t.jsonl", $"{Prompt}\n");
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(1);

        _transport.IsConnected = false;
        await File.AppendAllTextAsync(path, $"{Reply}\n");
        await stop.CancelAsync();
        await run;
        await recorder.FinishAsync(CancellationToken.None);

        var spool = new TranscriptSpool(Config, TimeProvider.System, "http://127.0.0.1:1");
        await Assert.That(Directory.GetFiles(spool.DirectoryPath, "*.transcript.jsonl").Length).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(0);
    }

    [Test]
    public async Task A_claude_source_sends_canonical_batches_when_the_server_supports_them() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        _transport.SupportsCanonical = true;
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await CanonicalLinesSent(_transport, 2);
        await stop.CancelAsync();
        await run;

        var batch = _transport.CanonicalBatches.Single();
        await Assert.That(batch.SessionId).IsEqualTo(Sid);
        await Assert.That(batch.Lines.Select(line => line.Number)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(batch.Events.Select(e => e.EventId)).IsEquivalentTo([Guid.Parse(Uuid(0)), Guid.Parse(Uuid(1))], CollectionOrdering.Matching);
        await Assert.That(_transport.Batches).IsEmpty();
        await Assert.That(recorder.State.LinesProcessed).IsEqualTo(2);
    }

    [Test]
    public async Task A_claude_source_sends_raw_lines_when_the_server_does_not_support_canonical_batches() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches.SelectMany(batch => batch.Lines)).IsEquivalentTo(Session[..2], CollectionOrdering.Matching);
        await Assert.That(_transport.CanonicalBatches).IsEmpty();
        await Assert.That(_transport.CapabilityAsks).IsEqualTo(1);
    }

    /// <summary>The server is not even asked: a vendor outside the canonical list sends what it always has.</summary>
    [Test]
    public async Task A_codex_source_sends_raw_lines_whatever_the_server_supports() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        _transport.SupportsCanonical = true;
        using var recorder = Recorder(path, agentId: "a1", vendor: "codex");
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches.SelectMany(batch => batch.Lines)).IsEquivalentTo(Session[..2], CollectionOrdering.Matching);
        await Assert.That(_transport.CanonicalBatches).IsEmpty();
        await Assert.That(_transport.CapabilityAsks).IsEqualTo(0);
    }

    [Test]
    public async Task An_unsupported_reply_resends_the_chunk_raw_and_stays_raw_until_reconnect() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        _transport.SupportsCanonical = true;
        _transport.CanonicalRefusals = 1;
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);
        await File.AppendAllTextAsync(path, Jsonl(Session[2..3]));
        await LinesSent(3);
        var canonicalBeforeReconnect = _transport.CanonicalBatches.Count;

        _transport.ResumeFrom = 3;
        await _transport.RaiseReconnectedAsync();
        await File.AppendAllTextAsync(path, Jsonl(Session[3..4]));
        await CanonicalLinesSent(_transport, 1);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches[0].Lines).IsEquivalentTo(Session[..2], CollectionOrdering.Matching);
        await Assert.That(_transport.Batches[0].LineNumbers!).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
        await Assert.That(canonicalBeforeReconnect).IsEqualTo(0);
        await Assert.That(EventsSent(_transport)).IsEquivalentTo(Projected(Session[..4])[3..], CollectionOrdering.Matching);
        await Assert.That(_transport.CapabilityAsks).IsEqualTo(2);
    }

    /// <summary>Records <paramref name="lines"/> twice: once by one recorder, written in two halves, and once by a recorder
    /// stopped after the first half and replaced by a fresh one resuming there.</summary>
    async Task<(FakeRecorderTransport Whole, FakeRecorderTransport Restarted)> RecordWholeAndRestartedAsync(string[] lines, int restartAt) {
        var whole     = new FakeRecorderTransport { IsConnected = true, SupportsCanonical = true };
        var wholePath = Tmp.CreateFile("whole.jsonl", Jsonl(lines[..restartAt]));

        using (var recorder = Recorder(wholePath, transport: whole)) {
            using var stop = new CancellationTokenSource();
            var       run  = recorder.RunAsync(stop.Token);
            await CanonicalLinesSent(whole, restartAt);
            await File.AppendAllTextAsync(wholePath, Jsonl(lines[restartAt..]));
            await CanonicalLinesSent(whole, lines.Length);
            await stop.CancelAsync();
            await run;
        }

        var restarted     = new FakeRecorderTransport { IsConnected = true, SupportsCanonical = true };
        var restartedPath = Tmp.CreateFile("restarted.jsonl", Jsonl(lines[..restartAt]));

        using (var first = Recorder(restartedPath, transport: restarted)) {
            using var stop = new CancellationTokenSource();
            var       run  = first.RunAsync(stop.Token);
            await CanonicalLinesSent(restarted, restartAt);
            await stop.CancelAsync();
            await run;
        }

        await File.AppendAllTextAsync(restartedPath, Jsonl(lines[restartAt..]));
        restarted.ResumeFrom = restartAt;

        using (var second = Recorder(restartedPath, transport: restarted)) {
            using var stop = new CancellationTokenSource();
            var       run  = second.RunAsync(stop.Token);
            await CanonicalLinesSent(restarted, lines.Length);
            await stop.CancelAsync();
            await run;
        }

        return (whole, restarted);
    }

    /// <summary>A recorder replaced mid-session projects the lines the server already holds before it sends, so a usage
    /// repeated across the restart stays an echo.</summary>
    [Test]
    public async Task A_recorder_restarted_mid_session_sends_the_events_an_uninterrupted_one_sends() {
        var (whole, restarted) = await RecordWholeAndRestartedAsync(Session, restartAt: 30);

        // Without the lines before the restart, line 30 would carry its usage again and line 43 retract nothing.
        var events = whole.CanonicalBatches.SelectMany(batch => batch.Events).ToArray();
        await Assert.That(events.Single(e => e.Line == 29).Usage).IsNotNull();
        await Assert.That(events.Single(e => e.Line == 30).Usage).IsNull();
        await Assert.That(events.Count(e => e.Type == CanonicalEventTypes.UserMessageRetracted)).IsEqualTo(1);

        await Assert.That(restarted.Batches).IsEmpty();
        await Assert.That(EventsSent(restarted)).IsEquivalentTo(EventsSent(whole), CollectionOrdering.Matching);
    }

    /// <summary>Line 29 is over the record limit, so its capture sent a capture-loss line and line 30's usage, the same as
    /// line 29's, is new. A replay that projected line 29 from the file would make it an echo.</summary>
    [Test]
    public async Task A_recorder_restarted_past_a_line_its_capture_replaced_sends_the_events_an_uninterrupted_one_sends() {
        string[] lines = [
            .. Session[..29],
            Assistant(29, parent: 28, usage: 30).Replace("reply 29", new string('x', CaptureRule.MaxRecordBytes), StringComparison.Ordinal),
            Assistant(30, parent: 29, usage: 30),
            .. Session[31..],
        ];

        var (whole, restarted) = await RecordWholeAndRestartedAsync(lines, restartAt: 30);

        var whole30 = whole.CanonicalBatches.SelectMany(batch => batch.Events).Single(e => e.Line == 30);
        await Assert.That(whole.CanonicalBatches.SelectMany(batch => batch.CaptureLoss ?? []).Single().Line).IsEqualTo(29);
        await Assert.That(whole30.Usage).IsNotNull();

        await Assert.That(restarted.Batches).IsEmpty();
        await Assert.That(EventsSent(restarted)).IsEquivalentTo(EventsSent(whole), CollectionOrdering.Matching);
    }

    /// Line 29 large enough that only the redaction can say how it was captured, with <paramref name="text"/>, and line 30
    /// repeating its usage.
    static string[] WithLargeLine29(string text) => [
        .. Session[..29],
        Assistant(29, parent: 28, usage: 30).Replace("reply 29", text, StringComparison.Ordinal),
        Assistant(30, parent: 29, usage: 30),
        .. Session[31..],
    ];

    /// <summary>The capture sent line 29, so line 30's usage is an echo; the restarted recorder redacts line 29 to learn
    /// that, rather than sending raw.</summary>
    [Test]
    public async Task A_recorder_restarted_past_a_large_line_its_capture_kept_sends_the_events_an_uninterrupted_one_sends() {
        var lines = WithLargeLine29(new string('x', CaptureRule.MaxRecordBytes / CaptureRule.MaxRewriteGrowth));

        await Assert.That(CaptureRule.Of(lines[29])).IsEqualTo(LineCapture.Undecided);

        var (whole, restarted) = await RecordWholeAndRestartedAsync(lines, restartAt: 30);

        await Assert.That(whole.CanonicalBatches.SelectMany(batch => batch.Events).Single(e => e.Line == 30).Usage).IsNull();
        await Assert.That(restarted.Batches).IsEmpty();
        await Assert.That(EventsSent(restarted)).IsEquivalentTo(EventsSent(whole), CollectionOrdering.Matching);
    }

    /// <summary>Redacting line 29's token rewrites it, and its DEL characters, escaped at six bytes each, take it past the
    /// record limit: the capture sent a capture-loss line and line 30's usage is new.</summary>
    [Test]
    public async Task A_recorder_restarted_past_a_large_line_its_capture_replaced_sends_the_events_an_uninterrupted_one_sends() {
        var lines = WithLargeLine29("ghp_abcdefghij1234567890 " + new string((char)0x7F, CaptureRule.MaxRecordBytes / CaptureRule.MaxRewriteGrowth));

        await Assert.That(CaptureRule.Of(lines[29])).IsEqualTo(LineCapture.Undecided);

        var (whole, restarted) = await RecordWholeAndRestartedAsync(lines, restartAt: 30);

        await Assert.That(whole.CanonicalBatches.SelectMany(batch => batch.CaptureLoss ?? []).Single().Line).IsEqualTo(29);
        await Assert.That(whole.CanonicalBatches.SelectMany(batch => batch.Events).Single(e => e.Line == 30).Usage).IsNotNull();
        await Assert.That(restarted.Batches).IsEmpty();
        await Assert.That(EventsSent(restarted)).IsEquivalentTo(EventsSent(whole), CollectionOrdering.Matching);
    }

    /// <summary>The failed send projected lines the server never took. The next poll reads more lines than it did, so only
    /// a projection rebuilt up to the cursor sends what an uninterrupted run sends: here line 30 stays an echo of 29.</summary>
    [Test]
    public async Task A_send_failure_leaves_the_cursor_and_the_next_poll_resends_the_same_events() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..30]));
        _transport.SupportsCanonical = true;
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await CanonicalLinesSent(_transport, 30);

        var grown = 0;
        _transport.OnSend = async ct => {
            if (Interlocked.Exchange(ref grown, 1) == 0) await File.AppendAllTextAsync(path, Jsonl(Session[45..]), ct);
        };
        _transport.CanonicalFailures = 1;
        await File.AppendAllTextAsync(path, Jsonl(Session[30..45]));
        await CanonicalLinesSent(_transport, 60);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.CanonicalBatches.Select(batch => batch.Lines[0].Number)).IsEquivalentTo([0, 30], CollectionOrdering.Matching);
        await Assert.That(EventsSent(_transport)).IsEquivalentTo(Projected(Session), CollectionOrdering.Matching);
        await Assert.That(_transport.Batches).IsEmpty();
        await Assert.That(recorder.State.LinesProcessed).IsEqualTo(60);
    }

    /// <summary>A server that fails every canonical batch costs one rebuild, not one per poll.</summary>
    [Test]
    public async Task A_second_failure_before_the_cursor_moves_sends_raw_until_reconnect() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        _transport.SupportsCanonical = true;
        _transport.CanonicalFailures = 2;
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);

        _transport.ResumeFrom = 2;
        await _transport.RaiseReconnectedAsync();
        await File.AppendAllTextAsync(path, Jsonl(Session[2..3]));
        await CanonicalLinesSent(_transport, 1);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(_transport.CanonicalBatches.Single().Lines.Select(line => line.Number)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    /// <summary>The stop that ends the run here also fails the canonical send in flight, so the final drain needs the lines
    /// before its cursor projected again, inside the seconds before the kill.</summary>
    [Test]
    public async Task A_final_drain_that_cannot_rebuild_its_route_in_time_sends_raw_lines() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        var log  = new ConcurrentQueue<string>();
        _transport.SupportsCanonical = true;
        using var recorder = new TranscriptRecorder(
            new RecordingSource(Sid, null, path, "claude", null), _transport, new NoStamps(), TimeProvider.System,
            new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)), _housekeeping, TimeProvider.System, log.Enqueue, () => { })) {
            FinalDrainReplayBudget = TimeSpan.Zero,
        };
        using var stop = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await CanonicalLinesSent(_transport, 2);

        _transport.OnSend = _ => stop.CancelAsync();
        _transport.CanonicalFailures = 1;
        await File.AppendAllTextAsync(path, Jsonl(Session[2..3]));
        await run.WithinDeadlineAsync("the run to stop");

        _transport.OnSend = null;
        await recorder.FinishAsync(CancellationToken.None);

        await Assert.That(_transport.CanonicalBatches.Single().Lines.Select(line => line.Number)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(log).Contains(line => line.Contains("ran out of time", StringComparison.Ordinal));
    }

    /// <summary>A transcript shorter than the server's cursor cannot give the route the state of the lines before it.</summary>
    [Test]
    public async Task A_resume_whose_earlier_lines_cannot_all_be_replayed_sends_raw_lines() {
        var path = Tmp.CreateFile("t.jsonl", Jsonl(Session[..2]));
        _transport.SupportsCanonical = true;
        _transport.ResumeFrom        = 4;
        var grown = 0;
        _housekeeping.BeforeDrain = async () => {
            if (Interlocked.Exchange(ref grown, 1) == 0) await File.AppendAllTextAsync(path, Jsonl(Session[2..6]));
        };
        using var recorder = Recorder(path);
        using var stop     = new CancellationTokenSource();

        var run = recorder.RunAsync(stop.Token);
        await LinesSent(2);
        await stop.CancelAsync();
        await run;

        await Assert.That(_transport.Batches.SelectMany(batch => batch.LineNumbers!)).IsEquivalentTo([4, 5], CollectionOrdering.Matching);
        await Assert.That(_transport.CanonicalBatches).IsEmpty();
    }

    // Cursor's own sessionStart hook posts (and spawns this very watcher)
    // before any transcript line is ever read, exactly like Antigravity — so the generic
    // below-threshold buffer must not apply to it either.
    [Test]
    [Arguments("antigravity", true)]
    [Arguments("cursor",      true)]
    [Arguments("mistral-vibe", true)]
    [Arguments("grok",        true)]
    [Arguments("muse",        true)]
    [Arguments("codex",       false)]
    [Arguments("claude",      false)]
    [Arguments("gemini",      false)]
    public async Task SkipsThresholdBuffering_matches_only_the_vendors_that_pre_commit_the_session(string vendor, bool expected) =>
        await Assert.That(TranscriptRecorder.SkipsThresholdBuffering(vendor)).IsEqualTo(expected);
}
