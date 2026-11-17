using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Muse;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Muse;

/// <summary>The rules of kcap-server's <c>MuseTranscriptNormalizer</c>, on single records cut from Muse Code 1.4.7
/// logs, sometimes with one field altered to reach a branch the captures do not.</summary>
public class MuseTranscriptEventsTests {
    const string Session = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
    const string Run     = "ffd9590d-bebc-4c76-86a4-f1b40853d520";

    static readonly DateTimeOffset Received = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    const string Started =
        """{"schema_version":1,"id":"0a4d6b8c-1b8e-4c4f-9a43-0d64d1f3a0c1","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":23,"recorded_at":1791917078811234,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","event":{"kind":"started","prompt":"run pwd"},"source_run_record_id":"bc145258-86dc-49ad-ac81-57f9b4bccbd7","source_run_record_sequence":5}}""";

    const string Message =
        """{"schema_version":1,"id":"1b3e7d0f-6a75-4e93-9a44-6dd1ea53e0a3","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":57,"recorded_at":1791917081312345,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","event":{"kind":"assistant_message_committed","message_id":"3d6588ec-596f-4ddd-8868-500ab08a8c9a","response_id":"resp_6ace7c183d9b27a8fc1344c9","text":"Running pwd now.","provider_item_id":"rs_01a1368b4d4d737aa3aa4b379db1690e","phase":"commentary"},"source_run_record_id":"cc85815e-0e3b-4ac0-bcad-5006b4ef7838","source_run_record_sequence":29}}""";

    const string ReasoningSummary =
        """{"schema_version":1,"id":"3c01aef2-150d-4d44-ac84-cbeb72d13040","stream":{"kind":"session","id":"01a13694-2560-71d3-a276-032c42f78ab9"},"sequence":132,"recorded_at":1791917059129101,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"7fb24846-6520-4fc6-9c68-905cc415bf70","event":{"kind":"reasoning_summary_committed","message_id":"3bec07b1-4285-441b-a6c9-2e3cf7512d00","response_id":"resp_6ace7bfc178073a635d74585","text":"Enforcing the denial.","provider_item_id":"rs_6ace7bfc178073a635d74585"},"source_run_record_id":"0370bf5a-c5c4-4407-9357-88d198b2c5ba","source_run_record_sequence":71}}""";

    const string ModelCompleted =
        """{"schema_version":1,"id":"c486814d-d4b0-40e5-9005-bbe69c13cfed","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":60,"recorded_at":1791917081542602,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","event":{"kind":"model_completed","usage":{"input_tokens":27293,"output_tokens":30,"cached_tokens":26993,"cache_write_tokens":0,"cache_read_tokens":26993,"reasoning_tokens":19},"duration_ms":2480,"finish_reason":"tool_calls","model":"muse-spark-1.3"},"source_run_record_id":"5dce03c4-29e0-4b3f-9106-d5ce2e526e42","source_run_record_sequence":27}}""";

    const string ToolCalls =
        """{"schema_version":1,"id":"c2606efb-7cad-49c1-ad70-554a9d30f4b1","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":62,"recorded_at":1791917081565743,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","event":{"kind":"assistant_tool_calls_committed","message_id":"3b4fd986-58ad-4805-9fa7-9ae4ddebcbc4","response_id":"resp_6ace7c183d9b27a8fc1344c9","tool_calls":[{"id":"fc_01a13694c11874b79691ce18a5954076","call_id":"call_01a13694c11874b79691ce18a5954076","name":"bash","args":"{\"command\":\"pwd\",\"description\":\"Print working directory\"}"}]},"source_run_record_id":"573b7019-5b44-4c2c-8d59-287d12d39857","source_run_record_sequence":28}}""";

    const string Terminal =
        """{"schema_version":1,"id":"abe5f698-734b-4dac-ab40-bff65f7434dc","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":129,"recorded_at":1791917097171937,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","event":{"kind":"terminal","terminal":"completed","reason":null,"turn_duration_ms":18409,"time_to_first_token_ms":4783,"eot_gate_ms":13318},"source_run_record_id":"ccd936e6-09d4-4818-851c-3e9b4cfe7db0","source_run_record_sequence":62}}""";

