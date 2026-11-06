using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class TranscriptRecorderTests {
    const string Prompt = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply  = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";
    const string Later  = """{"type":"assistant","message":{"content":[{"type":"text","text":"And more."}]}}""";

    static readonly string Sid = Guid.NewGuid().ToString("N");

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeRecorderTransport _transport = new() { IsConnected = true };
    readonly FakeRecorderVendor    _vendor    = new();

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    TranscriptRecorder Recorder(string path, string? agentId = null) => new(
        new RecordingSource(Sid, agentId, path, "claude", null), _transport, new NoStamps(), TimeProvider.System,
        new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)), _vendor, TimeProvider.System, _ => { }, () => { }));

    async Task LinesSent(int count) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (_transport.Batches.Sum(batch => batch.Lines.Length) < count) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{count} line(s) never reached the transport");

            await Task.Delay(10);
        }
    }

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
        await Assert.That(_vendor.Prepared).IsEqualTo(1);
        await Assert.That(_vendor.Connected).IsEqualTo(1);
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
