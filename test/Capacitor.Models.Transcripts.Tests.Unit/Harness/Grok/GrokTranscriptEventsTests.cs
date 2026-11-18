using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Grok;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Grok;

/// <summary>The rules of kcap-server's Grok normalizer, one line at a time: what each update emits, the ids and times
/// it emits them on, how a turn's usage finds its peak and its target, and which lines it ignores or rejects.</summary>
public class GrokTranscriptEventsTests {
    static readonly DateTimeOffset Received  = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset AgentTime = DateTimeOffset.FromUnixTimeMilliseconds(1791900941391);

    static GrokContext Context(string? agentId = null) => (GrokContext)GrokTranscriptEvents.Instance.CreateContext("s", agentId);

    static ProjectionResult P(string line, GrokContext? ctx = null, int lineNumber = 1) =>
        GrokTranscriptEvents.Instance.Project(line, lineNumber, Received, ctx ?? Context());

    static IReadOnlyList<CanonicalEvent> E(string line, GrokContext? ctx = null) => P(line, ctx).Events;

    /// An ACP update; <paramref name="meta"/> is the inside of the line's <c>_meta</c>.
    static string Acp(string update, string meta = """ "eventId":"e1","agentTimestampMs":1791900941391 """) =>
        """{"timestamp":1791900942,"method":"session/update","params":{"sessionId":"s","update":UPDATE,"_meta":{META}}}""".Replace("META", meta).Replace("UPDATE", update);

    static string Turn(string promptId, string modelUsage, string eventId = "t1") =>
        """{"timestamp":1791900950,"method":"_x.ai/session/update","params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","prompt_id":"PROMPT","usage":{"modelUsage":USAGE}},"_meta":{"eventId":"EVENT","agentTimestampMs":1791900950991}}}"""
            .Replace("PROMPT", promptId).Replace("EVENT", eventId).Replace("USAGE", modelUsage);

    static string TurnMeta(string promptId, long totalTokens, string eventId) =>
        $""" "eventId":"{eventId}","promptId":"{promptId}","totalTokens":{totalTokens} """;