    const string FailedTerminal =
        """{"schema_version":1,"id":"29aceecd-48e1-4c57-bea6-e162e1f54e24","stream":{"kind":"session","id":"6e036af4-674d-443c-8f22-bb26268fb019"},"sequence":3,"recorded_at":1791917400348332,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"6e036af4-674d-443c-8f22-bb26268fb019","event":{"kind":"terminal","terminal":"failed","reason":"invalid run configuration: provider does not support base instructions","turn_duration_ms":11},"source_run_record_id":"8476dd26-01f2-4b4b-9ccb-7b7462b7d7e8","source_run_record_sequence":3}}""";

    const string ModelConfigured =
        """{"schema_version":1,"id":"dc5597a8-174a-420d-b19e-712185f380a4","stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"sequence":14,"recorded_at":1791917078727891,"record_type":"event","durability":"durable","causation_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","payload_type":"run.model.configured","payload_schema_version":1,"payload":{"kind":"run_model","record":{"command_id":"ffd9590d-bebc-4c76-86a4-f1b40853d520","run_stream":{"kind":"run","id":"ffd9590d-bebc-4c76-86a4-f1b40853d520"},"provider_id":"meta","profile_id":"tbh","model_id":"muse-spark-1.3","display_label":"muse-spark-1.3","source":"startup"}}}""";

    const string Resumed =
        """{"schema_version":1,"id":"fdb2a64f-d25c-41a9-98bf-c32ad48d563b","stream":{"kind":"session","id":"01a135b7-0283-7ae3-aedb-c7dc0ef6c791"},"sequence":85,"recorded_at":1791902552457908,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"session.resumed","payload_schema_version":1,"payload":{"kind":"session_resumed","record":{"schema_version":1,"session_id":"01a135b7-0283-7ae3-aedb-c7dc0ef6c791","prior_turn_count":1,"resumed_from_sequence":79,"resumed_from_record_id":"5dac3ad1-835c-483e-8855-f68f68377bbf"}}}""";

    const string ApprovalRequested =
        """{"schema_version":1,"id":"7e2a1f30-3d55-4c8e-9a1b-0f6a2b7c9d10","stream":{"kind":"session","id":"01a1368b-38a8-7a43-880e-e994ade1ca52"},"sequence":70,"recorded_at":1791916462600000,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"approval","run_id":"9521c75c-0f0a-4258-8b21-c6f64b995c22","event":{"kind":"requested"}}}""";

    const string RetainedMarker =
        """{"retained_marker":"omitted_live_only","schema_version":1,"stream":{"kind":"session","id":"01a13694-b0ad-7ae2-8bf5-2af6357a0847"},"position":{"id":"1183e013-1d3b-4b86-b56c-e25fa03ce8b9","sequence":72},"omitted_record":{"record_type":"status","durability":"ephemeral","payload_type":"runtime.session","payload_schema_version":1,"payload_kind":"task","omission_class":"task_tool_delta_v1"}}""";

    static string ResultLine(string text, int index = 0) =>
        "{\"schema_version\":1,\"id\":\"ccc213d1-33f1-474d-9b73-87b463e34d9e\",\"stream\":{\"kind\":\"session\",\"id\":\"" + Session + "\"},"
      + "\"sequence\":78,\"recorded_at\":1791917081826392,\"record_type\":\"event\",\"durability\":\"durable\",\"causation_id\":null,"
      + "\"payload_type\":\"runtime.session\",\"payload_schema_version\":1,\"payload\":{\"kind\":\"run\",\"run_id\":\"" + Run + "\","
      + "\"event\":{\"kind\":\"tool_result_batch_committed\",\"batch_id\":\"3b4fd986-58ad-4805-9fa7-9ae4ddebcbc4\",\"results\":[{\"tool_call_index\":" + index
      + ",\"tool_call_id\":\"call_01a13694c11874b79691ce18a5954076\",\"text\":" + JsonSerializer.Serialize(text) + "}]}}}";

