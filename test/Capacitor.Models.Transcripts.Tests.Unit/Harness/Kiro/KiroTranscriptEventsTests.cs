using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Kiro;

/// <summary>The rules of kcap-server's Kiro normalizer, one line at a time: what each line kind emits, the ids it
/// emits them on, where a turn's time and usage land, and which lines it ignores or rejects.</summary>
public class KiroTranscriptEventsTests {
    const string Session = "5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b";

    static readonly DateTimeOffset Received   = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset PromptTime = DateTimeOffset.FromUnixTimeSeconds(1781727363);

    static KiroContext Context(string sessionId = Session, string? agentId = null) =>
        (KiroContext)KiroTranscriptEvents.Instance.CreateContext(sessionId, agentId);

    static ProjectionResult P(string line, KiroContext? ctx = null, int lineNumber = 1) =>
        KiroTranscriptEvents.Instance.Project(line, lineNumber, Received, ctx ?? Context());

    static IReadOnlyList<CanonicalEvent> E(string line, KiroContext? ctx = null) => P(line, ctx).Events;

    const string Prompt = """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"hello"}],"meta":{"timestamp":1781727363}}}""";

    static string PromptLine(string text) =>
        """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"""
      + JsonSerializer.Serialize(text)
      + """}],"meta":{"timestamp":1781727363}}}""";

    static string ContentOf(string text) => ((UserMessageReceived)E(PromptLine(text))[0].Payload).Content;

    static Struct? KiroSlug(CanonicalEvent e) => SchemaExtensions.Slug(e.Payload, KiroExtension.Slug);