    static string Reply(string text, string promptId, long totalTokens, string eventId) =>
        Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"TEXT"}}""".Replace("TEXT", text), TurnMeta(promptId, totalTokens, eventId));

    static string Call(string callId, string promptId, long totalTokens, string eventId) =>
        Acp("""{"sessionUpdate":"tool_call","toolCallId":"CALL","title":"list_dir","rawInput":{}}""".Replace("CALL", callId), TurnMeta(promptId, totalTokens, eventId));

    const string OneModel = """{"grok-4.5-build":{"inputTokens":92568,"outputTokens":1368,"cachedReadTokens":56960,"reasoningTokens":203}}""";

    static CodexUsageBackfilledEvent Usage(CanonicalEvent e) => (CodexUsageBackfilledEvent)e.Payload;

    static ToolResultReceived Result(string update) => (ToolResultReceived)E(Acp(update)).Single().Payload;

    // ── Lines that emit nothing ───────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"available_commands_update"},"_meta":{"eventId":"s-1"}}}""")]
    [Arguments("""{"method":"_x.ai/session/update","params":{"sessionId":"s","update":{"sessionUpdate":"session_recap","summary":"x"},"_meta":{"eventId":"s-2"}}}""")]
    [Arguments("""{"method":"session/other","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}},"_meta":{"eventId":"s-3"}}}""")]
    [Arguments("""{"method":"session/update","params":{"sessionId":"s"}}""")]
    [Arguments("""{"method":"session/update"}""")]
    public async Task An_update_the_chat_has_no_event_for_emits_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) =>
        await Assert.That(P(line).Rejected).IsNotNull();

    [Test]
    public async Task An_empty_text_run_emits_nothing() =>
        await Assert.That(E(Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""}}"""))).IsEmpty();

    // ── Text, ids and times ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_reply_is_one_event_at_the_agents_time_on_its_event_id() {
        var e = E(Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}""")).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(((AssistantTextGenerated)e.Payload).Content).IsEqualTo("Hello");
        await Assert.That(((AssistantTextGenerated)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(AgentTime);
        await Assert.That(e.Timestamp).IsEqualTo(AgentTime);
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-10-13T14:15:41.3910000+00:00");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.Grok("e1"));
    }

    /// <summary>The server's pipeline stamps every user message as it stores it, so the projection leaves it bare.</summary>
    [Test]
    public async Task A_user_text_run_is_a_user_message_without_a_turn_stamp() {
        var message = (UserMessageReceived)E(Acp("""{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"Summarize"}}""")).Single().Payload;

        await Assert.That(message.Content).IsEqualTo("Summarize");
        await Assert.That(message.Extensions).IsEmpty();
    }

    [Test]
    public async Task A_thought_is_thinking_marked_unencrypted() {
        var thinking = (AssistantThinkingGenerated)E(Acp("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Hmm"}}""")).Single().Payload;

        await Assert.That(thinking.Content).IsEqualTo("Hmm");
        await Assert.That(thinking.HasEncrypted).IsTrue();
        await Assert.That(thinking.Encrypted).IsFalse();
    }

    [Test]
    public async Task A_line_without_the_agents_time_takes_its_root_timestamp_in_seconds() {
        var e = E(Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}""", """ "eventId":"e1" """)).Single();

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1791900942));
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-10-13T14:15:42.0000000+00:00");
    }

    [Test]
    public async Task A_line_with_no_time_takes_the_receive_time_and_no_record_timestamp() {
        var e = E("""{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}}}""").Single();

        await Assert.That(e.Timestamp).IsEqualTo(Received);
        await Assert.That(((AssistantTextGenerated)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
        await Assert.That(e.RecordTimestamp).IsNull();
    }

    /// <summary>The server stores this line at the wrapped instant, so the projection must not reject it.</summary>
    [Test]
    public async Task A_root_time_whose_milliseconds_overflow_wraps_as_the_server_stores_it() {
        var line = Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}""", """ "eventId":"e1" """)
            .Replace("1791900942", "18446744073709552");

        var e = E(line).Single();

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(384));
        await Assert.That(e.RecordTimestamp).IsEqualTo("1970-01-01T00:00:00.3840000+00:00");
    }

    [Test]
    public async Task A_time_out_of_range_rejects_the_line_and_leaves_the_turn() {
        var ctx = Context();
        P(Reply("x", "p1", 10, "a"), ctx);

        var result = P(Acp("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}""", """ "eventId":"e2","agentTimestampMs":999999999999999999,"promptId":"p2" """), ctx);

        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(ctx.TurnPromptId).IsEqualTo("p1");
    }

    [Test]
    public async Task A_line_naming_no_event_id_is_keyed_by_its_number_and_bytes() {
        const string line = """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}}}""";

        var first = P(line, lineNumber: 7).Events.Single().EventId;

        await Assert.That(first).IsEqualTo(TranscriptIds.Grok(TranscriptIds.GrokLineEventId(7, line)));
        await Assert.That(P(line, lineNumber: 8).Events.Single().EventId).IsNotEqualTo(first);
    }

    // ── Tool calls ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_tool_call_carries_its_id_name_kind_and_input() {
        var line = Acp("""{"sessionUpdate":"tool_call","toolCallId":"call-a-1","title":"run_terminal_command","rawInput":{"command":"head -1 README.md"},"_meta":{"x.ai/tool":{"name":"run_terminal_command","kind":"execute"}}}""");
        var call = ((AssistantToolCallsGenerated)E(line).Single().Payload).ToolCalls.Single();

        await Assert.That(call.CallId).IsEqualTo("call-a-1");
        await Assert.That(call.ToolName).IsEqualTo("run_terminal_command");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("head -1 README.md");
    }

    /// <summary>ACP's own kinds pass through, two Grok names map onto ACP's, any other is <c>other</c>, and none stays none.</summary>
    [Test]
    [Arguments("read", "read")]
    [Arguments("search", "search")]
    [Arguments("list", "read")]
    [Arguments("write", "edit")]
    [Arguments("use_tool", "other")]
    [Arguments(" ", null)]
    public async Task A_tool_calls_kind_is_acps_vocabulary(string grokKind, string? expected) {
        var line = Acp("""{"sessionUpdate":"tool_call","toolCallId":"c","_meta":{"x.ai/tool":{"name":"t","kind":"KIND"}}}""".Replace("KIND", grokKind));
        var call = ((AssistantToolCallsGenerated)E(line).Single().Payload).ToolCalls.Single();

        await Assert.That(call.HasToolKind ? call.ToolKind : null).IsEqualTo(expected);
    }

    [Test]
    public async Task A_tool_call_without_groks_tool_block_is_named_by_its_title_and_has_no_kind_or_input() {
        var call = ((AssistantToolCallsGenerated)E(Acp("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"grep","rawInput":"helper"}""")).Single().Payload).ToolCalls.Single();

        await Assert.That(call.ToolName).IsEqualTo("grep");
        await Assert.That(call.HasToolKind).IsFalse();
        await Assert.That(call.Arguments).IsNull();
    }

    [Test]
    public async Task A_tool_call_without_an_id_emits_nothing() =>
        await Assert.That(E(Acp("""{"sessionUpdate":"tool_call","toolCallId":"","title":"grep"}"""))).IsEmpty();

    // ── Tool results ──────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"sessionUpdate":"tool_call_update","toolCallId":"c","kind":"other","title":"List","content":[{"type":"content","content":{"type":"text","text":"List the root"}}]}""")]
    [Arguments("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"in_progress"}""")]
    [Arguments("""{"sessionUpdate":"tool_call_update","toolCallId":"","status":"completed"}""")]
    public async Task A_tool_update_before_the_call_finishes_emits_nothing(string update) =>
        await Assert.That(E(Acp(update))).IsEmpty();

    [Test]
    public async Task A_failed_tool_records_the_error_text_and_the_acp_flag() {
        var result = Result("""{"sessionUpdate":"tool_call_update","toolCallId":"call-a-2","status":"failed","rawOutput":{"type":"MCP","output":{"Error":"Error: notes server is not connected"},"is_error":true}}""");

        await Assert.That(result.CallId).IsEqualTo("call-a-2");
        await Assert.That(result.Result).IsEqualTo("Error: notes server is not connected");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(result, AcpExtension.Slug), "is_error")).IsTrue();
    }

    [Test]
    public async Task A_completed_tool_is_not_flagged() {
        var result = Result("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed","content":[{"type":"content","content":{"type":"text","text":"ok"}}]}""");

        await Assert.That(result.Extensions).IsEmpty();
    }

    /// <summary>The update's text blocks win; else an MCP call's error or output, a <c>content</c> or <c>text</c> string, or
    /// the whole <c>rawOutput</c> as JSON.</summary>
    [Test]
    [Arguments("""[{"type":"content","content":{"type":"text","text":"one"}},"junk",{"type":"content","content":{"type":"text","text":"two"}}]""", """{"text":"raw"}""", "one\ntwo")]
    [Arguments("""[{"type":"diff","path":"/x"}]""", """{"content":"from content"}""", "from content")]
    [Arguments("null", """{"output":{"Error":"failed"}}""", "failed")]
    [Arguments("null", """{"output":{"Error":{"code":1}}}""", """{"code":1}""")]
    [Arguments("null", """{"output":{"OkayOutput":"fine"}}""", "fine")]
    [Arguments("null", """{"output":{"OkayOutput":{"replaced":1}}}""", """{"replaced":1}""")]
    [Arguments("null", """{"output":[35,32],"text":"from text"}""", "from text")]
    [Arguments("null", """{"type":"ListDir","entries":["a"]}""", """{"type":"ListDir","entries":["a"]}""")]
    [Arguments("null", "42", "42")]
    public async Task A_tool_results_text_is_read_from_its_blocks_then_its_raw_output(string content, string rawOutput, string expected) {
        var result = Result("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed","content":CONTENT,"rawOutput":RAW}""".Replace("CONTENT", content).Replace("RAW", rawOutput));

        await Assert.That(result.Result).IsEqualTo(expected);
    }

    [Test]
    public async Task A_tool_result_with_nothing_to_read_has_no_text() =>
        await Assert.That(Result("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed"}""").HasResult).IsFalse();

    // ── Turn usage ────────────────────────────────────────────────────────────────────────────

    /// <summary>Grok's <c>inputTokens</c> includes the cached read; the canonical input is net of it.</summary>
    [Test]
    public async Task Turn_usage_is_recorded_per_model_with_input_net_of_cache() {
        var e     = E(Turn("p1", OneModel)).Single();
        var usage = Usage(e);

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.CodexUsageBackfilled);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.GrokUsage("t1", "grok-4.5-build"));
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-10-13T14:15:50.9910000+00:00");
        await Assert.That(usage.Model).IsEqualTo("grok-4.5-build");
        await Assert.That(usage.InputTokens).IsEqualTo(92568L - 56960L);
        await Assert.That(usage.CacheReadTokens).IsEqualTo(56960L);
        await Assert.That(usage.OutputTokens).IsEqualTo(1368L);
        await Assert.That(usage.ReasoningTokens).IsEqualTo(203L);
        await Assert.That(usage.CacheWriteTokens).IsNull();
    }

    /// <summary>A reader shows a backfill only on a turn it targets. The target names no tool: a reader adds a target
    /// tool's share of the tokens to that tool.</summary>
    [Test]
    public async Task Turn_usage_targets_the_turns_last_text_over_a_later_tool_call() {
        var ctx  = Context();
        var text = E(Reply("first", "p1", 100, "a"), ctx).Single();
        P(Call("c1", "p1", 200, "b"), ctx);

        var usage = Usage(E(Turn("p1", OneModel), ctx).Single());

        await Assert.That(usage.Targets.Single().EventId).IsEqualTo(text.EventId.ToString());
        await Assert.That(usage.Targets.Single().EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(usage.Targets.Single().ToolName).IsNull();
        await Assert.That(usage.Targets.Single().IsEcho).IsFalse();
        await Assert.That(usage.OccupancyTokens).IsEqualTo(200L);
    }

    [Test]
    public async Task A_turn_with_no_text_targets_its_last_tool_call() {
        var ctx = Context();
        P(Call("c1", "p1", 100, "a"), ctx);
        var last = E(Call("c2", "p1", 50, "b"), ctx).Single();

        var target = Usage(E(Turn("p1", OneModel), ctx).Single()).Targets.Single();

        await Assert.That(target.EventId).IsEqualTo(last.EventId.ToString());
        await Assert.That(target.EventType).IsEqualTo(CanonicalEventTypes.AssistantToolCallsGenerated);
    }

    /// <summary>The pipeline normalizes a line again when its append failed; the retry must carry the same peak and target.</summary>
    [Test]
    public async Task A_retried_turn_completed_line_keeps_its_peak_and_target() {
        var ctx = Context();
        P(Reply("x", "p1", 4000, "a"), ctx);

        var first = Usage(E(Turn("p1", OneModel), ctx).Single());
        var retry = Usage(E(Turn("p1", OneModel), ctx).Single());

        await Assert.That(first.OccupancyTokens).IsEqualTo(4000L);
        await Assert.That(retry.OccupancyTokens).IsEqualTo(first.OccupancyTokens);
        await Assert.That(retry.Targets.Single().EventId).IsEqualTo(first.Targets.Single().EventId);
    }

    /// <summary>The turn's peak is one request's context, so only one model may claim it.</summary>
    [Test]
    public async Task A_turn_with_several_models_credits_its_peak_to_the_one_that_read_the_most() {
        var ctx = Context();
        P(Reply("x", "p1", 4000, "a"), ctx);

        var events = E(Turn("p1", """{"small":{"inputTokens":100,"outputTokens":1,"cachedReadTokens":0},"big":{"inputTokens":9000,"outputTokens":5,"cachedReadTokens":1000}}"""), ctx);
        var byModel = events.Select(Usage).ToDictionary(u => u.Model!);

        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo(new[] { TranscriptIds.GrokUsage("t1", "small"), TranscriptIds.GrokUsage("t1", "big") }, CollectionOrdering.Matching);
        await Assert.That(byModel["big"].OccupancyTokens).IsEqualTo(4000L);
        await Assert.That(byModel["small"].OccupancyTokens).IsEqualTo(0L);
        await Assert.That(byModel["small"].Targets.Single().EventId).IsEqualTo(byModel["big"].Targets.Single().EventId);
    }

    /// <summary>The server picks the peak before it drops the models that record nothing, so one of those that read the
    /// most leaves the recorded models with no peak.</summary>
    [Test]
    public async Task A_model_that_records_nothing_still_takes_the_peak_when_it_read_the_most() {
        var ctx = Context();
        P(Reply("x", "p1", 4000, "a"), ctx);

        var usage = Usage(E(Turn("p1", """{"":{"inputTokens":9000},"grok":{"inputTokens":100,"outputTokens":1}}"""), ctx).Single());

        await Assert.That(usage.Model).IsEqualTo("grok");
        await Assert.That(usage.OccupancyTokens).IsEqualTo(0L);
    }

    [Test]
    public async Task A_model_with_nothing_counted_or_no_name_records_nothing_and_counts_floor_at_zero() {
        var events = E(Turn("p1", """{"idle":{"inputTokens":0,"outputTokens":0},"":{"inputTokens":5},"odd":7,"cached":{"inputTokens":100,"outputTokens":-3,"cachedReadTokens":150,"reasoningTokens":-1}}"""));
        var usage  = Usage(events.Single());

        await Assert.That(usage.Model).IsEqualTo("cached");
        await Assert.That(usage.InputTokens).IsEqualTo(0L);
        await Assert.That(usage.CacheReadTokens).IsEqualTo(150L);
        await Assert.That(usage.OutputTokens).IsEqualTo(0L);
        await Assert.That(usage.ReasoningTokens).IsEqualTo(0L);
    }

    [Test]
    public async Task A_turn_completed_without_per_model_usage_emits_nothing() =>
        await Assert.That(E("""{"method":"_x.ai/session/update","params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","prompt_id":"p1","usage":{"inputTokens":10}},"_meta":{"eventId":"t"}}}""")).IsEmpty();

    /// <summary>A parent's turn usage already includes its subagents', so a subagent stream recording its own would count it twice.</summary>
    [Test]
    public async Task A_subagent_stream_records_no_usage() {
        var ctx = Context(agentId: "child");

        await Assert.That(E(Reply("x", "p1", 10, "a"), ctx)).Count().IsEqualTo(1);
        await Assert.That(E(Turn("p1", OneModel), ctx)).IsEmpty();
    }

    [Test]
    public async Task A_turn_completed_for_another_turn_carries_no_peak_or_target() {
        var ctx = Context();
        P(Reply("x", "p2", 4000, "a"), ctx);

        var usage = Usage(E(Turn("p1", OneModel), ctx).Single());

        await Assert.That(usage.OccupancyTokens).IsEqualTo(0L);
        await Assert.That(usage.Targets).IsEmpty();
    }

    [Test]
    public async Task A_line_naming_a_new_turn_starts_it_over_whatever_the_line_emits() {
        var ctx = Context();
        P(Reply("x", "p1", 4000, "a"), ctx);

        const string unknownMethod = """{"method":"session/other","params":{"sessionId":"s","update":{"sessionUpdate":"plan"},"_meta":{"eventId":"b","promptId":"p2","totalTokens":30}}}""";
        await Assert.That(E(unknownMethod, ctx)).IsEmpty();

        var usage = Usage(E(Turn("p2", OneModel), ctx).Single());
        await Assert.That(usage.OccupancyTokens).IsEqualTo(30L);
        await Assert.That(usage.Targets).IsEmpty();
    }

    [Test]
    public async Task A_lower_token_count_later_in_the_turn_keeps_the_peak() {
        var ctx = Context();
        P(Reply("x", "p1", 4000, "a"), ctx);
        P(Acp("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"in_progress"}""", """ "eventId":"b","promptId":"p1","totalTokens":1000 """), ctx);

        await Assert.That(ctx.TurnPeakTokens).IsEqualTo(4000L);
    }

    /// <summary>The turn's state spans batches and lives on the context, so a caller resuming mid-turn restores it.</summary>
    [Test]
    public async Task A_seeded_turn_gives_its_completion_the_stored_peak_and_target() {
        var anchor = Guid.NewGuid();
        var ctx    = Context();
        ctx.TurnPromptId   = "p1";
        ctx.TurnPeakTokens = 777;
        ctx.TurnAnchor     = new(anchor, CanonicalEventTypes.AssistantTextGenerated);

        var usage = Usage(E(Turn("p1", OneModel), ctx).Single());

        await Assert.That(usage.OccupancyTokens).IsEqualTo(777L);
        await Assert.That(usage.Targets.Single().EventId).IsEqualTo(anchor.ToString());
    }

    [Test]
    public async Task Each_usage_event_has_its_own_targets_list() {
        var ctx = Context();
        P(Reply("x", "p1", 10, "a"), ctx);

        var events = E(Turn("p1", """{"a":{"inputTokens":1},"b":{"inputTokens":2}}"""), ctx).Select(Usage).ToArray();

        await Assert.That(ReferenceEquals(events[0].Targets, events[1].Targets)).IsFalse();
    }
}