    static string Frame(params string[] children) =>
        "{\"retained_frame\":\"session_permission_transaction\",\"frame_schema_version\":1,\"outer_log_ordinal\":1,\"transaction_id\":\"01a13694-b0e7-7d41-9678-b3f7abace056\",\"children\":["
      + string.Join(",", children.Select((c, i) => "{\"child_index\":" + i + ",\"record_json\":" + JsonSerializer.Serialize(c) + "}"))
      + "],\"content_sha256\":\"sha256:0\"}";

    static ProjectionResult Project(string line, MuseContext? ctx = null) =>
        MuseTranscriptEvents.Instance.Project(line, 1, Received, ctx ?? new MuseContext());

    static IReadOnlyList<CanonicalEvent> Events(string line, MuseContext? ctx = null) => Project(line, ctx).Events;

    static List<CanonicalEvent> All(MuseContext ctx, params string[] lines) => [.. lines.SelectMany(l => Events(l, ctx))];

    static string Slug(CanonicalEvent e) => JsonFormatter.Default.Format(SchemaExtensions.Slug(e.Payload, MuseExtension.Slug)!);

    static (long Input, long Output, long Cached, long Reasoning, string Model)? Usage(CanonicalEvent e) =>
        e.Usage is { } u ? (u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.ReasoningTokens, u.Model) : null;

