using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MistralVibe;

public class MistralVibeTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line) =>
        MistralVibeTranscriptEvents.Instance.Project(line, 1, Received,
            MistralVibeTranscriptEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    // ── legacy messages.jsonl ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Legacy_user_message_becomes_a_user_message_on_the_numbered_line_id() {
        const string line = """{"role":"user","content":"hello"}""";
        var events = E(line);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("hello");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeRecord(1, line));
    }

    /// <summary>A legacy line has no id and no time, so a message repeated later in the file is a new event.</summary>
    [Test]
    public async Task A_repeated_legacy_line_takes_a_new_id_on_each_line() {
        const string line    = """{"role":"user","content":"yes"}""";
        var          context = MistralVibeTranscriptEvents.Instance.CreateContext("sess", null);
        var          first   = MistralVibeTranscriptEvents.Instance.Project(line, 3, Received, context).Events[0];
        var          again   = MistralVibeTranscriptEvents.Instance.Project(line, 8, Received, context).Events[0];

        await Assert.That(first.EventId).IsEqualTo(TranscriptIds.VibeRecord(3, line));
        await Assert.That(again.EventId).IsEqualTo(TranscriptIds.VibeRecord(8, line));
    }

    [Test]
    public async Task Legacy_assistant_text_and_tool_calls_emit_one_event_per_call() {
        const string line = """{"role":"assistant","content":"on it","tool_calls":[{"id":"call_1","type":"function","function":{"name":"file_system.bash","arguments":"{\"cmd\":\"ls\"}"}},{"id":"call_2","type":"function","function":{"name":"file_system.read_file","arguments":"{\"path\":\"a.txt\"}"}}]}""";
        var events   = E(line);
        var recordId = TranscriptIds.VibeRecord(1, line);
        await Assert.That(events.Count).IsEqualTo(3);

        await Assert.That(((AssistantTextGenerated)events[0].Payload).Content).IsEqualTo("on it");
        await Assert.That(events[0].EventId).IsEqualTo(recordId);

        var first = ((AssistantToolCallsGenerated)events[1].Payload).ToolCalls.Single();
        await Assert.That(first.ToolName).IsEqualTo("bash"); // group prefix stripped
        await Assert.That(first.CallId).IsEqualTo("call_1");
        await Assert.That(first.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(first.Arguments.Fields["cmd"].StringValue).IsEqualTo("ls");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.VibeToolCall(recordId, "call_1"));

        var second = ((AssistantToolCallsGenerated)events[2].Payload).ToolCalls.Single();
        await Assert.That(second.CallId).IsEqualTo("call_2");
        await Assert.That(second.ToolKind).IsEqualTo(AcpToolKind.Read);
        await Assert.That(events[2].EventId).IsEqualTo(TranscriptIds.VibeToolCall(recordId, "call_2"));

        // Source order carries the sequence; every event keeps the line's own time.
        await Assert.That(events.Select(e => e.Timestamp).Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task A_legacy_call_without_an_id_or_a_name_emits_nothing() {
        const string line = """{"role":"assistant","content":"","tool_calls":[{"type":"function","function":{"name":"file_system.bash","arguments":"{}"}},{"id":"call_n","type":"function","function":{"arguments":"{}"}},{"id":"call_e","type":"function","function":{"name":"","arguments":"{}"}},{"id":"call_k","type":"function","function":{"name":"grep"}}]}""";
        var events = E(line);

        await Assert.That(events.Count).IsEqualTo(1);
        var call = ((AssistantToolCallsGenerated)events[0].Payload).ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("call_k");
        await Assert.That(call.Arguments.Fields.Count).IsEqualTo(0); // no arguments at all is an empty struct
    }

    [Test]
    [Arguments("tool_call_id")]
    [Arguments("call_id")]
    public async Task Legacy_tool_role_becomes_a_tool_result_paired_by_call_id(string key) {
        var line   = $$"""{"role":"tool","{{key}}":"call_1","content":"file.txt"}""";
        var events = E(line);
        var result = (ToolResultReceived)events[0].Payload;
        await Assert.That(result.CallId).IsEqualTo("call_1");
        await Assert.That(result.Result).IsEqualTo("file.txt");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeRecord(1, line));
    }

    [Test]
    public async Task A_legacy_tool_result_naming_no_call_emits_nothing() {
        await Assert.That(E("""{"role":"tool","content":"file.txt"}""").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Legacy_system_message_is_not_recorded() {
        await Assert.That(E("""{"role":"system","content":"you are helpful"}""").Count).IsEqualTo(0);
    }

    // ── unified store entries ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Unified_message_entry_reads_role_content_array_and_uses_the_stable_id() {
        // Real vibe 2.26.0 unified entry: content is a text-block array, createdAt is epoch ms, id is stable.
        const string entry = """{"type":"message","role":"user","content":[{"type":"text","text":"hi there"}],"createdAt":1791384615123,"id":"user-turn-abc-1"}""";
        var events = E(entry);
        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("hi there");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeEntry("user-turn-abc-1"));
        // Numeric createdAt is resolved to the real time, not the batch receive time.
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1791384615123));
    }

    [Test]
    public async Task Unified_reasoning_becomes_thinking() {
        var thinking = (AssistantThinkingGenerated)E("""{"type":"reasoning","role":"assistant","content":[{"type":"text","text":"let me think"}],"createdAt":1000,"id":"r1"}""")[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("let me think");
    }

    // The shape vibe 2.26.0's projector writes for a finished tool call.
    const string CompletedShell = """{"type":"effect","id":"effect-a1","sessionId":"s","turnId":"t1","createdAt":1000,"updatedAt":1200,"generationStatus":"completed","title":"file_system.bash","detail":{"kind":"shell","toolName":"file_system.bash","input":{"command":"ls"},"display":{"summary":"ls"}},"state":{"status":"completed","output":{"stdout":"a.txt\n","stderr":""},"outputText":"a.txt\n","durationMs":12.5,"display":{"success":true}}}""";

    [Test]
    public async Task Unified_effect_emits_a_tool_call_and_its_settled_result() {
        var events = E(CompletedShell);
        await Assert.That(events.Count).IsEqualTo(2);

        var call = ((AssistantToolCallsGenerated)events[0].Payload).ToolCalls[0];
        await Assert.That(call.ToolName).IsEqualTo("bash");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.CallId).IsEqualTo("effect-a1");
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeEntry("effect-a1"));

        var result = (ToolResultReceived)events[1].Payload;
        await Assert.That(result.CallId).IsEqualTo("effect-a1");
        await Assert.That(result.Result).IsEqualTo("a.txt\n");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.Sibling(TranscriptIds.VibeEntry("effect-a1"), "result"));
    }

    [Test]
    public async Task A_built_in_tool_with_no_output_text_reports_its_structured_stdout() {
        // vibe 2.26.0 leaves outputText empty for its built-in tools and returns the result here.
        const string line = """{"type":"effect","id":"effect-b","createdAt":1,"generationStatus":"completed","detail":{"kind":"tool","toolName":"file_system.bash","input":{"command":"python3 hello.py"}},"state":{"status":"completed","outputText":"","output":{"_meta":{},"content":[],"structured_content":{"command":"python3 hello.py","returncode":0,"stderr":"","stdout":"hi\n"},"type":"success"}}}""";

        await Assert.That(((ToolResultReceived)E(line)[1].Payload).Result).IsEqualTo("hi\n");
    }

    [Test]
    public async Task A_failed_effect_reports_its_error_message() {
        const string line = """{"type":"effect","id":"effect-f","createdAt":1,"generationStatus":"completed","detail":{"kind":"file_read","toolName":"file_system.read_file","input":{"file_path":"x"}},"state":{"status":"failed","error":{"message":"no such file"},"outputText":""}}""";
        var events = E(line);

        await Assert.That(((AssistantToolCallsGenerated)events[0].Payload).ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Read);
        await Assert.That(((ToolResultReceived)events[1].Payload).Result).IsEqualTo("no such file");
    }

    [Test]
    public async Task A_running_effect_has_no_result_yet() {
        const string line = """{"type":"effect","id":"effect-r","createdAt":1,"generationStatus":"in_progress","detail":{"kind":"shell","toolName":"file_system.bash","input":{"command":"sleep 5"}},"state":{"status":"running","outputText":""}}""";
        await Assert.That(E(line).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_generic_effect_is_classified_by_its_bare_name() {
        const string line = """{"type":"effect","id":"effect-m","createdAt":1,"generationStatus":"completed","detail":{"kind":"tool","toolName":"web.grep","input":null},"state":{"status":"completed","output":null,"outputText":""}}""";
        var call = ((AssistantToolCallsGenerated)E(line)[0].Payload).ToolCalls[0];

        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Search);
        await Assert.That(call.Arguments.Fields.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"notice","text":"x"}""")]
    [Arguments("""{"type":"checkpoint","at":"y"}""")]
    public async Task Unified_notice_and_checkpoint_are_ignored(string line) {
        await Assert.That(P(line).Events.Count).IsEqualTo(0);
        await Assert.That(P(line).Rejected).IsNull();
    }

    // ── framing ────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[1,2,3]")]
    public async Task Non_object_lines_reject(string line) {
        await Assert.That(P(line).Rejected).IsNotNull();
    }
}