    // ── Prompt ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_prompt_is_a_user_message_at_its_own_time_on_its_message_id() {
        var e = E(Prompt).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("hello");
        await Assert.That(((UserMessageReceived)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(PromptTime);
        await Assert.That(e.Timestamp).IsEqualTo(PromptTime);
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-06-17T20:16:03.0000000+00:00");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "m1", "user"));
    }

    [Test]
    public async Task A_prompts_text_blocks_join_by_newline_and_empty_ones_drop() {
        const string line = """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"one"},{"kind":"text","data":""},{"kind":"image","data":"x"},{"kind":"text","data":"two"}]}}""";

        await Assert.That(((UserMessageReceived)E(line)[0].Payload).Content).IsEqualTo("one\ntwo");
    }

    /// <summary>Crew's first prompt opens with the agent system prompt, and the marker runs straight on from the
    /// preceding block with no newline.</summary>
    [Test]
    public async Task A_crew_first_prompt_keeps_only_the_users_request() {
        const string text = "[AGENT SYSTEM PROMPT]\nYou are Kiro -- powered by the Kiro Crew layer.\n[END AGENT SYSTEM PROMPT]\n"
                 + "[CRITICAL RULES -- always follow these]\nShow a diff.\n[END CRITICAL RULES]\n[RUNTIME] KiroCrew dashboard\n"
                 + "[REPLY FORMAT RULES]\n(ask that inline.)[CURRENT USER REQUEST -- respond to this]\nGive me an overview of this repository";

        await Assert.That(ContentOf(text)).IsEqualTo("Give me an overview of this repository");
    }

    [Test]
    public async Task A_crew_later_prompt_keeps_only_the_users_request() {
        const string text = "[RUNTIME] KiroCrew dashboard\nThis interface is authoritative.\n\n[PROJECT] Active project directory: /work/app\n"
                 + "[RESOURCES] Host memory is tight.\n[REPLY FORMAT RULES]\n(end with options)[CURRENT USER REQUEST -- respond to this]\n"
                 + "Why are the subagents queued?\nAnd are they running now?";

        await Assert.That(ContentOf(text)).IsEqualTo("Why are the subagents queued?\nAnd are they running now?");
    }

    [Test]
    [Arguments("What does this mean?\n[CURRENT USER REQUEST -- respond to this]\nsomething")]
    [Arguments("[RUNTIME] notes for the agent\n[CURRENT USER REQUEST -- respond to this]\ndo the thing")]
    [Arguments("[AGENT SYSTEM PROMPT]\nmine\n[CURRENT USER REQUEST -- respond to this]\ndo the thing")]
    [Arguments("[RUNTIME] KiroCrew dashboard\n(rules)[CURRENT USER REQUEST -- respond to this]\n   ")]
    public async Task A_prompt_crew_did_not_wrap_or_with_an_empty_request_is_kept_whole(string text) {
        await Assert.That(ContentOf(text)).IsEqualTo(text);
    }

    [Test]
    public async Task A_crew_request_keeps_its_leading_indentation() {
        const string text = "[RUNTIME] KiroCrew dashboard\n(rules)[CURRENT USER REQUEST -- respond to this]\n    var x = 1;\nwhy?";

        await Assert.That(ContentOf(text)).IsEqualTo("    var x = 1;\nwhy?");
    }

    [Test]
    public async Task A_prompt_with_no_text_emits_nothing_and_leaves_the_turn_time() {
        var ctx = Context();
        P(Prompt, ctx);

        const string empty = """{"version":"v1","kind":"Prompt","data":{"message_id":"m9","content":[{"kind":"text","data":""}],"meta":{"timestamp":1791384000}}}""";

        await Assert.That(E(empty, ctx)).IsEmpty();
        await Assert.That(ctx.LastPromptAt).IsEqualTo(PromptTime);
    }

    [Test]
    public async Task A_prompt_without_a_time_takes_the_earliest_instant_and_keeps_the_turn_time() {
        var ctx = Context();
        P(Prompt, ctx);

        var e = E("""{"version":"v1","kind":"Prompt","data":{"message_id":"m2","content":[{"kind":"text","data":"again"}]}}""", ctx)[0];

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(((UserMessageReceived)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(e.RecordTimestamp).IsNull();
        await Assert.That(ctx.LastPromptAt).IsEqualTo(PromptTime);
    }

    /// <summary>The server fails such a line when it converts the time, before it records it for the turn.</summary>
    [Test]
    public async Task A_prompt_time_outside_the_calendar_rejects_the_line_and_leaves_the_turn_time() {
        var ctx = Context();
        P(Prompt, ctx);

        var result = P(Prompt.Replace("1781727363", "999999999999", StringComparison.Ordinal), ctx);

        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(ctx.LastPromptAt).IsEqualTo(PromptTime);
    }

    // ── AssistantMessage ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task An_assistant_message_emits_its_text_then_its_tool_call_a_tick_apart() {
        var ctx = Context();
        P(Prompt, ctx);

        var events = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"On it."},{"kind":"toolUse","data":{"toolUseId":"tooluse_A","name":"execute_bash","input":{"__tool_use_purpose":"list","command":"ls"}}}]}}""", ctx);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(((AssistantTextGenerated)events[0].Payload).Content).IsEqualTo("On it.");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "m2", "text:0"));
        await Assert.That(events[0].Timestamp).IsEqualTo(PromptTime);
        await Assert.That(events[0].RecordTimestamp).IsEqualTo("2026-06-17T20:16:03.0000000+00:00");

        var call = ((AssistantToolCallsGenerated)events[1].Payload).ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("tooluse_A");
        await Assert.That(call.ToolName).IsEqualTo("execute_bash");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "m2", "tool:tooluse_A"));
        await Assert.That(events[1].Timestamp).IsEqualTo(PromptTime.AddTicks(1));
        await Assert.That(((AssistantToolCallsGenerated)events[1].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(PromptTime.AddTicks(1));

        await Assert.That(events.All(e => e.Usage is null && KiroSlug(e) is null)).IsTrue();
    }

    /// <summary>Kiro writes an empty text block beside a tool call; it emits nothing and takes no position.</summary>
    [Test]
    public async Task An_empty_text_block_emits_nothing_and_takes_no_position() {
        var events = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":""},{"kind":"toolUse","data":{"toolUseId":"t","name":"read","input":{}}},{"kind":"text","data":"after"}]}}""");

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo([CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.AssistantTextGenerated]);
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "m2", "text:1"));
    }

    [Test]
    public async Task Several_text_blocks_in_one_message_take_distinct_ids() {
        var events = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"First."},{"kind":"text","data":"Second."}]}}""");

        await Assert.That(events.Select(e => ((AssistantTextGenerated)e.Payload).Content)).IsEquivalentTo(["First.", "Second."]);
        await Assert.That(events[0].EventId).IsNotEqualTo(events[1].EventId);
    }

    [Test]
    [Arguments("""{"kind":"toolUse","data":{"name":"fs_read"}}""")]
    [Arguments("""{"kind":"toolUse","data":{"toolUseId":"t1"}}""")]
    [Arguments("""{"kind":"toolUse","data":"t1"}""")]
    [Arguments("""{"kind":"image","data":"x"}""")]
    [Arguments("\"text\"")]
    public async Task A_block_it_cannot_name_emits_nothing(string block) {
        await Assert.That(E($$$"""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{{{block}}}]}}""")).IsEmpty();
    }

    [Test]
    public async Task A_tool_input_that_is_not_an_object_leaves_the_arguments_unset() {
        var call = ((AssistantToolCallsGenerated)E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"toolUse","data":{"toolUseId":"t1","name":"execute_bash","input":"ls"}}]}}""")[0].Payload).ToolCalls[0];

        await Assert.That(call.Arguments).IsNull();
    }

    [Test]
    public async Task A_reply_before_any_prompt_takes_the_earliest_instant_and_no_record_time() {
        var events = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"a"},{"kind":"text","data":"b"}]}}""");

        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(events[1].Timestamp).IsEqualTo(DateTimeOffset.MinValue.AddTicks(1));
        await Assert.That(events.All(e => e.RecordTimestamp is null)).IsTrue();
    }

    [Test]
    public async Task The_turn_time_spans_batches() {
        var ctx = Context();
        P(Prompt, ctx);
        ctx.BeginBatch();

        var e = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"yo"}]}}""", ctx)[0];

        await Assert.That(e.Timestamp).IsEqualTo(PromptTime);
    }

    /// <summary>A caller resuming a stream past its prompt seeds the turn time.</summary>
    [Test]
    public async Task A_seeded_turn_time_stamps_the_turns_replies() {
        var ctx = Context();
        ctx.LastPromptAt = PromptTime;

        var e = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"yo"}]}}""", ctx)[0];

        await Assert.That(e.Timestamp).IsEqualTo(PromptTime);
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-06-17T20:16:03.0000000+00:00");
    }

    // ── usage the recorder copied from Kiro's sidecar ──────────────────────────────────────────

    [Test]
    public async Task Credits_and_context_fill_ride_the_kiro_slug_with_no_token_usage() {
        var e = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"Done."}],"_kcap_usage":{"credits":0.7672,"context_usage_percentage":5.2612}}}""")[0];

        var slug = KiroSlug(e)!;
        await Assert.That(slug.Fields["credits"].NumberValue).IsEqualTo(0.7672);
        await Assert.That(slug.Fields["context_usage_percentage"].NumberValue).IsEqualTo(5.2612);
        await Assert.That(e.Usage).IsNull();
    }

    /// <summary>The server's record writes every field, so a fill Kiro did not report is a null, not absent.</summary>
    [Test]
    public async Task A_missing_context_fill_is_null_on_the_slug() {
        var slug = KiroSlug(E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"Done."}],"_kcap_usage":{"credits":0.1}}}""")[0])!;

        await Assert.That(slug.Fields["context_usage_percentage"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
    }

    [Test]
    public async Task Positive_token_counts_with_a_model_are_the_turns_usage() {
        var e = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"Done."}],"_kcap_usage":{"credits":0.1,"context_usage_percentage":7.5,"input_token_count":1200,"output_token_count":340,"model":"minimax-m2.5"}}}""")[0];

        await Assert.That(e.Usage!.Model).IsEqualTo("minimax-m2.5");
        await Assert.That(e.Usage.InputTokens).IsEqualTo(1200L);
        await Assert.That(e.Usage.OutputTokens).IsEqualTo(340L);
        await Assert.That(e.Usage.TotalTokens).IsEqualTo(1540L);
        await Assert.That(e.UsageIsEcho).IsFalse();
        await Assert.That(KiroSlug(e)!.Fields["credits"].NumberValue).IsEqualTo(0.1);
    }

    /// <summary>The server drops a count that is not positive before it writes the usage, so it is neither stored nor summed.</summary>
    [Test]
    [Arguments("""{"input_token_count":5,"output_token_count":0,"model":"m"}""")]
    [Arguments("""{"input_token_count":5,"output_token_count":-3,"model":"m"}""")]
    [Arguments("""{"input_token_count":5,"model":"m"}""")]
    public async Task A_count_that_is_not_positive_is_left_out_of_the_usage(string usage) {
        var e = E($$$"""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"Done."}],"_kcap_usage":{{{usage}}}}}""")[0];

        await Assert.That(JsonFormatter.Default.Format(e.Usage!)).IsEqualTo("""{ "inputTokens": "5", "totalTokens": "5", "model": "m" }""");
    }

    [Test]
    [Arguments("""{"input_token_count":1200,"output_token_count":340}""")]
    [Arguments("""{"input_token_count":0,"output_token_count":0,"model":"auto","credits":0.1}""")]
    public async Task Tokens_without_a_model_or_a_positive_count_are_no_usage(string usage) {
        var e = E($$$"""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"Done."}],"_kcap_usage":{{{usage}}}}}""")[0];

        await Assert.That(e.Usage).IsNull();
    }

    [Test]
    public async Task Only_the_turns_first_event_carries_its_usage_whatever_its_kind() {
        var events = E("""{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":""},{"kind":"toolUse","data":{"toolUseId":"t1","name":"execute_bash","input":{"command":"ls"}}},{"kind":"text","data":"Ran it."}],"_kcap_usage":{"credits":0.33,"context_usage_percentage":6.1,"input_token_count":5,"model":"m"}}}""");

        await Assert.That(events[0].EventType).IsEqualTo(CanonicalEventTypes.AssistantToolCallsGenerated);
        await Assert.That(KiroSlug(events[0])!.Fields["credits"].NumberValue).IsEqualTo(0.33);
        await Assert.That(events[0].Usage!.InputTokens).IsEqualTo(5L);
        await Assert.That(KiroSlug(events[1])).IsNull();
        await Assert.That(events[1].Usage).IsNull();
    }

    // ── ToolResults ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_tool_result_carries_its_text_and_status_on_its_call_id() {
        var ctx = Context();
        P(Prompt, ctx);

        var e = E("""{"version":"v1","kind":"ToolResults","data":{"message_id":"m3","content":[{"kind":"toolResult","data":{"toolUseId":"tooluse_A","content":[{"kind":"text","data":"12\n"}],"status":"success"}}]}}""", ctx).Single();

        var result = (ToolResultReceived)e.Payload;
        await Assert.That(result.CallId).IsEqualTo("tooluse_A");
        await Assert.That(result.Result).IsEqualTo("12\n");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "tooluse_A", "result"));
        await Assert.That(e.Timestamp).IsEqualTo(PromptTime);

        var slug = KiroSlug(e)!;
        await Assert.That(slug.Fields["success"].BoolValue).IsTrue();
        await Assert.That(slug.Fields["tool_name"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
        await Assert.That(slug.Fields["status"].StringValue).IsEqualTo("success");
    }

    [Test]
    [Arguments("error", false)]
    [Arguments("cancelled", null)]
    public async Task A_status_other_than_success_reads_as_its_outcome(string status, bool? success) {
        var slug = KiroSlug(E($$$"""{"version":"v1","kind":"ToolResults","data":{"message_id":"m4","content":[{"kind":"toolResult","data":{"toolUseId":"tooluse_B","content":[{"kind":"text","data":"boom"}],"status":"{{{status}}}"}}]}}""")[0])!;

        await Assert.That(slug.Fields["status"].StringValue).IsEqualTo(status);
        await Assert.That(slug.Fields["success"].KindCase == Value.KindOneofCase.NullValue ? (bool?)null : slug.Fields["success"].BoolValue).IsEqualTo(success);
    }

    [Test]
    public async Task A_result_with_no_status_or_text_has_no_slug_and_no_result() {
        var e = E("""{"version":"v1","kind":"ToolResults","data":{"message_id":"m4","content":[{"kind":"toolResult","data":{"toolUseId":"t9"}}]}}""")[0];

        await Assert.That(KiroSlug(e)).IsNull();
        await Assert.That(((ToolResultReceived)e.Payload).Result).IsEmpty();
    }

    [Test]
    public async Task Each_result_block_with_a_call_id_is_its_own_event() {
        var events = E("""{"version":"v1","kind":"ToolResults","data":{"content":[{"kind":"toolResult","data":{"toolUseId":"a","status":"success"}},{"kind":"toolResult","data":{"content":[]}},{"kind":"text","data":"x"},{"kind":"toolResult","data":{"toolUseId":"b","status":"error"}}]}}""");

        await Assert.That(events.Select(e => ((ToolResultReceived)e.Payload).CallId)).IsEquivalentTo(["a", "b"]);
    }

    // ── KiroUsageBackfilled ────────────────────────────────────────────────────────────────────

    const string Backfill = """{"kind":"KiroUsageBackfilled","data":{"message_id":"msg-1","credits":2.0,"context_usage_percentage":40.0}}""";

    [Test]
    public async Task A_usage_backfill_restates_the_turns_credits_on_its_anchor() {
        var e = E(Backfill).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.KiroUsageBackfilled);
        var backfill = (KiroUsageBackfill)e.Payload;
        await Assert.That(backfill.Credits).IsEqualTo(2.0);
        await Assert.That(backfill.ContextUsagePercentage).IsEqualTo(40.0);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.KiroUsage(Session, "msg-1"));
    }

    [Test]
    public async Task A_resent_backfill_keeps_its_id_whatever_line_carries_it() {
        await Assert.That(P(Backfill, lineNumber: 2_000_000_009).Events[0].EventId).IsEqualTo(P(Backfill, lineNumber: 5).Events[0].EventId);
    }

    [Test]
    public async Task A_backfill_on_a_subagent_stream_is_scoped_by_the_agent() {
        await Assert.That(E(Backfill, Context(agentId: "agent-1"))[0].EventId).IsEqualTo(TranscriptIds.KiroUsage($"{Session}:agent-1", "msg-1"));
    }

    [Test]
    [Arguments("""{"kind":"KiroUsageBackfilled","data":{"credits":2.0,"context_usage_percentage":40.0}}""", "sess")]
    [Arguments("""{"kind":"KiroUsageBackfilled","data":{"message_id":"msg-1","credits":2.0}}""", "")]
    public async Task A_backfill_with_no_stable_id_is_dropped(string line, string sessionId) {
        await Assert.That(E(line, Context(sessionId))).IsEmpty();
    }

    [Test]
    public async Task A_backfill_without_credits_counts_zero() {
        await Assert.That(((KiroUsageBackfill)E("""{"kind":"KiroUsageBackfilled","data":{"message_id":"m"}}""")[0].Payload).Credits).IsEqualTo(0.0);
    }

    // ── ids ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The server keys a stream's messages by the session alone, a subagent's included.</summary>
    [Test]
    public async Task Message_ids_on_a_subagent_stream_are_keyed_by_the_session() {
        await Assert.That(E(Prompt, Context(agentId: "agent-1"))[0].EventId).IsEqualTo(TranscriptIds.KiroRecord(Session, "m1", "user"));
    }

    [Test]
    public async Task A_line_with_no_message_id_or_no_session_is_named_by_its_bytes() {
        const string line = """{"version":"v1","kind":"AssistantMessage","data":{"content":[{"kind":"text","data":"a"},{"kind":"toolUse","data":{"toolUseId":"t1","name":"x"}}]}}""";
        var events = E(line);

        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.KiroFallback(line, "text:0"));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.KiroFallback(line, "tool:t1"));
        await Assert.That(E(Prompt, Context(""))[0].EventId).IsEqualTo(TranscriptIds.KiroFallback(Prompt, "user"));
    }

    [Test]
    public async Task A_replayed_line_takes_the_same_ids() {
        const string line = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"y"},{"kind":"toolUse","data":{"toolUseId":"tooluse_A","name":"bash","input":{}}}]}}""";

        await Assert.That(E(line).Select(e => e.EventId)).IsEquivalentTo(E(line).Select(e => e.EventId));
    }

    // ── framing ────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"version":"v1","kind":"Thinking","data":{"text":"…"}}""")]
    [Arguments("""{"foo":"bar"}""")]
    [Arguments("""{"kind":"Prompt","data":"x"}""")]
    [Arguments("""{"kind":"AssistantMessage","data":{"content":"x"}}""")]
    [Arguments("""{"kind":"ToolResults","data":{}}""")]
    public async Task A_line_of_no_kind_it_reads_is_ignored(string line) {
        var result = P(line);

        await Assert.That(result.Events).IsEmpty();
        await Assert.That(result.Rejected).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[1,2,3]")]
    public async Task A_line_that_is_not_a_json_object_rejects(string line) {
        await Assert.That(P(line).Rejected).IsNotNull();
    }

    [Test]
    [Arguments("fs_read", AcpToolKind.Read)]
    [Arguments("fs_write", AcpToolKind.Edit)]
    [Arguments("execute_bash", AcpToolKind.Execute)]
    [Arguments("grep", AcpToolKind.Search)]
    [Arguments("web_fetch", AcpToolKind.Fetch)]
    [Arguments("thinking", AcpToolKind.Think)]
    [Arguments("use_aws", AcpToolKind.Other)]
    [Arguments("@github/search_code", AcpToolKind.Other)]
    public async Task A_tool_call_is_classified_by_its_native_name(string name, string kind) {
        await Assert.That(KiroToolKinds.Of(name)).IsEqualTo(kind);
    }
}
