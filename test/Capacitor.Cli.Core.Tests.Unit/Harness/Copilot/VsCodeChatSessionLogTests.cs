using System.Globalization;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

/// <summary>
/// Replaying VS Code's chat log, from lines shaped like a captured Local session (prompt and reply text replaced) to
/// what kcap stores: the title and each request's output, credits and resolved model. A shape kcap does not expect
/// leaves out that entry or field only, and is named.
/// </summary>
public class VsCodeChatSessionLogTests {
    const string Sid = "87b11035-b254-437d-b242-49a936315953";

    static readonly string Snapshot =
        """{"kind":0,"v":{"version":3,"creationDate":1794323183101,"initialLocation":"panel","responderUsername":"GitHub Copilot","sessionId":"SID","hasPendingEdits":false,"requests":[],"pendingRequests":[],"inputState":{"mode":{"id":"agent"}}}}""".Replace("SID", Sid);

    static string Push(string requestId) =>
        """{"kind":2,"k":["requests"],"v":[{"requestId":"REQ","timestamp":1794323247002,"modelId":"copilot/auto","message":{"text":"prompt","parts":[]},"response":[],"modelState":{"value":0},"agent":{"id":"github.copilot.editsAgent"}}]}""".Replace("REQ", requestId);

    static string[] Completion(int index, long tokens, double credits, string model) => [
        .. new[] {
            """{"kind":1,"k":["requests",IDX,"result"],"v":{"timings":{"firstProgress":5309,"totalElapsed":19084},"metadata":{"promptTokens":36714,"outputTokens":36,"toolCallRounds":[],"toolCallResults":{},"resolvedModel":"MODEL"},"details":"GPT-6 Luna • 0.5 credits"}}""",
            """{"kind":1,"k":["requests",IDX,"modelState"],"v":{"value":1,"completedAt":1794323266086}}""",
            """{"kind":1,"k":["requests",IDX,"completionTokens"],"v":TOKENS}""",
            """{"kind":1,"k":["requests",IDX,"promptTokens"],"v":36714}""",
            """{"kind":1,"k":["requests",IDX,"copilotCredits"],"v":CREDITS}""",
            """{"kind":2,"k":["requests",IDX,"response"],"v":[{"kind":"thinking","value":"thought"},{"value":"reply"}]}""",
        }.Select(line => line
            .Replace("IDX", index.ToString(CultureInfo.InvariantCulture))
            .Replace("TOKENS", tokens.ToString(CultureInfo.InvariantCulture))
            .Replace("CREDITS", credits.ToString("R", CultureInfo.InvariantCulture))
            .Replace("MODEL", model)),
    ];

    const string Title = """{"kind":1,"k":["customTitle"],"v":"Create notes.md and sleep"}""";

    static string[] OneRequest() => [Snapshot, Title, Push("request_1"), .. Completion(0, 430, 0.46501515, "gpt-6-luna")];

    static VsCodeChatSession? Replay(params string[] lines) => VsCodeChatSessionLog.Replay(string.Join("\n", lines) + "\n", Sid);

    [Test]
    public async Task A_captured_session_replays_to_its_title_output_credits_and_model() {
        var session = Replay(OneRequest());

        await Assert.That(session!.CustomTitle).IsEqualTo("Create notes.md and sleep");
        await Assert.That(session.Requests).IsEquivalentTo([new VsCodeChatRequest("request_1", true, 430, 0.46501515, "gpt-6-luna")]);
        await Assert.That(session.Skipped).IsEmpty();
    }

    [Test]
    public async Task A_push_with_a_length_truncates_first_and_a_delete_removes_the_field() {
        var session = Replay([
            .. OneRequest(),
            Push("request_2"),
            """{"kind":2,"k":["requests"],"i":1}""",
            """{"kind":3,"k":["requests",0,"copilotCredits"]}""",
        ]);

        await Assert.That(session!.Requests.Select(r => (r.RequestId, r.CopilotCredits))).IsEquivalentTo([("request_1", (double?)null)]);
    }

