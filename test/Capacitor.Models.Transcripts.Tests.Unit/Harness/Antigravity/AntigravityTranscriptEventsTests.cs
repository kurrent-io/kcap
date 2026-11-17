using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Antigravity;

public class AntigravityTranscriptEventsTests {
    static readonly AntigravityTranscriptEvents Projection = AntigravityTranscriptEvents.Instance;
    static readonly DateTimeOffset              Received   = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    static AntigravityContext Context(string sessionId = "sess", string? agentId = null) =>
        (AntigravityContext)Projection.CreateContext(sessionId, agentId);

    static IReadOnlyList<CanonicalEvent> Project(string line, AntigravityContext ctx, int lineNumber = 1) =>
        Projection.Project(line, lineNumber, Received, ctx).Events;

    static string Planner(int step, string calls, string time = "2026-07-02T19:04:11Z") =>
        $$"""{"step_index":{{step}},"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{time}}","tool_calls":[{{calls}}]}""";

    static string RunCommand(int step, string content, string time = "2026-07-02T19:04:12Z") =>
        $$"""{"step_index":{{step}},"source":"MODEL","type":"RUN_COMMAND","status":"DONE","created_at":"{{time}}","content":"{{content}}"}""";

    static string Call(string name = "run_command") => $$$"""{"name":"{{{name}}}","args":{}}""";

    static string CallIdOf(CanonicalEvent e) => ((AssistantToolCallsGenerated)e.Payload).ToolCalls[0].CallId;

    static string CallIdOfResult(CanonicalEvent e) => ((ToolResultReceived)e.Payload).CallId;

    static string Compact(IMessage message) => JsonNode.Parse(JsonFormatter.Default.Format(message))!.ToJsonString();

    // ── USER_INPUT ────────────────────────────────────────────────────────────

