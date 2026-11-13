using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>
/// A VS Code Local session's usage, read from its chat log and sent as one usage record per completed request and one
/// credits line per request whose credits rose. The chat log is written seconds
/// after the transcript, so a drain that reads no new transcript line still sends what the log gained.
/// </summary>
public class CopilotLocalUsageTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    TestRecorders Recorders => field ??= new(Config.Root);

    const string DashedSid = "87b11035-b254-437d-b242-49a936315953";
    static readonly string Sid = DashedSid.Replace("-", "");

    static string Request(string id, int state, long? tokens, double? credits, string? model = "gpt-6-luna") {
        var request = new JsonObject { ["requestId"] = id, ["modelState"] = new JsonObject { ["value"] = state } };
        if (tokens is { } t) request["completionTokens"] = t;
        if (credits is { } c) request["copilotCredits"] = c;
        if (model is not null) request["result"] = new JsonObject { ["metadata"] = new JsonObject { ["resolvedModel"] = model } };
        return request.ToJsonString();
    }

    static string Log(params string[] requests) =>
        """{"kind":0,"v":{"version":3,"sessionId":"SID","customTitle":"Create notes.md","requests":[REQUESTS]}}"""
            .Replace("SID", DashedSid).Replace("REQUESTS", string.Join(",", requests)) + "\n";

    static VsCodeChatSession Session(params string[] requests) => VsCodeChatSessionLog.Replay(Log(requests), DashedSid)!;

    string Transcript() {
        Tmp.CreateDir("ws", "chatSessions");
        return Tmp.CreateFile(Path.Combine("ws", "GitHub.copilot-chat", "transcripts", DashedSid + ".jsonl"), [
            """{"type":"session.start","data":{"sessionId":"s"},"id":"00000000-0000-4000-8000-000000000001","timestamp":"2026-11-10T10:00:00Z"}""",
            """{"type":"user.message","data":{"content":"prompt"},"id":"00000000-0000-4000-8000-000000000002","timestamp":"2026-11-10T10:00:01Z"}""",
        ]);
    }

    string ChatLog => Tmp.PathTo("ws", "chatSessions", DashedSid + ".jsonl");

    Task Drain(string transcript, WatchState state) => Recorders.For(Sid, transcript, null, "copilot").DrainNewLines(state, CancellationToken.None);

    JsonNode[] Sent => Recorders.Transport.Batches.SelectMany(b => b.Lines).Select(l => JsonNode.Parse(l)!).ToArray();

    static (string? Type, string? Model, long? Output, long? Nano) Shape(JsonNode line) => (
        line["type"]?.GetValue<string>(),
        line["data"]?["usage"]?["model"]?.GetValue<string>(),
        line["data"]?["usage"]?["outputTokens"]?.GetValue<long>(),
        line["data"]?["totalNanoAiu"]?.GetValue<long>());

    [Test]
    public async Task A_drain_with_no_new_transcript_line_sends_what_the_chat_log_gained() {
        var transcript = Transcript();
        var state      = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;

        await Drain(transcript, state);
        await File.WriteAllTextAsync(ChatLog, Log(Request("request_1", 1, 430, 0.46501515)));
        await Drain(transcript, state);
        await Drain(transcript, state);

        await Assert.That(Sent.Skip(2).Select(Shape).ToArray()).IsEquivalentTo(new (string?, string?, long?, long?)[] {
            ("session.usage_record", "gpt-6-luna", 430, null),
            ("kcap.vscode_request_credits", null, null, 465015150),
        });
        await Assert.That(Recorders.Transport.Batches[1].LineNumbers!.All(n => n >= CopilotLocalUsage.LineBase)).IsTrue();
    }

    [Test]
    public async Task A_failed_send_sends_the_same_usage_on_the_next_drain() {
        var transcript = Transcript();
        var state      = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await Drain(transcript, state);
        await File.WriteAllTextAsync(ChatLog, Log(Request("request_1", 1, 430, 0.5)));

        Recorders.Transport.OnSend = _ => throw new IOException("hub down");
        await Drain(transcript, state);
        Recorders.Transport.OnSend = null;
        await Drain(transcript, state);

        await Assert.That(Sent.Skip(2).Select(Shape).ToArray()).IsEquivalentTo(new (string?, string?, long?, long?)[] {
            ("session.usage_record", "gpt-6-luna", 430, null),
            ("kcap.vscode_request_credits", null, null, 500000000),
        });
    }

    /// <summary>A server found behind on reconnect may have lost usage with the gap, and its frontier cannot say, so
    /// the next drain sends the usage again.</summary>
    [Test]
    public async Task A_rewind_on_reconnect_sends_the_usage_again() {
        var transcript = Transcript();
        var state      = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await File.WriteAllTextAsync(ChatLog, Log(Request("request_1", 1, 430, 0.5)));
        await Drain(transcript, state);

        using var gate = new SemaphoreSlim(1, 1);
        await Recorders.For(Sid, transcript, null, "copilot").GatedReregisterAsync(gate, state, _ => Task.FromResult(0), null, CancellationToken.None);
        await Drain(transcript, state);

        await Assert.That(Sent.Count(l => Shape(l).Type == "kcap.vscode_request_credits")).IsEqualTo(2);
    }

    [Test]
    public async Task Forgetting_what_was_sent_keeps_what_is_staged() {
        var state = new CopilotLocalUsageState(new VsCodeChatSessionCache(ChatLog, DashedSid, _ => { }));
        state.StagedRequests.Add("request_1");
        state.StagedCredits.Add(("request_1", 500000000));
        state.SentRequests.Add("request_0");

        state.ForgetSent();

        await Assert.That(state.SentRequests).IsEmpty();
        await Assert.That(state.StagedRequests).IsEquivalentTo(["request_1"]);
        await Assert.That(state.StagedCredits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Usage_the_final_drain_cannot_send_marks_the_session_for_import() {
        var transcript = Transcript();
        var recorder   = Recorders.For(Sid, transcript, null, "copilot");
        recorder.State.ThresholdReached = true;
        Recorders.Transport.IsConnected = true;
        await recorder.DrainNewLines(recorder.State, CancellationToken.None);
        await File.WriteAllTextAsync(ChatLog, Log(Request("request_1", 1, 430, 0.5)));

        Recorders.Transport.OnSend = _ => throw new IOException("hub down");
        await recorder.FinishAsync(CancellationToken.None);

        await Assert.That(new TranscriptSpool(Config.Root, TimeProvider.System, "http://127.0.0.1:1").NeedsImport(Sid)).IsTrue();
    }

    static readonly IReadOnlyDictionary<string, long> NoCredits = FrozenDictionary<string, long>.Empty;

    [Test]
    public async Task Each_request_reports_its_credits_and_each_completed_one_its_output_once() {
        var session = Session(
            Request("a", 1, 430, 0.5),
            Request("b", 1, 120, 0.25),
            Request("pending", 0, 9, 0.125),
            Request("no-model", 1, 9, null, model: null),
            Request("no-output", 1, 0, null));

        var first = CopilotLocalUsage.Build(Sid, session, FrozenSet<string>.Empty, NoCredits);
        var again = CopilotLocalUsage.Build(Sid, session, first.RequestIds.ToFrozenSet(), first.Credits.ToDictionary(c => c.RequestId, c => c.NanoAiu));

        await Assert.That(first.RequestIds).IsEquivalentTo(["a", "b"]);
        await Assert.That(first.Credits).IsEquivalentTo([("a", 500000000L), ("b", 250000000L), ("pending", 125000000L)]);
        await Assert.That(again.Lines).IsEmpty();
    }

    /// <summary>A request the user deleted from the chat takes nothing back, and a later charge is sent whole.</summary>
    [Test]
    public async Task A_deleted_request_holds_back_no_later_charge() {
        var sent  = new Dictionary<string, long> { ["a"] = 500000000, ["b"] = 250000000 };
        var after = CopilotLocalUsage.Build(Sid, Session(Request("b", 1, 120, 0.25), Request("c", 1, 80, 0.1)), new HashSet<string> { "a", "b" }, sent);

        await Assert.That(after.Credits).IsEquivalentTo([("c", 100000000L)]);
    }

    /// <summary>A request's lines keep their numbers when the requests before it change, in bands of their own.</summary>
    [Test]
    public async Task A_requests_line_numbers_do_not_move_with_its_position() {
        var before = CopilotLocalUsage.Build(Sid, Session(Request("a", 1, 430, 0.5), Request("b", 1, 120, 0.25)), FrozenSet<string>.Empty, NoCredits);
        var after  = CopilotLocalUsage.Build(Sid, Session(Request("b", 1, 120, 0.25)), FrozenSet<string>.Empty, NoCredits);

        await Assert.That(after.LineNumbers).IsEquivalentTo(before.LineNumbers.Skip(2).ToArray());
        await Assert.That(before.LineNumbers.Distinct().Count()).IsEqualTo(4);
        await Assert.That(before.LineNumbers.All(n => n > CopilotLocalUsage.LineBase && n < 2_000_000_000)).IsTrue();
    }

    [Test]
    public async Task A_live_send_and_a_reimport_carry_the_same_ids() {
        var session = Session(Request("a", 1, 430, 0.5));

        var live     = CopilotLocalUsage.Build(Sid, session, FrozenSet<string>.Empty, NoCredits);
        var imported = CopilotLocalUsage.Build(DashedSid.ToUpperInvariant(), session, FrozenSet<string>.Empty, NoCredits);

        await Assert.That(imported.Lines).IsEquivalentTo(live.Lines);
        await Assert.That(live.Lines.Select(l => Guid.TryParse(JsonNode.Parse(l)!["id"]!.GetValue<string>(), out _)).All(ok => ok)).IsTrue();
    }

    [Test]
    public async Task A_log_of_an_unexpected_shape_warns_once_and_never_throws() {
        var transcript = Transcript();
        var state      = new WatchState { ThresholdReached = true };
        var warnings   = new List<string>();

        foreach (var text in new[] { "{\"kind\":7}\nnot json\n", "{\"kind\":0,\"v\":[]}\n", Log(Request("a", 1, 1, 0.5)) + "{\"kind\":5}\n" }) {
            await File.WriteAllTextAsync(ChatLog, text);
            var lines = new List<string>();
            CopilotLocalUsage.Append(state, lines, [], transcript, Sid, warnings.Add);
        }

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(state.CopilotLocalUsage!.StagedRequests).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task A_missing_chat_log_stages_nothing() {
        var state = new WatchState { ThresholdReached = true };
        var lines = new List<string>();

        CopilotLocalUsage.Append(state, lines, [], Transcript(), Sid, _ => { });

        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task The_title_is_the_chat_logs_custom_title_and_only_a_local_layout_has_one() {
        var transcript = Transcript();
        await File.WriteAllTextAsync(ChatLog, Log());

        await Assert.That(VsCodeChatTitle.ForTranscript(transcript)!.Read()!.Title).IsEqualTo("Create notes.md");
        await Assert.That(VsCodeChatTitle.ForTranscript(Tmp.PathTo("session-state", DashedSid, "events.jsonl"))).IsNull();
    }
}