    [Test]
    public async Task A_rewritten_log_reads_from_its_new_snapshot_alone() {
        var compacted = """{"kind":0,"v":{"version":3,"sessionId":"SID","customTitle":"Renamed","requests":[{"requestId":"request_1","modelState":{"value":1},"completionTokens":430,"copilotCredits":0.5,"result":{"metadata":{"resolvedModel":"gpt-6-luna"}}}]}}""".Replace("SID", Sid);

        var session = Replay([.. OneRequest(), compacted]);

        await Assert.That(session!.CustomTitle).IsEqualTo("Renamed");
        await Assert.That(session.Requests).IsEquivalentTo([new VsCodeChatRequest("request_1", true, 430, 0.5, "gpt-6-luna")]);
    }

    [Test]
    [Arguments("""{"kind":9,"k":["customTitle"],"v":"x"}""", "unknown kind 9")]
    [Arguments("""{"k":["customTitle"],"v":"x"}""", "has no kind")]
    [Arguments("""{"kind":1,"k":["requests",7,"completionTokens"],"v":1}""", "does not fit")]
    [Arguments("""{"kind":1,"k":[{"a":1}],"v":1}""", "does not fit")]
    [Arguments("""not json at all""", "not JSON")]
    [Arguments("""{"kind":2,"k":["requests"],"i":0,"v":{}}""", "does not fit")]
    [Arguments("""{"kind":2,"k":["requests"],"i":"0","v":[]}""", "does not fit")]
    public async Task An_entry_of_an_unexpected_shape_is_skipped_and_the_rest_still_applies(string entry, string named) {
        var lines   = OneRequest();
        var session = Replay([.. lines[..3], entry, .. lines[3..]]);

        await Assert.That(session!.Requests).IsEquivalentTo([new VsCodeChatRequest("request_1", true, 430, 0.46501515, "gpt-6-luna")]);
        await Assert.That(session.Skipped.Single()).Contains(named);
    }

    [Test]
    public async Task A_last_line_still_being_written_is_left_for_the_next_read() {
        var text = string.Join("\n", OneRequest()) + "\n" + """{"kind":1,"k":["customTitle"],"v":"Half""";

        var session = VsCodeChatSessionLog.Replay(text, Sid);

        await Assert.That(session!.CustomTitle).IsEqualTo("Create notes.md and sleep");
        await Assert.That(session.Skipped).IsEmpty();
        await Assert.That(VsCodeChatSessionLog.Replay(Snapshot[..40], Sid)).IsNull();
    }

    [Test]
    public async Task Entries_with_no_snapshot_read_as_nothing_and_say_so() {
        var session = Replay(Title, Push("request_1"));

        await Assert.That(session!.Requests).IsEmpty();
        await Assert.That(session.Skipped).Contains("the log has no snapshot");
    }

    [Test]
    public async Task A_wrongly_typed_field_is_left_out_of_its_request_alone() {
        var session = Replay([
            Snapshot, Push("request_1"), .. Completion(0, 430, 0.5, "gpt-6-luna"),
            """{"kind":1,"k":["requests",0,"completionTokens"],"v":"430"}""",
            """{"kind":1,"k":["requests",0,"result","metadata","resolvedModel"],"v":7}""",
            """{"kind":1,"k":["customTitle"],"v":{"text":"x"}}""",
            """{"kind":2,"k":["requests"],"v":[{"timestamp":1}]}""",
        ]);

        await Assert.That(session!.Requests).IsEquivalentTo([new VsCodeChatRequest("request_1", true, null, 0.5, null)]);
        await Assert.That(session.CustomTitle).IsNull();
        await Assert.That(session.Skipped.Count).IsEqualTo(4);
    }

    [Test]
    public async Task The_model_falls_back_to_the_auto_mode_resolution() {
        var session = Replay(
            Snapshot, Push("request_1"),
            """{"kind":2,"k":["requests",0,"response"],"v":[{"kind":"autoModeResolution","resolved":{"id":"gpt-6-luna","name":"GPT-6 Luna"}}]}""");

        await Assert.That(session!.Requests.Single().Model).IsEqualTo("gpt-6-luna");
    }

    [Test]
    public async Task Another_sessions_log_reads_as_nothing() {
        var session = VsCodeChatSessionLog.Replay(string.Join("\n", OneRequest()), "11111111-2222-3333-4444-555555555555");

        await Assert.That(session!.Requests).IsEmpty();
        await Assert.That(session.CustomTitle).IsNull();
    }

    [Test]
    public async Task A_missing_log_reads_as_nothing_to_read() {
        using var tmp = new TempDir();

        await Assert.That(VsCodeChatSessionLog.Read(tmp.PathTo("absent.jsonl"), Sid)).IsNull();
    }
}