    [Test]
    public async Task A_user_input_keeps_only_the_text_inside_its_request_wrapper() {
        const string line = """{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:04:07Z","content":"<USER_REQUEST>\nAdd a goodbye function.\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: 2026-07-02T15:04:07-04:00.\n</ADDITIONAL_METADATA>"}""";

        var e = Project(line, Context()).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("Add a goodbye function.");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.AntigravityStep(line));
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-07-02T19:04:07Z");
        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:04:07Z", CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments("""{"type":"USER_INPUT","content":"  plain prompt \n"}""", "plain prompt")]
    [Arguments("""{"type":"USER_INPUT","content":"</USER_REQUEST>x<USER_REQUEST>"}""", "</USER_REQUEST>x<USER_REQUEST>")]
    public async Task A_user_input_without_a_wrapper_keeps_its_whole_trimmed_content(string line, string expected) {
        var e = Project(line, Context()).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"type":"USER_INPUT","content":"<USER_REQUEST>   </USER_REQUEST>"}""")]
    [Arguments("""{"type":"USER_INPUT","content":""}""")]
    [Arguments("""{"type":"USER_INPUT"}""")]
    public async Task A_user_input_with_no_text_emits_nothing(string line) =>
        await Assert.That(Project(line, Context())).IsEmpty();

    // ── PLANNER_RESPONSE ──────────────────────────────────────────────────────

    [Test]
    public async Task A_planner_emits_thinking_text_and_calls_each_a_tick_after_the_last() {
        const string line = """{"step_index":7,"type":"PLANNER_RESPONSE","created_at":"2026-07-02T19:04:10Z","thinking":"**Plan**","content":"Doing it.","tool_calls":[{"name":"view_file","args":{"AbsolutePath":"/a.py"}},{"name":"run_command","args":{"CommandLine":"ls"}}]}""";
        var at = DateTimeOffset.Parse("2026-07-02T19:04:10Z", CultureInfo.InvariantCulture);
        var id = TranscriptIds.AntigravityStep(line);

        var events = Project(line, Context());

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo([
            CanonicalEventTypes.AssistantThinkingGenerated, CanonicalEventTypes.AssistantTextGenerated,
            CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.AssistantToolCallsGenerated,
        ], CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo([
            TranscriptIds.Sibling(id, "thinking"), TranscriptIds.Sibling(id, "text"),
            TranscriptIds.Sibling(id, "toolcall:0"), TranscriptIds.Sibling(id, "toolcall:1"),
        ], CollectionOrdering.Matching);
        await Assert.That(events.Select(e => CanonicalEventTime.Of(e.Payload))).IsEquivalentTo(
            new DateTimeOffset?[] { at, at.AddTicks(1), at.AddTicks(2), at.AddTicks(3) }, CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.Timestamp)).IsEquivalentTo(
            [at, at.AddTicks(1), at.AddTicks(2), at.AddTicks(3)], CollectionOrdering.Matching);
        await Assert.That(events.All(e => e.RecordTimestamp == "2026-07-02T19:04:10Z")).IsTrue();
    }

    [Test]
    public async Task A_text_only_planner_takes_the_steps_own_time() {
        const string line = """{"step_index":11,"type":"PLANNER_RESPONSE","created_at":"2026-07-02T19:04:30Z","content":"Done."}""";

        var e = Project(line, Context()).Single();

        await Assert.That(((AssistantTextGenerated)e.Payload).Content).IsEqualTo("Done.");
        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:04:30Z", CultureInfo.InvariantCulture));
    }

    /// <summary>The transcript names no call, so each gets the step's sibling id in 32-hex form; the entry's index
    /// in <c>tool_calls</c> keys both ids, a skipped entry included.</summary>
    [Test]
    public async Task A_call_gets_an_id_derived_from_its_step_and_index() {
        var line = Planner(9, """{"args":{}},{"name":"run_command","args":{"CommandLine":"python hello.py","Cwd":"/repo"}}""");
        var id   = TranscriptIds.AntigravityStep(line);

        var e    = Project(line, Context()).Single();
        var call = ((AssistantToolCallsGenerated)e.Payload).ToolCalls.Single();

        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.Sibling(id, "toolcall:1"));
        await Assert.That(call.CallId).IsEqualTo(TranscriptIds.Sibling(id, "call:1").ToString("N"));
        await Assert.That(call.ToolName).IsEqualTo("run_command");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(Compact(call.Arguments)).IsEqualTo("""{"CommandLine":"python hello.py","Cwd":"/repo"}""");
    }

    [Test]
    public async Task A_call_without_object_args_has_no_arguments() {
        var e = Project(Planner(9, """{"name":"run_command","args":"x"}"""), Context()).Single();

        await Assert.That(((AssistantToolCallsGenerated)e.Payload).ToolCalls[0].Arguments).IsNull();
    }

    [Test]
    public async Task A_result_step_pairs_with_the_call_before_it() {
        var ctx  = Context();
        var call = Project(Planner(9, Call()), ctx).Single();

        var result = Project(RunCommand(10, "hello, world"), ctx).Single();

        await Assert.That(CallIdOfResult(result)).IsEqualTo(CallIdOf(call));
        await Assert.That(((ToolResultReceived)result.Payload).Result).IsEqualTo("hello, world");
        await Assert.That(result.LineNumber).IsNull();
    }

    [Test]
    public async Task Results_pair_with_waiting_calls_oldest_first() {
        var ctx   = Context();
        var calls = Project(Planner(2, $"{Call("view_file")},{Call("view_file")}"), ctx);

        var first  = Project("""{"step_index":3,"type":"VIEW_FILE","content":"a"}""", ctx).Single();
        var second = Project("""{"step_index":4,"type":"VIEW_FILE","content":"b"}""", ctx).Single();

        await Assert.That(CallIdOfResult(first)).IsEqualTo(CallIdOf(calls[0]));
        await Assert.That(CallIdOfResult(second)).IsEqualTo(CallIdOf(calls[1]));
    }

    [Test]
    public async Task A_result_with_empty_content_has_no_result_text() {
        var ctx = Context();
        Project(Planner(9, Call()), ctx);

        var result = (ToolResultReceived)Project(RunCommand(10, ""), ctx).Single().Payload;

        await Assert.That(result.HasResult).IsFalse();
    }

    // ── failures ──────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"step_index":10,"type":"RUN_COMMAND","content":"boom","error":"exit code 1"}""", true)]
    [Arguments("""{"step_index":10,"type":"VIEW_FILE","content":"x","error":"not found"}""", true)]
    [Arguments("""{"step_index":10,"type":"RUN_COMMAND","content":"Completed At: ...\nThe command failed with exit code: 5\nOutput:\nFAILED"}""", true)]
    [Arguments("""{"step_index":10,"type":"CODE_ACTION","content":"THE COMMAND FAILED WITH EXIT CODE:   2"}""", true)]
    [Arguments("""{"step_index":10,"type":"RUN_COMMAND","content":"The command failed with exit code: n/a"}""", false)]
    [Arguments("""{"step_index":10,"type":"VIEW_FILE","content":"The command failed with exit code: 5"}""", false)]
    [Arguments("""{"step_index":10,"type":"LIST_DIRECTORY","content":"The command failed with exit code: 5"}""", false)]
    [Arguments("""{"step_index":10,"type":"RUN_COMMAND","content":"The command completed successfully.","error":""}""", false)]
    public async Task Only_an_error_or_a_commands_failure_line_marks_a_result_failed(string line, bool failed) {
        var ctx = Context();
        Project(Planner(9, Call()), ctx);

        var result = (ToolResultReceived)Project(line, ctx).Single().Payload;

        await Assert.That(result.Extensions.ContainsKey(AntigravityExtension.Slug)).IsEqualTo(failed);
        if (failed) await Assert.That(Compact(result.Extensions[AntigravityExtension.Slug])).IsEqualTo("""{"is_error":true}""");
    }

    // ── interrupts ────────────────────────────────────────────────────────────

    [Test]
    public async Task Ask_question_is_an_input_interrupt_with_the_servers_elicitation_block() {
        var line = Planner(5, """{"name":"ask_question","args":{"questions":[{"is_multi_select":true,"question":"Which area?","options":["Add tests","",7,"Extend libraries"]},{"question":"ignored"}],"toolSummary":"Ask user"}}""", "2026-07-02T19:04:57Z");
        var id   = TranscriptIds.AntigravityStep(line);

        var e      = Project(line, Context()).Single();
        var issued = (InterruptIssued)e.Payload;

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.InterruptIssued);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.Sibling(id, "toolcall:0"));
        await Assert.That(issued.RequestId).IsEqualTo(TranscriptIds.Sibling(id, "call:0").ToString("N"));
        await Assert.That(issued.Kind).IsEqualTo("input");
        await Assert.That(issued.ToolName).IsEqualTo("AskUserQuestion");
        await Assert.That(issued.Prompt).IsEqualTo("Which area?");
        await Assert.That(Compact(issued.Timestamp)).IsEqualTo("\"2026-07-02T19:04:57Z\"");
        await Assert.That(Compact(issued.Extensions["claude_code"])).IsEqualTo(
            """{"elicitation":{"options":[{"label":"Add tests","description":null,"option_id":null,"kind":null},{"label":"Extend libraries","description":null,"option_id":null,"kind":null}],"is_multi_select":true,"selected_index":null,"selected_option_ids":null,"selected_option_labels":null,"min_selections":null,"max_selections":null}}""");
        await Assert.That(Compact(issued.Extensions[AntigravityExtension.Slug])).IsEqualTo("""{"native_name":"ask_question"}""");
    }

    [Test]
    [Arguments("""{"Action":"command","Reason":"To run git","Target":"git"}""", "git")]
    [Arguments("""{"Action":"command","Reason":"To run git"}""", "command")]
    [Arguments("""{"Target":7}""", "ask_permission")]
    public async Task Ask_permission_is_a_permission_interrupt_named_after_its_target(string args, string toolName) {
        var line = Planner(5, $$"""{"name":"ask_permission","args":{{args}}}""");

        var issued = (InterruptIssued)Project(line, Context()).Single().Payload;

        await Assert.That(issued.Kind).IsEqualTo("permission");
        await Assert.That(issued.ToolName).IsEqualTo(toolName);
        await Assert.That(issued.HasPrompt).IsFalse();
        await Assert.That(Compact(issued.Extensions["claude_code"])).IsEqualTo(
            $$$"""{"permission":{"tool_name":"{{{toolName}}}","tool_input":{{{JsonNode.Parse(args)!.ToJsonString()}}},"rule_id":null,"suggestion_type":null,"behavior":null,"updated_tool_input":null,"options":null}}""");
        await Assert.That(Compact(issued.Extensions[AntigravityExtension.Slug])).IsEqualTo("""{"native_name":"ask_permission"}""");
    }

    [Test]
    public async Task Ask_permission_without_args_has_a_null_tool_input() {
        var issued = (InterruptIssued)Project(Planner(5, """{"name":"ask_permission"}"""), Context()).Single().Payload;

        await Assert.That(issued.ToolName).IsEqualTo("ask_permission");
        await Assert.That(Compact(issued.Extensions["claude_code"])).Contains("\"tool_input\":null");
    }

    /// <summary>Whatever its arguments, an interaction tool is an interrupt and queues no call: a queued id no result
    /// answers would pair the next real result with the wrong call.</summary>
    [Test]
    [Arguments("""{"name":"ask_question","args":{"questions":[{"question":123}]}}""")]
    [Arguments("""{"name":"ask_question","args":{"questions":[]}}""")]
    [Arguments("""{"name":"ask_question","args":{"questions":"x"}}""")]
    [Arguments("""{"name":"ask_question"}""")]
    [Arguments("""{"name":"ask_permission","args":{}}""")]
    public async Task A_malformed_interaction_is_still_an_interrupt_and_queues_no_call(string call) {
        var ctx = Context();

        var e = Project(Planner(5, call), ctx).Single();
        var next = Project(RunCommand(6, "out"), ctx);

        await Assert.That(e.Payload).IsTypeOf<InterruptIssued>();
        await Assert.That(next).IsEmpty();
        if (e.Payload is InterruptIssued { Kind: "input" } question) {
            await Assert.That(question.Prompt).IsEqualTo("");
            await Assert.That(Compact(question.Extensions["claude_code"])).Contains("\"options\":[]");
        }
    }

    [Test]
    public async Task A_planner_mixing_a_call_and_a_question_emits_both_and_queues_only_the_call() {
        var ctx    = Context();
        var events = Project(Planner(5, """{"name":"view_file","args":{"path":"x"}},{"name":"ask_question","args":{"questions":[{"question":"Proceed?","options":["Yes"]}]}}"""), ctx);

        var result = Project("""{"step_index":6,"type":"VIEW_FILE","content":"x"}""", ctx).Single();

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.InterruptIssued], CollectionOrdering.Matching);
        await Assert.That(CallIdOfResult(result)).IsEqualTo(CallIdOf(events[0]));
    }

    [Test]
    public async Task An_interrupt_on_a_step_with_no_time_takes_the_receive_time() {
        var first  = (InterruptIssued)Project("""{"step_index":5,"type":"PLANNER_RESPONSE","tool_calls":[{"name":"ask_question","args":{}}]}""", Context()).Single().Payload;
        var second = Project("""{"step_index":5,"type":"PLANNER_RESPONSE","content":"x","tool_calls":[{"name":"ask_question","args":{}}]}""", Context())[1];

        await Assert.That(first.Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
        await Assert.That(second.Timestamp).IsEqualTo(DateTimeOffset.MinValue.AddTicks(1));
        await Assert.That(second.RecordTimestamp).IsNull();
    }

    // ── times ─────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"step_index":1,"type":"PLANNER_RESPONSE","content":"x"}""", null)]
    [Arguments("""{"step_index":1,"type":"PLANNER_RESPONSE","content":"x","created_at":"not a time"}""", "not a time")]
    public async Task A_step_without_a_parseable_time_is_stored_at_the_earliest_instant(string line, string? recordTimestamp) {
        var e = Project(line, Context()).Single();

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(CanonicalEventTime.Of(e.Payload)).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(e.RecordTimestamp).IsEqualTo(recordTimestamp);
    }

    // ── noise and rejected lines ──────────────────────────────────────────────

    [Test]
    [Arguments("""{"step_index":1,"source":"SYSTEM","type":"CONVERSATION_HISTORY","created_at":"2026-07-02T19:04:07Z","content":"…"}""")]
    [Arguments("""{"step_index":4,"source":"SYSTEM","type":"CHECKPOINT","created_at":"2026-07-02T19:04:09Z"}""")]
    [Arguments("""{"step_index":28,"source":"MODEL","type":"GENERIC","content":"You have 2 active subagent(s)"}""")]
    [Arguments("""{"step_index":29,"type":"INVOKE_SUBAGENT"}""")]
    [Arguments("""{"step_index":30,"content":"no type"}""")]
    [Arguments("""{"step_index":31,"type":7}""")]
    public async Task Noise_and_untyped_steps_emit_nothing(string line) {
        var result = Projection.Project(line, 1, Received, Context());

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json at all")]
    [Arguments("[1,2]")]
    [Arguments("\"USER_INPUT\"")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) {
        var result = Projection.Project(line, 1, Received, Context());

        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(result.Events).IsEmpty();
    }

    // ── USAGE ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_usage_line_is_a_backfill_with_reasoning_folded_into_output() {
        const string line = """{"step_index":5,"type":"USAGE","gen_row":5,"model":"gemini-3.5-flash-low","input_tokens":19622,"output_tokens":411,"cache_read_tokens":16281,"cache_write_tokens":24,"reasoning_tokens":156}""";

        var e = Project(line, Context("sess-usage-1")).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.AntigravityUsageBackfilled);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.AntigravityUsage("sess-usage-1", 5));
        await Assert.That(e.Payload).IsEqualTo(new AntigravityUsageBackfill {
            Model = "gemini-3.5-flash-low", InputTokens = 19622, OutputTokens = 411 + 156, CacheReadTokens = 16281, CacheWriteTokens = 24,
        });
    }

    [Test]
    public async Task A_usage_line_with_no_counts_has_zero_tokens_and_no_model() {
        var e = Project("""{"type":"USAGE","gen_row":2}""", Context()).Single();

        await Assert.That(e.Payload).IsEqualTo(new AntigravityUsageBackfill());
    }

    /// <summary>The id names the row in its stream, never the line, so a row re-sent with its keys reordered, its
    /// model renamed or a count corrected stays one event.</summary>
    [Test]
    public async Task A_usage_id_is_stable_across_restatements_of_its_row() {
        var ctx = Context("sess-scope-1");

        var a = Project("""{"type":"USAGE","gen_row":3,"model":"gemini-default","input_tokens":100,"output_tokens":10}""", ctx).Single();
        var b = Project("""{"gen_row":3,"input_tokens":120,"type":"USAGE","output_tokens":10,"model":"gemini-3.5-flash-low"}""", ctx).Single();
        var c = Project("""{"type":"USAGE","gen_row":4,"model":"gemini-default","input_tokens":100,"output_tokens":10}""", ctx).Single();

        await Assert.That(a.EventId).IsEqualTo(b.EventId);
        await Assert.That(a.EventId).IsNotEqualTo(c.EventId);
    }

    [Test]
    public async Task A_subagent_streams_usage_is_scoped_by_its_agent() {
        var ctx = Context("sess", "agent-1");

        var e = Project("""{"type":"USAGE","gen_row":3}""", ctx).Single();

        await Assert.That(ctx.IdScope).IsEqualTo("sess:agent-1");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.AntigravityUsage("sess:agent-1", 3));
        await Assert.That(Context("sess", "").IdScope).IsEqualTo("sess");
    }

    /// <summary>A row with no identity of its own is dropped: under a line hash a restated row would count twice.</summary>
    [Test]
    [Arguments("sess", """{"type":"USAGE","model":"gemini-default","input_tokens":100}""")]
    [Arguments("sess", """{"type":"USAGE","gen_row":"3","input_tokens":100}""")]
    [Arguments("sess", """{"type":"USAGE","gen_row":3.5,"input_tokens":100}""")]
    [Arguments("", """{"type":"USAGE","gen_row":3,"input_tokens":100}""")]
    public async Task A_usage_line_without_a_row_or_a_scope_emits_nothing(string sessionId, string line) =>
        await Assert.That(Project(line, Context(sessionId))).IsEmpty();

    [Test]
    public async Task A_usage_line_without_a_time_takes_the_last_steps() {
        var ctx = Context("sess-usage-ts");
        Project("""{"step_index":4,"type":"CHECKPOINT","created_at":"2026-07-02T19:04:20Z"}""", ctx);

        var e = Project("""{"type":"USAGE","gen_row":4}""", ctx).Single();

        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-07-02T19:04:20Z");
        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:04:20Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task A_usage_line_keeps_its_own_time_and_passes_it_on() {
        var ctx = Context();
        Project("""{"step_index":4,"type":"CHECKPOINT","created_at":"2026-07-02T19:04:20Z"}""", ctx);

        var own  = Project("""{"type":"USAGE","gen_row":4,"created_at":"2026-07-02T19:05:00Z"}""", ctx).Single();
        var next = Project("""{"type":"USAGE","gen_row":5}""", ctx).Single();

        await Assert.That(own.RecordTimestamp).IsEqualTo("2026-07-02T19:05:00Z");
        await Assert.That(next.RecordTimestamp).IsEqualTo("2026-07-02T19:05:00Z");
    }

    /// <summary>The server gives a row sent again the latest step's time; the store keeps the row's first event by
    /// id, so the projection keeps the time that event had.</summary>
    [Test]
    public async Task A_usage_row_sent_again_without_a_time_keeps_the_time_it_was_first_given() {
        var ctx = Context();
        Project("""{"step_index":4,"type":"CHECKPOINT","created_at":"2026-07-02T19:04:20Z"}""", ctx);
        var first = Project("""{"type":"USAGE","gen_row":4,"input_tokens":1}""", ctx).Single();
        Project("""{"step_index":5,"type":"CHECKPOINT","created_at":"2026-07-02T19:05:00Z"}""", ctx);

        var again = Project("""{"type":"USAGE","gen_row":4,"input_tokens":2}""", ctx).Single();
        var other = Project("""{"type":"USAGE","gen_row":5}""", ctx).Single();

        await Assert.That(again.EventId).IsEqualTo(first.EventId);
        await Assert.That(again.RecordTimestamp).IsEqualTo("2026-07-02T19:04:20Z");
        await Assert.That(again.Timestamp).IsEqualTo(first.Timestamp);
        await Assert.That(other.RecordTimestamp).IsEqualTo("2026-07-02T19:05:00Z");
    }

    [Test]
    public async Task A_usage_line_before_any_step_has_no_time() {
        var e = Project("""{"type":"USAGE","gen_row":0}""", Context()).Single();

        await Assert.That(e.RecordTimestamp).IsNull();
        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.MinValue);
    }

    // ── step order ────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"step_index":7,"type":"VIEW_FILE"}""", 7L)]
    [Arguments("""{"type":"USAGE","gen_row":3}""", long.MaxValue)]
    [Arguments("""{"step_index":"7"}""", long.MaxValue)]
    [Arguments("[7]", long.MaxValue)]
    [Arguments("not json", long.MaxValue)]
    [Arguments("", long.MaxValue)]
    public async Task The_step_order_is_the_step_index_and_anything_without_one_sorts_last(string line, long expected) =>
        await Assert.That(Projection.StepOrder(line)).IsEqualTo(expected);

    // ── held results ──────────────────────────────────────────────────────────

    [Test]
    public async Task A_result_written_before_its_planner_is_emitted_by_the_planner_under_its_own_line() {
        var ctx    = Context();
        var early  = RunCommand(6, "early", "2026-07-02T19:04:58Z");
        var held   = Project(early, ctx, lineNumber: 5);

        var events = Project(Planner(5, Call(), "2026-07-02T19:04:57Z"), ctx, lineNumber: 6);

        await Assert.That(held).IsEmpty();
        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived], CollectionOrdering.Matching);
        var result = events[1];
        await Assert.That(result.EventId).IsEqualTo(TranscriptIds.AntigravityStep(early));
        await Assert.That(CallIdOfResult(result)).IsEqualTo(CallIdOf(events[0]));
        await Assert.That(result.LineNumber).IsEqualTo(5);
        await Assert.That(result.RecordTimestamp).IsEqualTo("2026-07-02T19:04:58Z");
        await Assert.That(result.Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:04:58Z", CultureInfo.InvariantCulture));
        await Assert.That(events[0].LineNumber).IsNull();
    }

    /// <summary>A result follows its call, so a planner at the same or a higher step cannot own it.</summary>
    [Test]
    [Arguments(5)]
    [Arguments(3)]
    public async Task A_held_result_is_left_for_a_planner_below_its_step(int plannerStep) {
        var ctx = Context();
        Project(RunCommand(3, "stray"), ctx);

        var events = Project(Planner(plannerStep, Call()), ctx);
        var later  = Project(Planner(2, Call()), ctx);

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo([CanonicalEventTypes.AssistantToolCallsGenerated], CollectionOrdering.Matching);
        await Assert.That(later.Count(e => e.EventType == CanonicalEventTypes.ToolResultReceived)).IsEqualTo(1);
        await Assert.That(CallIdOfResult(later[1])).IsEqualTo(CallIdOf(events[0]));
    }

    /// <summary>Held across flushes, results can be held in any order; they pair with calls lowest step first.</summary>
    [Test]
    public async Task Held_results_are_claimed_in_step_order_not_the_order_they_were_held() {
        var ctx = Context();
        Project("""{"step_index":20,"type":"VIEW_FILE","content":"result-b"}""", ctx);
        Project("""{"step_index":18,"type":"VIEW_FILE","content":"result-a"}""", ctx);

        var events  = Project("""{"step_index":5,"type":"PLANNER_RESPONSE","tool_calls":[{"name":"view_file","args":{"f":"a"}},{"name":"view_file","args":{"f":"b"}}]}""", ctx);
        var calls   = events.Where(e => e.EventType == CanonicalEventTypes.AssistantToolCallsGenerated).Select(CallIdOf).ToArray();
        var results = events.Where(e => e.EventType == CanonicalEventTypes.ToolResultReceived).Select(e => (ToolResultReceived)e.Payload).ToArray();

        await Assert.That(results.Select(r => r.Result)).IsEquivalentTo(["result-a", "result-b"], CollectionOrdering.Matching);
        await Assert.That(results.Select(r => r.CallId)).IsEquivalentTo(calls, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_planner_claims_only_as_many_held_results_as_calls_are_waiting() {
        var ctx = Context();
        Project("""{"step_index":7,"type":"VIEW_FILE","content":"a"}""", ctx);
        Project("""{"step_index":8,"type":"VIEW_FILE","content":"b"}""", ctx);

        var first  = Project(Planner(5, Call("view_file")), ctx);
        var second = Project(Planner(6, Call("view_file")), ctx);

        await Assert.That(((ToolResultReceived)first[1].Payload).Result).IsEqualTo("a");
        await Assert.That(((ToolResultReceived)second[1].Payload).Result).IsEqualTo("b");
        await Assert.That(CallIdOfResult(second[1])).IsEqualTo(CallIdOf(second[0]));
    }

    /// <summary>A planner claims with the oldest waiting call, which can be an earlier planner's.</summary>
    [Test]
    public async Task A_held_result_takes_the_oldest_waiting_call() {
        var ctx = Context();
        Project(RunCommand(4, "early"), ctx);
        var oldest = Project(Planner(5, Call()), ctx).Single();

        var events = Project(Planner(3, Call()), ctx);

        await Assert.That(CallIdOfResult(events[1])).IsEqualTo(CallIdOf(oldest));
    }

    // ── a line delivered again ────────────────────────────────────────────────
    // The caller re-delivers lines it could not store, and lines after them; none of these depend on what it stored.

    [Test]
    public async Task A_held_result_delivered_again_before_its_planner_is_held_once() {
        var ctx  = Context();
        var line = """{"step_index":6,"type":"VIEW_FILE","content":"only once"}""";
        Project(line, ctx, 1);
        Project(line, ctx, 2);

        var events = Project("""{"step_index":5,"type":"PLANNER_RESPONSE","tool_calls":[{"name":"view_file","args":{}},{"name":"view_file","args":{}}]}""", ctx, 3);

        await Assert.That(events.Count(e => e.EventType == CanonicalEventTypes.ToolResultReceived)).IsEqualTo(1);
        await Assert.That(ctx.Held).IsEmpty();
    }

    [Test]
    public async Task A_held_results_own_line_delivered_after_its_planner_emits_nothing() {
        var ctx  = Context();
        var line = RunCommand(6, "early");
        Project(line, ctx);
        Project(Planner(5, Call()), ctx);

        await Assert.That(Project(line, ctx)).IsEmpty();
    }

    [Test]
    public async Task A_planner_delivered_again_queues_no_call_twice() {
        var ctx     = Context();
        var planner = Planner(1, Call());
        var calls   = Project(planner, ctx);
        var again   = Project(planner, ctx);

        var result = Project(RunCommand(2, "out"), ctx).Single();
        var next   = Project(RunCommand(3, "stray"), ctx);

        await Assert.That(again.Select(e => e.EventId)).IsEquivalentTo(calls.Select(e => e.EventId), CollectionOrdering.Matching);
        await Assert.That(CallIdOfResult(result)).IsEqualTo(CallIdOf(calls[0]));
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task A_result_delivered_again_keeps_its_call_and_takes_no_other() {
        var ctx   = Context();
        var calls = Project(Planner(1, $"{Call()},{Call()}"), ctx);
        var line  = RunCommand(2, "a");
        var first = Project(line, ctx).Single();

        var again = Project(line, ctx).Single();
        var next  = Project(RunCommand(3, "b"), ctx).Single();

        await Assert.That(again.EventId).IsEqualTo(first.EventId);
        await Assert.That(CallIdOfResult(again)).IsEqualTo(CallIdOf(calls[0]));
        await Assert.That(CallIdOfResult(next)).IsEqualTo(CallIdOf(calls[1]));
    }

    [Test]
    public async Task A_planner_delivered_again_emits_the_held_results_it_claimed_with_the_same_calls() {
        var ctx     = Context();
        var planner = Planner(5, Call());
        Project(RunCommand(6, "early"), ctx, 4);
        var first = Project(planner, ctx, 5);

        var again = Project(planner, ctx, 5);

        await Assert.That(again.Select(e => (e.EventId, e.LineNumber))).IsEquivalentTo(first.Select(e => (e.EventId, e.LineNumber)), CollectionOrdering.Matching);
        await Assert.That(CallIdOfResult(again[1])).IsEqualTo(CallIdOfResult(first[1]));
    }

    /// <summary>A store that fails one line re-delivers it with every line after it. A result the caller dropped or
    /// stored the first time still takes no call that belongs to a later result.</summary>
    [Test]
    public async Task Lines_delivered_again_after_a_failed_store_leave_the_next_result_its_own_call() {
        var ctx = Context();
        string[] lines = [
            """{"step_index":0,"type":"PLANNER_RESPONSE","created_at":"2026-09-08T01:00:00Z","content":"boom"}""",
            Planner(1, """{"name":"run_command","args":{"CommandLine":"a"}}"""),
            RunCommand(2, "a-out"),
            Planner(3, """{"name":"run_command","args":{"CommandLine":"b"}}"""),
        ];
        var first = lines.SelectMany(l => Project(l, ctx)).ToArray();

        foreach (var l in lines) Project(l, ctx);
        var result = Project(RunCommand(4, "b-out"), ctx).Single();

        var callB = first.Last(e => e.EventType == CanonicalEventTypes.AssistantToolCallsGenerated);
        await Assert.That(CallIdOfResult(result)).IsEqualTo(CallIdOf(callB));
    }

    [Test]
    public async Task What_the_caller_stored_does_not_change_what_a_line_projects_to() {
        var marked   = Context();
        var unmarked = Context();
        var planner  = Planner(5, Call());
        Project(RunCommand(6, "early"), marked);
        Project(RunCommand(6, "early"), unmarked);
        foreach (var e in Project(planner, marked)) marked.MarkPersisted(e.EventId);
        Project(planner, unmarked);
        marked.BeginBatch();
        unmarked.BeginBatch();

        var fromMarked   = Project(planner, marked);
        var fromUnmarked = Project(planner, unmarked);

        await Assert.That(fromMarked.Select(e => (e.EventId, Compact((IMessage)e.Payload))))
            .IsEquivalentTo(fromUnmarked.Select(e => (e.EventId, Compact((IMessage)e.Payload))), CollectionOrdering.Matching);
    }
}