    // ── lines it cannot read, and records it skips ───────────────────────

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("""{"schema_version":1,"id":"x","stream":{"kind":"session","id":"s"},"record_type":"ev""")]
    [Arguments("[1,2]")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) {
        var result = Project(line);

        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    public async Task An_unknown_payload_type_yields_nothing() =>
        await Assert.That(Events(Started.Replace("\"payload_type\":\"runtime.session\"", "\"payload_type\":\"runtime.someday.new\""))).IsEmpty();

    [Test]
    public async Task A_record_without_its_stream_or_id_yields_nothing() {
        await Assert.That(Events(Started.Replace("\"stream\":{\"kind\":\"session\",\"id\":\"" + Session + "\"},", ""))).IsEmpty();
        await Assert.That(Events(Started.Replace("\"id\":\"0a4d6b8c-1b8e-4c4f-9a43-0d64d1f3a0c1\",", ""))).IsEmpty();
    }

    [Test]
    public async Task A_status_or_ephemeral_record_yields_nothing() {
        await Assert.That(Events(Started.Replace("\"record_type\":\"event\"", "\"record_type\":\"status\""))).IsEmpty();
        await Assert.That(Events(Started.Replace("\"durability\":\"durable\"", "\"durability\":\"ephemeral\""))).IsEmpty();
    }

    [Test]
    [Arguments(Resumed)]
    [Arguments(ApprovalRequested)]
    [Arguments(ModelConfigured)]
    [Arguments(RetainedMarker)]
    public async Task A_record_without_conversation_yields_nothing(string line) {
        var result = Project(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    public async Task An_encrypted_reasoning_record_yields_nothing() =>
        await Assert.That(Events(ReasoningSummary.Replace("reasoning_summary_committed", "reasoning_committed"))).IsEmpty();

    // ── retained frames ──────────────────────────────────────────────────

    [Test]
    public async Task A_frame_emits_each_child_in_order_keyed_by_the_child_record() {
        var events = Events(Frame(Started, Message));

        await Assert.That(events.Select(e => e.EventType))
            .IsEquivalentTo([CanonicalEventTypes.UserMessageReceived, CanonicalEventTypes.AssistantTextGenerated], CollectionOrdering.Matching);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.MuseRecord(Session, "0a4d6b8c-1b8e-4c4f-9a43-0d64d1f3a0c1"));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.MuseRecord(Session, "1b3e7d0f-6a75-4e93-9a44-6dd1ea53e0a3"));
    }

    [Test]
    public async Task A_frame_child_that_cannot_be_read_costs_only_itself() {
        var events = Events(Frame("{not json", Message));

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(((AssistantTextGenerated)events[0].Payload).Content).IsEqualTo("Running pwd now.");
    }

    // ── one record, one event ────────────────────────────────────────────

    [Test]
    public async Task A_started_run_is_the_user_prompt_at_its_recorded_time() {
        var e      = Events(Started).Single();
        var prompt = (UserMessageReceived)e.Payload;
        var at     = DateTimeOffset.FromUnixTimeMilliseconds(1791917078811).AddTicks(2340);

        await Assert.That(prompt.Content).IsEqualTo("run pwd");
        await Assert.That(prompt.Timestamp.ToDateTimeOffset()).IsEqualTo(at);
        await Assert.That(e.Timestamp).IsEqualTo(at);
        await Assert.That(e.RecordTimestamp).IsEqualTo("2026-10-13T18:44:38.8112340+00:00");
        await Assert.That(e.VendorMetadata!).IsEquivalentTo(new Dictionary<string, string> { [MuseMetadataKeys.RunId] = Run });
        await Assert.That(e.VendorMetadataValues!).IsEquivalentTo(new Dictionary<string, object> { [MuseMetadataKeys.Sequence] = 23L });
        await Assert.That(e.Usage).IsNull();
    }

    [Test]
    public async Task A_task_id_on_the_payload_or_its_event_is_carried() {
        var onPayload = Events(Started.Replace("\"kind\":\"run\",", "\"kind\":\"run\",\"task_id\":\"t-1\",")).Single();
        var onEvent   = Events(Started.Replace("\"kind\":\"started\",", "\"kind\":\"started\",\"task_id\":\"t-2\",")).Single();

        await Assert.That(onPayload.VendorMetadata![MuseMetadataKeys.TaskId]).IsEqualTo("t-1");
        await Assert.That(onEvent.VendorMetadata![MuseMetadataKeys.TaskId]).IsEqualTo("t-2");
    }

    /// <summary>A record with no <c>recorded_at</c> is stored with no payload time and no <c>$timestamp</c>.</summary>
    [Test]
    public async Task An_untimed_record_takes_the_receive_time_and_leaves_the_payload_untimed() {
        var e = Events(Started.Replace("\"recorded_at\":1791917078811234,", "")).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Timestamp).IsNull();
        await Assert.That(e.Timestamp).IsEqualTo(Received);
        await Assert.That(e.RecordTimestamp).IsNull();
    }

    [Test]
    [Arguments("-1")]
    [Arguments("253402300800000000")]
    public async Task A_recorded_time_out_of_range_is_untimed(string micros) {
        var e = Events(Started.Replace("1791917078811234", micros)).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Timestamp).IsNull();
        await Assert.That(e.RecordTimestamp).IsNull();
    }

    [Test]
    public async Task The_id_is_derived_from_the_stream_and_record_whatever_the_line_number() {
        var first  = MuseTranscriptEvents.Instance.Project(Started, 1, Received, new MuseContext()).Events.Single().EventId;
        var replay = MuseTranscriptEvents.Instance.Project(Started, 99, Received, new MuseContext()).Events.Single().EventId;

        await Assert.That(replay).IsEqualTo(first);
        await Assert.That(first).IsEqualTo(TranscriptIds.MuseRecord(Session, "0a4d6b8c-1b8e-4c4f-9a43-0d64d1f3a0c1"));
    }

    [Test]
    public async Task An_assistant_message_is_assistant_text() =>
        await Assert.That(((AssistantTextGenerated)Events(Message).Single().Payload).Content).IsEqualTo("Running pwd now.");

    [Test]
    public async Task A_reasoning_summary_is_thinking() =>
        await Assert.That(((AssistantThinkingGenerated)Events(ReasoningSummary).Single().Payload).Content).IsEqualTo("Enforcing the denial.");

    [Test]
    public async Task Tool_calls_carry_call_id_name_kind_parsed_args_and_run() {
        var e     = Events(ToolCalls).Single();
        var call  = ((AssistantToolCallsGenerated)e.Payload).ToolCalls.Single();

        await Assert.That(call.CallId).IsEqualTo("call_01a13694c11874b79691ce18a5954076");
        await Assert.That(call.ToolName).IsEqualTo("bash");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("pwd");
        await Assert.That(Slug(e)).IsEqualTo($$"""{ "run_id": "{{Run}}" }""");
    }

    [Test]
    public async Task Tool_calls_of_a_record_with_no_run_carry_a_null_run_id() {
        var e = Events(ToolCalls.Replace("\"run_id\":\"" + Run + "\",", "")).Single();

        await Assert.That(Slug(e)).IsEqualTo("""{ "run_id": null }""");
        await Assert.That(e.VendorMetadata).IsNull();
    }

    [Test]
    public async Task Tool_call_args_that_are_not_an_object_are_kept_whole() {
        var call = ((AssistantToolCallsGenerated)Events(ToolCalls.Replace("""{\"command\":\"pwd\",\"description\":\"Print working directory\"}""", "pwd")).Single().Payload).ToolCalls.Single();

        await Assert.That(call.Arguments.Fields["arguments"].StringValue).IsEqualTo("pwd");
    }

    [Test]
    public async Task A_call_without_an_id_or_name_is_skipped_and_a_record_with_none_left_yields_nothing() =>
        await Assert.That(Events(ToolCalls.Replace("\"name\":\"bash\",", ""))).IsEmpty();

    [Test]
    [Arguments("read_file", AcpToolKind.Read)]
    [Arguments("write_file", AcpToolKind.Edit)]
    [Arguments("edit_file", AcpToolKind.Edit)]
    [Arguments("bash", AcpToolKind.Execute)]
    [Arguments("search", AcpToolKind.Search)]
    [Arguments("web_fetch", AcpToolKind.Fetch)]
    [Arguments("web_search", AcpToolKind.Fetch)]
    [Arguments("subagent_spawn", AcpToolKind.Other)]
    [Arguments("request_user_input", AcpToolKind.Other)]
    [Arguments("mcp__kcap__search_sessions", AcpToolKind.Other)]
    public async Task A_tool_name_maps_to_its_kind(string toolName, string kind) =>
        await Assert.That(MuseToolKinds.Of(toolName)).IsEqualTo(kind);

    [Test]
    public async Task A_bash_result_reads_success_from_its_exit_code() {
        var ok   = Events(ResultLine("""{"chunk_id":"exec-1-1","command":"pwd","exit_code":0,"terminal_status":"completed","output":"/home/dev/project\n"}""")).Single();
        var fail = Events(ResultLine("""{"chunk_id":"exec-1-1","command":"false","exit_code":1,"terminal_status":"completed","output":""}""")).Single();

        await Assert.That(((ToolResultReceived)ok.Payload).CallId).IsEqualTo("call_01a13694c11874b79691ce18a5954076");
        await Assert.That(Slug(ok)).IsEqualTo("""{ "success": true, "exit_code": 0, "reason": null }""");
        await Assert.That(Slug(fail)).IsEqualTo("""{ "success": false, "exit_code": 1, "reason": null }""");
    }

    [Test]
    [Arguments("tool failed: tool policy denied filesystem write")]
    [Arguments("tool denied: approval aborted\nThe user rejected this action.")]
    [Arguments("tool approval cancelled")]
    public async Task Refused_or_failed_tool_text_is_a_failure_with_its_reason(string text) {
        var e = Events(ResultLine(text)).Single();

        await Assert.That(((ToolResultReceived)e.Payload).Result).IsEqualTo(text);
        await Assert.That(SchemaExtensions.Slug(e.Payload, MuseExtension.Slug)).IsEqualTo(Block(false, null, text));
    }

    /// <summary>The server reads a block with no <c>success</c> as failed, so a success is written out.</summary>
    [Test]
    [Arguments("Read text file `app.py`.\n1|x = 2")]
    [Arguments("""{"answered":true}""")]
    [Arguments("""{"exit_code":"0"}""")]
    [Arguments("{not json")]
    public async Task Any_other_result_text_is_kept_verbatim_and_succeeds(string text) {
        var e = Events(ResultLine(text)).Single();

        await Assert.That(((ToolResultReceived)e.Payload).Result).IsEqualTo(text);
        await Assert.That(SchemaExtensions.Slug(e.Payload, MuseExtension.Slug)).IsEqualTo(Block(true, null, null));
    }

    [Test]
    public async Task Each_result_of_a_batch_is_its_own_event_on_a_stable_distinct_id_a_tick_apart() {
        var two = ResultLine("first").Replace(
            "}]}}}",
            "},{\"tool_call_index\":1,\"tool_call_id\":\"call_second\",\"text\":\"second\"}]}}}");

        var events = Events(two);
        var replay = Events(two);

        await Assert.That(events.Select(e => ((ToolResultReceived)e.Payload).CallId))
            .IsEquivalentTo(["call_01a13694c11874b79691ce18a5954076", "call_second"], CollectionOrdering.Matching);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.MuseRecord(Session, "ccc213d1-33f1-474d-9b73-87b463e34d9e:0"));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.MuseRecord(Session, "ccc213d1-33f1-474d-9b73-87b463e34d9e:1"));
        await Assert.That(replay.Select(e => e.EventId)).IsEquivalentTo(events.Select(e => e.EventId), CollectionOrdering.Matching);
        await Assert.That(((ToolResultReceived)events[1].Payload).Timestamp.ToDateTimeOffset())
            .IsEqualTo(((ToolResultReceived)events[0].Payload).Timestamp.ToDateTimeOffset().AddTicks(1));
        await Assert.That(events[1].RecordTimestamp).IsEqualTo(events[0].RecordTimestamp);
    }

    /// <summary>A result without a call id emits nothing but still takes its position, which keys a result that
    /// names no index of its own.</summary>
    [Test]
    public async Task A_result_without_a_call_id_is_skipped_but_counts_toward_the_next_ones_position() {
        var line = ResultLine("first").Replace(
            "{\"tool_call_index\":0,\"tool_call_id\":\"call_01a13694c11874b79691ce18a5954076\",\"text\":\"first\"}",
            "{\"text\":\"orphan\"},{\"tool_call_id\":\"call_late\",\"text\":\"late\"}");

        var e = Events(line).Single();

        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.MuseToolResult(Session, "ccc213d1-33f1-474d-9b73-87b463e34d9e", 1));
        await Assert.That(((ToolResultReceived)e.Payload).Timestamp.ToDateTimeOffset())
            .IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1791917081826).AddTicks(3920));
    }

    // ── usage ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Reported_usage_lands_on_the_next_assistant_event_of_its_run() {
        var events = All(new MuseContext(), ModelCompleted, ToolCalls, Message);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(Usage(events[0])).IsEqualTo((27293 - 26993, 30, 26993, 19, "muse-spark-1.3"));
        await Assert.That(events[0].Usage!.TotalTokens).IsEqualTo(300 + 30 + 26993);
        await Assert.That(events[0].Usage!.HasReasoningTokens).IsTrue();
        await Assert.That(events[0].CacheCreationTokens).IsNull();
        await Assert.That(events[0].UsageIsEcho).IsFalse();
        await Assert.That(Usage(events[1])).IsNull();
    }

    [Test]
    public async Task Usage_is_not_taken_by_another_run() {
        var ctx    = new MuseContext();
        var events = All(ctx, ModelCompleted, ToolCalls.Replace(Run, "11111111-2222-3333-4444-555555555555"));

        await Assert.That(Usage(events[0])).IsNull();
        await Assert.That(ctx.PendingUsage.ContainsKey(Run)).IsTrue();
    }

    [Test]
    public async Task Usage_never_lands_on_thinking() {
        var events = All(new MuseContext(), ModelCompleted, ReasoningSummary.Replace("7fb24846-6520-4fc6-9c68-905cc415bf70", Run), ToolCalls);

        await Assert.That(Usage(events[0])).IsNull();
        await Assert.That(Usage(events[1])).IsNotNull();
    }

    [Test]
    public async Task A_report_from_a_record_with_no_run_is_dropped() {
        var ctx = new MuseContext();

        await Assert.That(Events(ModelCompleted.Replace("\"run_id\":\"" + Run + "\",", ""), ctx)).IsEmpty();
        await Assert.That(ctx.PendingUsage).IsEmpty();
    }

    /// <summary>The last response of a run reports its usage after its message, with no assistant event left in the
    /// run to carry it, so the run's end carries it on a backfill, never as assistant text.</summary>
    [Test]
    public async Task Usage_left_at_the_end_of_a_run_rides_on_a_usage_backfill() {
        var ctx    = new MuseContext();
        var events = All(ctx, Message, ModelCompleted, Terminal);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[1].EventType).IsEqualTo(CanonicalEventTypes.MuseUsageBackfilled);
        await Assert.That(events[1].Payload).IsTypeOf<MuseUsageBackfill>();
        await Assert.That(Usage(events[1])).IsEqualTo((27293 - 26993, 30, 26993, 19, "muse-spark-1.3"));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.MuseRecord(Session, "abe5f698-734b-4dac-ab40-bff65f7434dc"));
        await Assert.That(events[1].RecordTimestamp).IsEqualTo("2026-10-13T18:44:57.1719370+00:00");
        await Assert.That(ctx.PendingUsage).IsEmpty();
    }

    [Test]
    public async Task A_completed_run_with_nothing_pending_yields_nothing() =>
        await Assert.That(Events(Terminal)).IsEmpty();

    [Test]
    public async Task Two_reports_before_an_assistant_event_add_up() {
        var events = All(new MuseContext(), ModelCompleted, ModelCompleted.Replace("c486814d", "d486814d"), ToolCalls);

        await Assert.That(Usage(events[0])).IsEqualTo((600, 60, 2 * 26993, 38, "muse-spark-1.3"));
    }

    /// <summary>A reconnecting watcher rewinds to its last stored line, so a report no event has consumed yet arrives again.</summary>
    [Test]
    public async Task A_re_delivered_report_is_counted_once() {
        var events = All(new MuseContext(), ModelCompleted, ModelCompleted, ToolCalls);

        await Assert.That(Usage(events[0])!.Value.Output).IsEqualTo(30);
    }

    [Test]
    public async Task Cache_read_tokens_stand_in_for_missing_cached_tokens() {
        var events = All(new MuseContext(), ModelCompleted.Replace("\"cached_tokens\":26993,", ""), ToolCalls);

        await Assert.That(Usage(events[0])!.Value.Cached).IsEqualTo(26993);
        await Assert.That(Usage(events[0])!.Value.Input).IsEqualTo(300);
    }

    [Test]
    public async Task Input_net_of_cached_tokens_never_goes_below_zero() {
        var events = All(new MuseContext(), ModelCompleted.Replace("\"input_tokens\":27293", "\"input_tokens\":5"), ToolCalls);

        await Assert.That(Usage(events[0])!.Value.Input).IsEqualTo(0);
    }

    [Test]
    public async Task The_configured_model_names_usage_that_reports_none() {
        var events = All(new MuseContext(), ModelConfigured.Replace("muse-spark-1.3", "muse-spark-2"), ModelCompleted.Replace(",\"model\":\"muse-spark-1.3\"", ""), ToolCalls);

        await Assert.That(Usage(events[0])!.Value.Model).IsEqualTo("muse-spark-2");
    }

    [Test]
    public async Task A_reconfigured_model_also_names_later_usage() {
        var reconfigured = ModelConfigured.Replace("run.model.configured", "runtime.model_reconfigure.completed").Replace("muse-spark-1.3", "muse-spark-3");
        var ctx          = new MuseContext();

        await Assert.That(Events(reconfigured, ctx)).IsEmpty();
        await Assert.That(ctx.CurrentModel).IsEqualTo("muse-spark-3");
    }

    [Test]
    public async Task Usage_naming_no_model_with_none_configured_has_no_model() {
        var events = All(new MuseContext(), ModelCompleted.Replace(",\"model\":\"muse-spark-1.3\"", ""), ToolCalls);

        await Assert.That(events[0].Usage!.HasModel).IsFalse();
    }

    /// <summary>The pending slot spans batches: Muse reports a response's usage before its tool calls, and the two
    /// can arrive in different watcher batches.</summary>
    [Test]
    public async Task Pending_usage_survives_a_batch_boundary() {
        var ctx = new MuseContext();
        Events(ModelCompleted, ctx);
        ctx.BeginBatch();

        await Assert.That(Usage(Events(ToolCalls, ctx).Single())).IsNotNull();
    }

    /// <summary>A caller that starts projecting partway through a run hands the context what an earlier reader held.</summary>
    [Test]
    public async Task A_seeded_context_carries_its_pending_usage_onto_the_run() {
        var ctx = new MuseContext { CurrentModel = "muse-spark-2" };
        ctx.PendingUsage[Run] = new MusePendingUsage(10, 2, 3, 1, null, new HashSet<string>(StringComparer.Ordinal) { "earlier" });

        await Assert.That(Usage(Events(ToolCalls, ctx).Single())).IsEqualTo((10, 2, 3, 1, ""));
    }

    // ── failed run ────────────────────────────────────────────────────────

    [Test]
    public async Task A_failed_run_surfaces_its_reason_as_a_marked_assistant_turn() {
        var e = Events(FailedTerminal).Single();

        await Assert.That(((AssistantTextGenerated)e.Payload).Content)
            .IsEqualTo("Turn failed: invalid run configuration: provider does not support base instructions");
        await Assert.That(e.VendorMetadataValues![MuseMetadataKeys.TurnFailed] is true).IsTrue();
        await Assert.That(e.VendorMetadataValues![MuseMetadataKeys.Sequence]).IsEqualTo(3L);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.MuseRecord("6e036af4-674d-443c-8f22-bb26268fb019", "29aceecd-48e1-4c57-bea6-e162e1f54e24"));
    }

    [Test]
    public async Task A_failed_run_without_a_reason_still_says_it_failed() =>
        await Assert.That(((AssistantTextGenerated)Events(FailedTerminal.Replace("\"reason\":\"invalid run configuration: provider does not support base instructions\",", "")).Single().Payload).Content)
            .IsEqualTo("Turn failed");

    [Test]
    public async Task A_failed_run_takes_the_runs_pending_usage() {
        var report = ModelCompleted.Replace(Session, "6e036af4-674d-443c-8f22-bb26268fb019").Replace(Run, "6e036af4-674d-443c-8f22-bb26268fb019");
        var events = All(new MuseContext(), report, FailedTerminal);

        await Assert.That(events.Single().EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(Usage(events.Single())).IsNotNull();
    }

    [Test]
    public async Task The_registry_resolves_muse_to_this_projection() =>
        await Assert.That(TranscriptProjection.For("muse")).IsSameReferenceAs(MuseTranscriptEvents.Instance);

    static Struct Block(bool success, int? exitCode, string? reason) {
        var block = new Struct();
        block.Fields["success"]   = Value.ForBool(success);
        block.Fields["exit_code"] = exitCode is { } code ? Value.ForNumber(code) : Value.ForNull();
        block.Fields["reason"]    = reason is null ? Value.ForNull() : Value.ForString(reason);
        return block;
    }
}
