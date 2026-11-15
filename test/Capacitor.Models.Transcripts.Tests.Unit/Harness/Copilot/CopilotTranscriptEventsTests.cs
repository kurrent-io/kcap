using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>The rules of kcap-server's Copilot normalizer, one line at a time: what each line type emits, the ids it
/// emits them on, where usage and billing land, how attachments pair with their bytes, and which lines it ignores or
/// rejects. Forks are in <see cref="CopilotForkTests"/>.</summary>
public class CopilotTranscriptEventsTests {
    const string Session = "0c0c0c0c00004000800000000000000f";

    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    static CopilotContext Context() => (CopilotContext)CopilotTranscriptEvents.Instance.CreateContext(Session, null);

    static ProjectionResult P(string line, CopilotContext? ctx = null) =>
        CopilotTranscriptEvents.Instance.Project(line, 1, Received, ctx ?? Context());

    static IReadOnlyList<CanonicalEvent> E(string line, CopilotContext? ctx = null) => P(line, ctx).Events;

    static List<CanonicalEvent> All(IEnumerable<string> lines, CopilotContext? ctx = null) {
        var context = ctx ?? Context();
        return [.. lines.SelectMany(line => P(line, context).Events)];
    }

    static string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";

    static string Envelope(int n, string type, string data, string? agentId = null) =>
        agentId is null
            ? $$"""{"type":"{{type}}","data":{{data}},"id":"{{Id(n)}}","timestamp":"2026-11-05T02:23:{{n % 60:D2}}Z","parentId":null}"""
            : $$"""{"type":"{{type}}","data":{{data}},"agentId":"{{agentId}}","id":"{{Id(n)}}","timestamp":"2026-11-05T02:23:{{n % 60:D2}}Z","parentId":null}""";

    static string Slug(object payload) => JsonFormatter.Default.Format(SchemaExtensions.Slug(payload, CopilotExtension.Slug)!);

    static T Single<T>(IEnumerable<CanonicalEvent> events) => events.Select(e => e.Payload).OfType<T>().Single();

    // ── session.start ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_start_is_a_session_started_carrying_the_transcripts_git_context() {
        var line = """{"type":"session.start","data":{"sessionId":"1053bee4-574f-40e3-84ca-463bd7a82dc2","version":1,"producer":"copilot-agent","copilotVersion":"1.0.61","startTime":"2026-06-10T20:23:49.363Z","context":{"cwd":"/tmp/work","gitRoot":"/tmp/work","branch":"main","repository":"kurrent-io/scratch","hostType":"github","repositoryHost":"github.com"}},"id":"1499ffe1-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}""";

