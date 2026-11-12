using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>
/// A VS Code Local session's usage, read from its chat log and sent as the lines Copilot's own transcripts carry: one
/// usage record per completed request and a checkpoint with the session's credits. The chat log is written seconds
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
            ("session.usage_checkpoint", null, null, 465015150),
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
            ("session.usage_checkpoint", null, null, 500000000),
        });
    }

    [Test]
    public async Task Requests_add_up_and_each_completed_one_is_recorded_once() {
        var session = Session(
            Request("a", 1, 430, 0.5),
            Request("b", 1, 120, 0.25),
            Request("pending", 0, 9, 0.125),
            Request("no-model", 1, 9, null, model: null),
            Request("no-output", 1, 0, null));

        var first = CopilotLocalUsage.Build(Sid, session, FrozenSet<string>.Empty, sentNanoAiu: 0);
        var again = CopilotLocalUsage.Build(Sid, session, first.RequestIds.ToFrozenSet(), first.NanoAiu!.Value);

        await Assert.That(first.RequestIds).IsEquivalentTo(["a", "b"]);
        await Assert.That(first.NanoAiu).IsEqualTo(875000000);
        await Assert.That(again.Lines).IsEmpty();
    }

    [Test]
    public async Task A_lower_total_is_never_sent() {
        var fewer = CopilotLocalUsage.Build(Sid, Session(Request("a", 1, 430, 0.5)), new HashSet<string> { "a" }.ToFrozenSet(), sentNanoAiu: 750000000);

        await Assert.That(fewer.Lines).IsEmpty();
        await Assert.That(fewer.NanoAiu).IsNull();
    }

    [Test]
    public async Task A_live_send_and_a_reimport_carry_the_same_ids() {
        var session = Session(Request("a", 1, 430, 0.5));

        var live     = CopilotLocalUsage.Build(Sid, session, FrozenSet<string>.Empty, 0);
        var imported = CopilotLocalUsage.Build(DashedSid.ToUpperInvariant(), session, FrozenSet<string>.Empty, 0);

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