        var e       = E(line).Single();
        var started = (SessionStarted)e.Payload;

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.SessionStarted);
        await Assert.That(e.EventId).IsEqualTo(Guid.Parse("1499ffe1-8ac5-413f-add7-0998b8e1d47b"));
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-06-10T20:23:49.371Z");
        await Assert.That(started.Timestamp.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:49.371Z"));
        await Assert.That(Slug(started)).IsEqualTo(
            """{ "cwd": "/tmp/work", "git_root": "/tmp/work", "branch": "main", "repository": "kurrent-io/scratch", "repository_host": "github.com", "host_type": "github", "copilot_version": "1.0.61", "producer": "copilot-agent", "source": null }""");
        await Assert.That(started.HasPreviousSessionId).IsFalse();
        await Assert.That(e.VendorMetadata).IsNull();
    }

    /// <summary>The server stamps its own capacitor slug on a start it writes; the library writes none.</summary>
    [Test]
    public async Task A_start_carries_no_capacitor_slug() {
        var e = E(Envelope(1, "session.start", """{"context":{}}""")).Single();

        await Assert.That(SchemaExtensions.Slug(e.Payload, CapacitorExtension.Slug)).IsNull();
    }

    [Test]
    public async Task A_vs_code_local_start_without_context_still_starts_the_session() {
        var line = """{"type":"session.start","data":{"sessionId":"d72e59eb-1ced-4194-8df4-c3ae346b63d7","version":1,"producer":"copilot-agent","copilotVersion":"0.73.0","vscodeVersion":"1.145.0","startTime":"2026-11-05T03:39:56.150Z"},"id":"24b4ba4b-0543-4afc-b3c5-aa128944879d","timestamp":"2026-11-05T03:39:56.150Z","parentId":null}""";

        await Assert.That(Slug(E(line).Single().Payload)).IsEqualTo(
            """{ "cwd": null, "git_root": null, "branch": null, "repository": null, "repository_host": null, "host_type": null, "copilot_version": "0.73.0", "producer": "copilot-agent", "source": null }""");
    }

    // ── user.message ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_prompt_is_its_clean_content_not_the_transformed_one() {
        var line = """{"type":"user.message","data":{"content":"Reply with exactly: ok","transformedContent":"<current_datetime>2026-06-10</current_datetime>\n\nReply with exactly: ok\n\n<system_reminder>noise</system_reminder>","attachments":[],"interactionId":"i-1"},"id":"b4fbc5e5-3847-423d-8442-acd4bf05a65e","timestamp":"2026-06-10T20:23:50.801Z","parentId":"f77e5cfc-9153-4016-8610-7f873fc65471"}""";

        var e = E(line).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(e.EventId).IsEqualTo(Guid.Parse("b4fbc5e5-3847-423d-8442-acd4bf05a65e"));
        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("Reply with exactly: ok");
        await Assert.That(SchemaExtensions.Slug(e.Payload, CopilotExtension.Slug)).IsNull();
        await Assert.That(e.Attachments).IsNull();
    }

    [Test]
    public async Task A_prompt_with_neither_text_nor_attachments_emits_nothing() =>
        await Assert.That(E(Envelope(1, "user.message", """{"content":"","attachments":[]}"""))).IsEmpty();

    [Test]
    public async Task A_subagents_prompt_carries_its_agent_id() {
        var line = """{"type":"user.message","data":{"content":"Read the file a.txt","source":"agent-494ed425-f7f6-494a-99cb-4ab2ef7831fe"},"agentId":"56327224-7e62-4f04-ad66-dd8b4168efc8","id":"bf931c6e-aeca-4ed8-93cc-2fb5037d15db","timestamp":"2026-11-05T02:23:08.896Z","parentId":"dc1b61c5"}""";

        await Assert.That(Slug(E(line).Single().Payload)).IsEqualTo("""{ "agent_id": "56327224-7e62-4f04-ad66-dd8b4168efc8" }""");
    }

    // ── attachments ────────────────────────────────────────────────────────────────────────────────

    static string AssetLine(int n, string assetId, string mimeType, byte[] bytes) =>
        Envelope(n, "session.binary_asset", $$"""{"assetId":"{{assetId}}","type":"image","mimeType":"{{mimeType}}","byteLength":{{bytes.Length}},"data":"{{Convert.ToBase64String(bytes)}}"}""");

    static string Prompt(int n, string content, params (string DisplayName, string AssetId, string? MimeType)[] attachments) {
        var listed = string.Join(",", attachments.Select(a => a.MimeType is null
            ? $$"""{"type":"file","displayName":"{{a.DisplayName}}","assetId":"{{a.AssetId}}"}"""
            : $$"""{"type":"file","displayName":"{{a.DisplayName}}","assetId":"{{a.AssetId}}","mimeType":"{{a.MimeType}}"}"""));

        return Envelope(n, "user.message", $$"""{"content":{{JsonSerializer.Serialize(content)}},"attachments":[{{listed}}]}""");
    }

    [Test]
    public async Task An_asset_line_emits_nothing_and_its_prompt_carries_its_bytes() {
        var png = new byte[] { 10, 20, 30 };
        var pdf = new byte[] { 40, 50, 60 };
        var ctx = Context();

        await Assert.That(E(AssetLine(1, "sha256:img", "image/png", png), ctx)).IsEmpty();
        await Assert.That(E(AssetLine(2, "sha256:doc", "application/pdf", pdf), ctx)).IsEmpty();

        var e = E(Prompt(3, "look at these", ("red.png", "sha256:img", "image/png"), ("doc.pdf", "sha256:doc", null)), ctx).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("look at these");
        await Assert.That(e.Attachments!.Select(a => (a.FileName, a.ContentType))).IsEquivalentTo(new[] { ("red.png", "image/png"), ("doc.pdf", "application/pdf") });
        await Assert.That(e.Attachments![0].Data).IsEquivalentTo(png);
        await Assert.That(e.Attachments![1].Data).IsEquivalentTo(pdf);
        await Assert.That(e.Attachments![0].Id).IsEqualTo(TranscriptIds.Sibling(Guid.Parse(Id(3)), "att:sha256:img"));
        await Assert.That(ctx.Assets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_attachment_without_a_display_name_is_named_by_its_asset_id() {
        var ctx = Context();
        P(AssetLine(1, "sha256:x", "image/png", [1]), ctx);

        var e = E(Prompt(2, "x", ("", "sha256:x", null)), ctx).Single();

        await Assert.That(e.Attachments!.Single().FileName).IsEqualTo("sha256:x");
    }

    [Test]
    public async Task A_prompt_with_only_an_attachment_is_still_a_turn() {
        var ctx = Context();
        P(AssetLine(1, "sha256:only", "image/png", [7, 7, 7]), ctx);

        var e = E(Prompt(2, "", ("shot.png", "sha256:only", "image/png")), ctx).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("");
        await Assert.That(e.Attachments!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_attachment_whose_bytes_never_arrived_is_left_off() {
        var e = E(Prompt(1, "text here", ("missing.png", "sha256:nope", "image/png"))).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("text here");
        await Assert.That(e.Attachments).IsNull();
    }

    [Test]
    public async Task An_asset_that_is_not_base64_is_dropped_and_its_prompt_lands_bare() {
        var ctx = Context();
        P(Envelope(1, "session.binary_asset", """{"assetId":"sha256:bad","mimeType":"image/png","data":"!!!not-base64!!!"}"""), ctx);

        await Assert.That(ctx.Assets.Count).IsEqualTo(0);
        await Assert.That(E(Prompt(2, "still here", ("x.png", "sha256:bad", "image/png")), ctx).Single().Attachments).IsNull();
    }

    [Test]
    [Arguments("""{"mimeType":"image/png","data":"AQID"}""")]
    [Arguments("""{"assetId":"sha256:a","data":"AQID"}""")]
    [Arguments("""{"assetId":"sha256:a","mimeType":"image/png","data":""}""")]
    public async Task An_asset_missing_its_id_type_or_bytes_is_dropped(string data) {
        var ctx = Context();
        P(Envelope(1, "session.binary_asset", data), ctx);

        await Assert.That(ctx.Assets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_asset_outlives_a_batch_boundary() {
        var ctx = Context();
        P(AssetLine(1, "sha256:survive", "image/png", [9, 8, 7]), ctx);
        ctx.BeginBatch();

        await Assert.That(E(Prompt(2, "cross-batch", ("keep.png", "sha256:survive", "image/png")), ctx).Single().Attachments![0].Data)
            .IsEquivalentTo(new byte[] { 9, 8, 7 });
    }

    /// <summary>A prompt retried after a failed store pairs again: the lookup leaves the bytes in place.</summary>
    [Test]
    public async Task A_retried_prompt_pairs_again_under_the_same_ids() {
        var ctx    = Context();
        var prompt = Prompt(2, "x", ("d.png", "sha256:det", "image/png"));
        P(AssetLine(1, "sha256:det", "image/png", [3, 1, 4]), ctx);

        var first = E(prompt, ctx).Single().Attachments!.Single();
        var again = E(prompt, ctx).Single().Attachments!.Single();

        await Assert.That(again.Id).IsEqualTo(first.Id);
        await Assert.That(again.Data).IsEquivalentTo(first.Data);
    }

    // ── assistant.message ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_text_only_reply_carries_its_output_and_model() {
        var line = """{"type":"assistant.message","data":{"messageId":"m1","model":"claude-sonnet-4.6","content":"ok","toolRequests":[],"outputTokens":5},"id":"35037a88-eb95-4e8b-9c39-d2602b5881dc","timestamp":"2026-06-10T20:23:55Z","parentId":"p1"}""";

        var e = E(line).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.Sibling(Guid.Parse("35037a88-eb95-4e8b-9c39-d2602b5881dc"), "text"));
        await Assert.That(((AssistantTextGenerated)e.Payload).Content).IsEqualTo("ok");
        await Assert.That(JsonFormatter.Default.Format(e.Usage!)).IsEqualTo("""{ "outputTokens": "5", "totalTokens": "5", "model": "claude-sonnet-4.6" }""");
    }

    [Test]
    public async Task A_reply_emits_thinking_text_and_each_tool_request_in_order_a_tick_apart() {
        var line = """{"type":"assistant.message","data":{"messageId":"m2","model":"claude-sonnet-4.6","content":"On it.","toolRequests":[{"toolCallId":"toolu_A","name":"report_intent","arguments":{"intent":"Creating hello.txt"},"type":"function"},{"toolCallId":"toolu_B","name":"create","arguments":{"path":"/tmp/work/hello.txt","file_text":"hello world"},"type":"function"}],"reasoningText":"User wants a file created.","reasoningOpaque":"BLOB","outputTokens":300},"id":"0d5668ad-cf68-4c8c-a141-a5ad7151d20a","timestamp":"2026-06-10T20:23:58.761Z","parentId":"p2"}""";
        var id   = Guid.Parse("0d5668ad-cf68-4c8c-a141-a5ad7151d20a");
        var at   = DateTimeOffset.Parse("2026-06-10T20:23:58.761Z");

        var events = E(line);

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(new[] {
            CanonicalEventTypes.AssistantThinkingGenerated, CanonicalEventTypes.AssistantTextGenerated,
            CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.AssistantToolCallsGenerated,
        }, CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo(new[] {
            TranscriptIds.Sibling(id, "thinking"), TranscriptIds.Sibling(id, "text"), TranscriptIds.Sibling(id, "tool:toolu_A"), TranscriptIds.Sibling(id, "tool:toolu_B"),
        }, CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.Timestamp)).IsEquivalentTo(new[] { at, at.AddTicks(1), at.AddTicks(2), at.AddTicks(3) },
            CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.RecordTimestamp).Distinct()).IsEquivalentTo(new string?[] { "2026-06-10T20:23:58.761Z" });

        await Assert.That(events[0].Usage!.OutputTokens).IsEqualTo(300L);
        await Assert.That(events.Skip(1).All(e => e.Usage is null)).IsTrue();

        var create = ((AssistantToolCallsGenerated)events[3].Payload).ToolCalls.Single();
        await Assert.That(create.CallId).IsEqualTo("toolu_B");
        await Assert.That(create.ToolKind).IsEqualTo(AcpToolKind.Edit);
        await Assert.That(create.Arguments.Fields["path"].StringValue).IsEqualTo("/tmp/work/hello.txt");
        await Assert.That(((AssistantToolCallsGenerated)events[2].Payload).ToolCalls.Single().ToolKind).IsEqualTo(AcpToolKind.Other);
        await Assert.That(((AssistantToolCallsGenerated)events[3].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(at.AddTicks(3));
    }

    /// <summary>With no output count the line names no model either: usage needs a token count.</summary>
    [Test]
    public async Task A_reply_without_an_output_count_carries_no_usage() =>
        await Assert.That(E(Envelope(1, "assistant.message", """{"model":"claude-haiku-4.5","content":"ok"}""")).Single().Usage).IsNull();

    [Test]
    public async Task A_reply_with_nothing_to_show_emits_nothing() =>
        await Assert.That(E(Envelope(1, "assistant.message", """{"model":"m","content":"","toolRequests":[{"name":"bash"}],"outputTokens":3}"""))).IsEmpty();

    [Test]
    public async Task A_subagents_reply_carries_its_agent_id() {
        var line = """{"type":"assistant.message","data":{"model":"claude-haiku-4.5","content":"Found 2 files.","toolRequests":[],"outputTokens":50},"id":"5d5668ad-cf68-4c8c-a141-a5ad7151d20a","timestamp":"2026-06-10T20:24:05Z","parentId":"p5","agentId":"toolu_01G9Tq"}""";

        await Assert.That(Slug(E(line).Single().Payload)).IsEqualTo("""{ "agent_id": "toolu_01G9Tq" }""");
    }

    [Test]
    public async Task A_local_tool_requests_json_text_arguments_are_parsed() {
        var line = """{"type":"assistant.message","data":{"content":"Reading it.","toolRequests":[{"toolCallId":"call_x","name":"read_file","arguments":"{\"filePath\":\"/work/README.md\",\"startLine\":1}","type":"function"}]},"id":"768b5ce5-c98e-4784-a2f8-b89973f353f6","timestamp":"2026-11-05T03:39:59.153Z","parentId":"p"}""";

        var call = ((AssistantToolCallsGenerated)E(line)[1].Payload).ToolCalls.Single();

        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Read);
        await Assert.That(call.Arguments.Fields["filePath"].StringValue).IsEqualTo("/work/README.md");
    }

    [Test]
    [Arguments("\"not json\"")]
    [Arguments("\"[1,2]\"")]
    [Arguments("\"\"")]
    [Arguments("null")]
    [Arguments("7")]
    public async Task A_tool_request_with_unusable_arguments_keeps_the_call_without_them(string arguments) {
        var line = $$"""{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"call_x","name":"read_file","arguments":{{arguments}}}]},"id":"768b5ce5-c98e-4784-a2f8-b89973f353f6","timestamp":"2026-11-05T03:39:59.153Z"}""";

        var call = ((AssistantToolCallsGenerated)E(line).Single().Payload).ToolCalls.Single();

        await Assert.That(call.CallId).IsEqualTo("call_x");
        await Assert.That(call.Arguments).IsNull();
    }

    // ── tool.execution_complete ────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_completion_is_a_result_with_its_outcome_and_detail() {
        var line = """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_A","success":true,"result":{"content":"Intent logged","detailedContent":"Creating hello.txt"},"toolTelemetry":{}},"id":"3e386233-0a46-403c-a518-302bec1a22df","timestamp":"2026-06-10T20:23:58.800Z","parentId":"p3"}""";

        var e      = E(line).Single();
        var result = (ToolResultReceived)e.Payload;

        await Assert.That(e.EventId).IsEqualTo(Guid.Parse("3e386233-0a46-403c-a518-302bec1a22df"));
        await Assert.That(result.CallId).IsEqualTo("toolu_A");
        await Assert.That(result.Result).IsEqualTo("Intent logged");
        await Assert.That(Slug(result)).IsEqualTo("""{ "success": true, "detailed_content": "Creating hello.txt", "agent_id": null }""");
    }

    [Test]
    public async Task A_failed_completion_says_so() {
        var result = (ToolResultReceived)E(Envelope(1, "tool.execution_complete", """{"toolCallId":"toolu_F","success":false,"result":{"content":"command not found"}}""")).Single().Payload;

        await Assert.That(Slug(result)).IsEqualTo("""{ "success": false, "detailed_content": null, "agent_id": null }""");
    }

    [Test]
    public async Task A_local_completion_without_a_result_carries_its_outcome_only() {
        var result = (ToolResultReceived)E(Envelope(1, "tool.execution_complete", """{"toolCallId":"call_x","success":true}""")).Single().Payload;

        await Assert.That(result.Result).IsEqualTo("");
        await Assert.That(Slug(result)).IsEqualTo("""{ "success": true, "detailed_content": null, "agent_id": null }""");
    }

    [Test]
    public async Task A_structured_result_is_kept_as_its_json_and_a_detail_equal_to_it_is_dropped() {
        var result = (ToolResultReceived)E(Envelope(1, "tool.execution_complete",
            """{"toolCallId":"c","result":{"content":[{"type":"text","text":"x"}],"detailedContent":"[{\"type\":\"text\",\"text\":\"x\"}]"}}""")).Single().Payload;

        await Assert.That(result.Result).IsEqualTo("""[{"type":"text","text":"x"}]""");
        await Assert.That(SchemaExtensions.Slug(result, CopilotExtension.Slug)).IsNull();
    }

    [Test]
    public async Task A_subagents_completion_names_it() {
        var result = (ToolResultReceived)E(Envelope(1, "tool.execution_complete", """{"toolCallId":"c","result":{"content":"ok"}}""", "agent-a")).Single().Payload;

        await Assert.That(Slug(result)).IsEqualTo("""{ "success": null, "detailed_content": null, "agent_id": "agent-a" }""");
    }

    [Test]
    public async Task A_completion_without_a_call_id_emits_nothing() =>
        await Assert.That(E(Envelope(1, "tool.execution_complete", """{"success":true}"""))).IsEmpty();

    // ── usage records, shutdowns and checkpoints ───────────────────────────────────────────────────

    static string Record(int n, string model, long input, long output, long read, long write, string? agentId = null) =>
        Envelope(n, "session.usage_record",
            $$$"""{"usage":{"model":"{{{model}}}","inputTokens":{{{input}}},"outputTokens":{{{output}}},"cacheReadTokens":{{{read}}},"cacheWriteTokens":{{{write}}},"reasoningTokens":7}}""", agentId);

    [Test]
    public async Task A_usage_record_is_one_backfill_on_its_envelope_id_net_of_cache() {
        var e        = E(Record(1, "claude-haiku-4.5", 40340, 633, 0, 40330)).Single();
        var backfill = (CopilotUsageBackfill)e.Payload;

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.CopilotUsageBackfilled);
        await Assert.That(e.EventId).IsEqualTo(Guid.Parse(Id(1)));
        await Assert.That(backfill).IsEqualTo(new CopilotUsageBackfill {
            Model = "claude-haiku-4.5", InputTokens = 10, OutputTokens = 633, CacheReadTokens = 0, CacheWriteTokens = 40330, ReasoningTokens = 7,
            SessionModel = "claude-haiku-4.5",
        });
        await Assert.That(e.Usage).IsNull();
    }

    [Test]
    public async Task A_subagents_usage_record_never_names_the_session_model() =>
        await Assert.That(((CopilotUsageBackfill)E(Record(1, "gpt-5.6-luna", 25857, 94, 0, 25854, "agent-a")).Single().Payload).SessionModel).IsNull();

    [Test]
    [Arguments("""{"usage":{"inputTokens":5,"outputTokens":1}}""")]
    [Arguments("""{"usage":{"model":"","inputTokens":5}}""")]
    [Arguments("""{"model":"claude-haiku-4.5"}""")]
    public async Task A_usage_record_naming_no_model_emits_nothing(string data) =>
        await Assert.That(E(Envelope(1, "session.usage_record", data))).IsEmpty();

    [Test]
    public async Task A_shutdown_backfills_each_model_net_of_cache_with_no_output() {
        var line = """{"type":"session.shutdown","data":{"shutdownType":"routine","modelMetrics":{"claude-sonnet-4.6":{"usage":{"inputTokens":51647,"outputTokens":581,"cacheReadTokens":43694,"cacheWriteTokens":7946,"reasoningTokens":41}},"claude-haiku-4.5":{"usage":{"inputTokens":26417,"outputTokens":963,"cacheReadTokens":6255,"cacheWriteTokens":20100,"reasoningTokens":356}}}},"id":"1c3cd396-e283-4d76-8118-1a30d9d17617","timestamp":"2026-06-10T20:24:40Z","parentId":null}""";
        var id   = Guid.Parse("1c3cd396-e283-4d76-8118-1a30d9d17617");

        var events = E(line);

        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo(new[] { id, TranscriptIds.Sibling(id, "usage:claude-haiku-4.5") },
            CollectionOrdering.Matching);
        await Assert.That((CopilotUsageBackfill)events[0].Payload).IsEqualTo(new CopilotUsageBackfill {
            Model = "claude-sonnet-4.6", InputTokens = 51647 - 43694 - 7946, OutputTokens = 0, CacheReadTokens = 43694, CacheWriteTokens = 7946, ReasoningTokens = 41,
        });
        await Assert.That(((CopilotUsageBackfill)events[1].Payload).CacheWriteTokens).IsEqualTo(20100L);
    }

    /// <summary>The envelope id goes to the first model with a token count; a billing-only model ahead of it takes a
    /// sibling id and is emitted after it.</summary>
    [Test]
    public async Task A_shutdowns_envelope_id_goes_to_the_first_model_with_tokens() {
        var events = E(Envelope(1, "session.shutdown",
            """{"modelMetrics":{"gpt-x":{"requests":{"count":1,"cost":1},"totalNanoAiu":500},"claude-haiku-4.5":{"usage":{"inputTokens":10,"cacheReadTokens":4}}}}"""));
        var id = Guid.Parse(Id(1));

        await Assert.That(events.Select(e => (e.EventId, ((CopilotUsageBackfill)e.Payload).Model))).IsEquivalentTo(new[] {
            (id, (string?)"claude-haiku-4.5"), (TranscriptIds.Sibling(id, "usage:gpt-x"), "gpt-x"),
        }, CollectionOrdering.Matching);
        await Assert.That((CopilotUsageBackfill)events[1].Payload).IsEqualTo(new CopilotUsageBackfill { Model = "gpt-x", RequestCount = 1, RequestCost = 1, NanoAiu = 500 });
    }

    [Test]
    public async Task A_shutdown_model_with_neither_tokens_nor_billing_is_skipped() =>
        await Assert.That(E(Envelope(1, "session.shutdown",
            """{"modelMetrics":{"claude-sonnet-4.6":{"usage":{"inputTokens":0,"outputTokens":12,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0}}}}"""))).IsEmpty();

    [Test]
    public async Task A_shutdown_reports_its_billing_and_code_changes_on_a_sibling_id() {
        var events = E(Envelope(1, "session.shutdown",
            """{"currentModel":"claude-sonnet-4.6","totalNanoAiu":3109455000,"totalPremiumRequests":1,"codeChanges":{"linesAdded":4,"linesRemoved":2,"filesModified":["a.cs","b.cs"]},"modelMetrics":{"claude-sonnet-4.6":{"requests":{"count":3,"cost":1},"usage":{"inputTokens":10,"outputTokens":55,"cacheReadTokens":5,"cacheWriteTokens":2,"reasoningTokens":0},"totalNanoAiu":3109455000}}}"""));

        await Assert.That(events[0].Payload).IsEqualTo(new CopilotUsageBackfill {
            Model = "claude-sonnet-4.6", InputTokens = 3, CacheReadTokens = 5, CacheWriteTokens = 2, ReasoningTokens = 0,
            SessionModel = "claude-sonnet-4.6", RequestCount = 3, RequestCost = 1, NanoAiu = 3109455000,
        });

        var metrics = (CopilotSessionMetrics)events[1].Payload;
        await Assert.That(events[1].EventType).IsEqualTo(CanonicalEventTypes.CopilotSessionMetrics);
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.Sibling(Guid.Parse(Id(1)), "session-metrics"));
        await Assert.That((metrics.TotalNanoAiu, metrics.TotalPremiumRequests, metrics.LinesAdded, metrics.LinesRemoved, metrics.FilesModifiedCount))
            .IsEqualTo((3109455000L, 1d, 4, 2, 2));
        await Assert.That(metrics.FilesModified).IsEquivalentTo(new[] { "a.cs", "b.cs" });
        await Assert.That(metrics.CumulativeAcrossRuns).IsNull();
    }

    [Test]
    public async Task A_shutdown_reporting_nothing_emits_nothing() =>
        await Assert.That(E(Envelope(1, "session.shutdown", """{"shutdownType":"routine"}"""))).IsEmpty();

    [Test]
    public async Task A_shutdown_reporting_zero_code_changes_still_reports_them() {
        var metrics = Single<CopilotSessionMetrics>(E(Envelope(1, "session.shutdown", """{"codeChanges":{"linesAdded":0,"linesRemoved":0,"filesModified":[]}}""")));

        await Assert.That((metrics.LinesAdded, metrics.FilesModifiedCount, metrics.TotalNanoAiu)).IsEqualTo((0, 0, null));
    }

    [Test]
    public async Task A_shutdowns_files_keep_only_the_strings() {
        var metrics = Single<CopilotSessionMetrics>(E(Envelope(1, "session.shutdown", """{"codeChanges":{"linesAdded":1,"filesModified":["a.cs",42,true,{"x":1}]}}""")));

        await Assert.That(metrics.FilesModifiedCount).IsEqualTo(1);
        await Assert.That(metrics.FilesModified).IsEquivalentTo(new[] { "a.cs" });
    }

    [Test]
    public async Task A_checkpoint_reports_its_totals_on_its_envelope_id() {
        var e       = E(Envelope(1, "session.usage_checkpoint", """{"totalNanoAiu":3109455000,"totalPremiumRequests":1,"modelCacheState":[]}""")).Single();
        var metrics = (CopilotSessionMetrics)e.Payload;

        await Assert.That(e.EventId).IsEqualTo(Guid.Parse(Id(1)));
        await Assert.That((metrics.TotalNanoAiu, metrics.TotalPremiumRequests, metrics.LinesAdded)).IsEqualTo((3109455000L, 1d, null));
    }

    [Test]
    [Arguments("""{"totalNanoAiu":5,"usageAccountingWatermarks":{}}""", true)]
    [Arguments("""{"totalNanoAiu":5,"usageAccountingWatermarks":{"s":0}}""", true)]
    [Arguments("""{"totalNanoAiu":5}""", false)]
    public async Task Watermarks_being_present_mark_totals_as_cumulative(string data, bool cumulative) =>
        await Assert.That(((CopilotSessionMetrics)E(Envelope(1, "session.usage_checkpoint", data)).Single().Payload).CumulativeAcrossRuns == true).IsEqualTo(cumulative);

    [Test]
    public async Task Each_runs_totals_carry_the_start_or_resume_that_opened_it() {
        var ctx = Context();
        var events = All([
            """{"type":"session.start","data":{"context":{}},"id":"aaaaaaaa-0000-0000-0000-0000000000a1","timestamp":"2026-08-18T13:00:00Z"}""",
            """{"type":"session.shutdown","data":{"totalNanoAiu":4392015000},"id":"11111111-0000-0000-0000-000000000001","timestamp":"2026-08-18T13:05:00Z"}""",
            """{"type":"session.resume","data":{},"id":"bbbbbbbb-0000-0000-0000-0000000000b2","timestamp":"2026-08-18T13:10:00Z"}""",
            """{"type":"session.usage_checkpoint","data":{"totalNanoAiu":2247675000},"id":"22222222-0000-0000-0000-000000000002","timestamp":"2026-08-18T13:15:00Z"}""",
        ], ctx);

        await Assert.That(events.Select(e => e.Payload).OfType<CopilotSessionMetrics>().Select(m => m.RunMarker)).IsEquivalentTo(new Guid?[] {
            Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"),
        }, CollectionOrdering.Matching);
        await Assert.That(events.Count(e => e.EventType == CanonicalEventTypes.SessionStarted)).IsEqualTo(1);
    }

    static string Shutdown(int n, bool watermarks) =>
        Envelope(n, "session.shutdown", "{" + (watermarks ? """ "usageAccountingWatermarks":{"s":3}, """ : "")
          + """ "currentModel":"claude-haiku-4.5","modelMetrics":{"claude-haiku-4.5":{"requests":{"count":3,"cost":0.33},"usage":{"inputTokens":122985,"outputTokens":683,"cacheReadTokens":75939,"cacheWriteTokens":47023},"totalNanoAiu":6981065000}}}""");

    /// <summary>The run's records counted the tokens, so its shutdown adds billing alone, whether this context saw the
    /// records or only the watermarks saying they were counted.</summary>
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_shutdown_after_usage_records_adds_billing_alone(bool sawRecords, bool watermarks) {
        var ctx = Context();
        if (sawRecords) P(Record(1, "claude-haiku-4.5", 40340, 633, 0, 40330), ctx);

        var backfill = Single<CopilotUsageBackfill>(E(Shutdown(2, watermarks), ctx));

        await Assert.That(backfill).IsEqualTo(new CopilotUsageBackfill {
            Model = "claude-haiku-4.5", SessionModel = "claude-haiku-4.5", RequestCount = 3, RequestCost = 0.33, NanoAiu = 6981065000,
        });
    }

    [Test]
    public async Task A_resumed_run_counts_its_shutdown_again_without_records() {
        var ctx = Context();
        P(Record(1, "claude-haiku-4.5", 40340, 633, 0, 40330), ctx);
        P(Envelope(2, "session.resume", "{}"), ctx);

        await Assert.That(Single<CopilotUsageBackfill>(E(Shutdown(3, watermarks: false), ctx)).InputTokens).IsEqualTo(23L);
    }

    /// <summary>Older transcripts report output on each message and the rest on the shutdown; output is taken from the
    /// shutdown only when the run's messages were seen to omit it.</summary>
    [Test]
    [Arguments("""{"model":"claude-haiku-4.5","content":"ok","outputTokens":5}""", 0L)]
    [Arguments("""{"model":"claude-haiku-4.5","content":"ok"}""", 683L)]
    public async Task A_shutdown_counts_output_only_when_the_runs_messages_omitted_it(string message, long output) {
        var ctx = Context();
        P(Envelope(1, "assistant.message", message), ctx);

        var backfill = Single<CopilotUsageBackfill>(E(Shutdown(2, watermarks: false), ctx));

        await Assert.That((backfill.InputTokens, backfill.OutputTokens, backfill.CacheReadTokens, backfill.CacheWriteTokens)).IsEqualTo((23L, output, 75939L, 47023L));
    }

    [Test]
    public async Task A_shutdown_with_no_message_seen_counts_no_output() =>
        await Assert.That(Single<CopilotUsageBackfill>(E(Shutdown(1, watermarks: false))).OutputTokens).IsEqualTo(0L);

    // ── VS Code Local request credits ──────────────────────────────────────────────────────────────

    [Test]
    public async Task A_request_credits_line_reports_that_requests_credits_outside_any_run() {
        var ctx = Context();
        P(Envelope(1, "session.start", """{"sessionId":"s"}"""), ctx);

        var e = E(Envelope(2, "kcap.vscode_request_credits", """{"requestId":"r1","totalNanoAiu":465015150}"""), ctx).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.CopilotSessionMetrics);
        await Assert.That(e.EventId).IsEqualTo(Guid.Parse(Id(2)));
        await Assert.That(e.Payload).IsEqualTo(new CopilotSessionMetrics { TotalNanoAiu = 465015150, RequestId = "r1" });
    }

    [Test]
    [Arguments("""{"totalNanoAiu":5}""")]
    [Arguments("""{"requestId":"","totalNanoAiu":5}""")]
    [Arguments("""{"requestId":"r1","totalNanoAiu":"5"}""")]
    [Arguments("""{"requestId":"r1","totalNanoAiu":-5}""")]
    [Arguments("""{"requestId":7,"totalNanoAiu":5.5}""")]
    public async Task A_malformed_request_credits_line_emits_nothing(string data) =>
        await Assert.That(E(Envelope(1, "kcap.vscode_request_credits", data))).IsEmpty();

    // ── ids, times, noise and rejected lines ───────────────────────────────────────────────────────

    [Test]
    public async Task A_line_without_a_guid_id_is_identified_by_its_bytes() {
        const string line = """{"type":"user.message","data":{"content":"hi"},"id":"not-a-guid","timestamp":"2026-11-05T02:23:01Z"}""";

        await Assert.That(E(line).Single().EventId).IsEqualTo(TranscriptIds.CopilotRecord(null, line));
    }

    [Test]
    public async Task A_line_without_a_time_takes_the_earliest_instant() {
        var e = E("""{"type":"user.message","data":{"content":"hi"},"id":"00000000-0000-4000-8000-000000000001"}""").Single();

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(((UserMessageReceived)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(e.RecordTimestamp).IsNull();
    }

    [Test]
    public async Task A_time_that_does_not_parse_is_kept_as_written_and_the_event_takes_the_earliest_instant() {
        var e = E("""{"type":"user.message","data":{"content":"hi"},"id":"00000000-0000-4000-8000-000000000001","timestamp":"yesterday"}""").Single();

        await Assert.That(e.Timestamp).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(e.RecordTimestamp).IsEqualTo("yesterday");
    }

    [Test]
    [Arguments("""{"type":"hook.start","data":{"hookType":"sessionStart","input":{}},"id":"a1499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:49Z"}""")]
    [Arguments("""{"type":"hook.end","data":{"hookType":"sessionStart","success":true},"id":"a2499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:49Z"}""")]
    [Arguments("""{"type":"system.message","data":{"role":"system","content":"You are the GitHub Copilot CLI"},"id":"a3499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:50Z"}""")]
    [Arguments("""{"type":"system.notification","data":{"content":"Agent completed","kind":"agent_completed"},"id":"a4499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:24:14Z"}""")]
    [Arguments("""{"type":"assistant.turn_start","data":{"turnId":"0"},"id":"a5499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:51Z"}""")]
    [Arguments("""{"type":"assistant.turn_end","data":{"turnId":"0"},"id":"a6499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:59Z"}""")]
    [Arguments("""{"type":"tool.execution_start","data":{"toolCallId":"toolu_A","toolName":"report_intent","arguments":{}},"id":"a7499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:58Z"}""")]
    [Arguments("""{"type":"session.model_change","data":{"newModel":"claude-sonnet-4.6"},"id":"a8499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:23:50Z"}""")]
    [Arguments("""{"type":"session.resume","data":{"selectedModel":"claude-sonnet-4.6","eventCount":40},"id":"a9499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:24:30Z"}""")]
    [Arguments("""{"type":"subagent.started","data":{"toolCallId":"toolu_X","agentName":"explore"},"id":"b2499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:24:00Z","agentId":"toolu_X"}""")]
    [Arguments("""{"type":"some.future_event","data":{"anything":true},"id":"b3499ffe-8ac5-413f-add7-0998b8e1d47b","timestamp":"2026-06-10T20:24:00Z"}""")]
    [Arguments("""{"data":{"content":"no type"},"id":"b4499ffe-8ac5-413f-add7-0998b8e1d47b"}""")]
    [Arguments("""{"type":"user.message","data":"not an object","id":"b5499ffe-8ac5-413f-add7-0998b8e1d47b"}""")]
    public async Task Noise_and_unknown_lines_emit_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
        await Assert.That(result.Amendments).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[1,2]")]
    [Arguments("\"text\"")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) =>
        await Assert.That(P(line).Rejected).IsNotNull();

    [Test]
    public async Task Batch_boundaries_clear_nothing() {
        var ctx = Context();
        P(Envelope(1, "session.start", """{"startTime":"2026-11-05T02:23:01Z"}"""), ctx);
        P(Envelope(2, "session.usage_record", """{"usage":{"model":"m","inputTokens":1}}"""), ctx);
        P(Envelope(3, "assistant.message", """{"content":"ok"}"""), ctx);

        ctx.BeginBatch();

        await Assert.That((ctx.RunMarker, ctx.RunHasUsageRecords, ctx.RunMessagesReportOutput)).IsEqualTo((Guid.Parse(Id(1)), true, false));
    }

    [Test]
    [Arguments("view", AcpToolKind.Read)]
    [Arguments("read_file", AcpToolKind.Read)]
    [Arguments("apply_patch", AcpToolKind.Edit)]
    [Arguments("replace_string_in_file", AcpToolKind.Edit)]
    [Arguments("bash", AcpToolKind.Execute)]
    [Arguments("run_in_terminal", AcpToolKind.Execute)]
    [Arguments("grep", AcpToolKind.Search)]
    [Arguments("web_fetch", AcpToolKind.Fetch)]
    [Arguments("task", AcpToolKind.Other)]
    [Arguments("github-mcp-server-get_issue", AcpToolKind.Other)]
    public async Task Each_tool_call_is_classified(string tool, string kind) =>
        await Assert.That(CopilotToolKinds.Of(tool)).IsEqualTo(kind);
}
