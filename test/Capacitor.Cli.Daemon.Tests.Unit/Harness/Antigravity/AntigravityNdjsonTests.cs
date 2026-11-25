using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Pure unit tests for <see cref="AntigravityNdjson"/> and
/// <see cref="AntigravityStepAccumulator"/> — no process/runtime involved. Every non-obvious
/// fixture below is a line captured from a live <c>agy -p … --output-format stream-json</c> run, not
/// invented, so a shape this test locks in is a shape actually observed on the wire.
/// </summary>
public class AntigravityNdjsonTests {
    const string InitLine =
        """{"event":"init","conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","init":{"cwd":"/w","tools":["call_mcp_tool"],"permission_mode":"request-review"}}""";

    [Test]
    public async Task Init_yields_a_session_started_carrying_the_conversation_id() {
        var evt = AntigravityNdjson.TryParseLine(InitLine);
        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.Init);
        await Assert.That(evt.ConversationId).IsEqualTo("e80c33bf-c10f-4d2f-b626-b0043f488fc0");

        var envelopes = AntigravityNdjson.ToEnvelopes(evt, model: "gemini-3.5-flash");
        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.SessionStarted);
        await Assert.That(envelopes[0].RawSessionId).IsEqualTo("e80c33bf-c10f-4d2f-b626-b0043f488fc0");
        await Assert.That(envelopes[0].Cwd).IsEqualTo("/w");
        await Assert.That(envelopes[0].Model).IsEqualTo("gemini-3.5-flash");
    }

    [Test]
    public async Task A_terminal_result_never_produces_a_session_ended_envelope() {
        // The server's EndAgentSession owns session termination. A runtime that emits
        // session_ended would double-end the session.
        var evt = AntigravityNdjson.TryParseLine(
            """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"ok","num_turns":1}}""");

        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.Result);
        await Assert.That(evt.Status).IsEqualTo("SUCCESS");
        await Assert.That(AntigravityNdjson.ToEnvelopes(evt, null)
            .Any(e => e.Kind == AcpEventKind.SessionEnded)).IsFalse();
    }

    [Test]
    public async Task An_unknown_step_type_is_tolerated_not_rejected() {
        // Real agy output contains step_type:"unknown". Treating it as a protocol
        // violation would kill live reviewers.
        var evt = AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":1,"state":"DONE","step_type":"unknown"}}""");

        await Assert.That(evt).IsNotNull();
        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.StepUpdate);
    }

    [Test]
    public async Task A_malformed_or_blank_line_returns_null_rather_than_throwing() {
        await Assert.That(AntigravityNdjson.TryParseLine("")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("not json")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("""{"event":"nope"}""")!.Kind)
            .IsEqualTo(AntigravityEventKind.Unknown);
    }

    /// <summary>
    /// A line that is valid JSON but not an OBJECT reads as "nothing to read" (null), not as schema
    /// drift (<see cref="AntigravityEventKind.Unknown"/>). Every agy NDJSON line is an object, so a
    /// bare array/scalar is a torn or foreign line rather than a variant we don't understand yet —
    /// and the distinction is load-bearing: the runtime's read loop drops a null and keeps going,
    /// while an Unknown is a real event a future handler could act on. Untested before this test, so
    /// the top-level object guard could be deleted with the whole suite still green.
    /// </summary>
    [Test]
    public async Task A_valid_json_line_that_is_not_an_object_returns_null() {
        await Assert.That(AntigravityNdjson.TryParseLine("[1,2,3]")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("\"just a string\"")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("123")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("null")).IsNull();
    }

    [Test]
    public async Task Text_deltas_aggregate_per_step_and_flush_on_done() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"PROBE"}}""")!);
        var mid = acc.Flush(model: null);
        await Assert.That(mid.Count).IsEqualTo(0);   // nothing flushed while ACTIVE

        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"_OK"}}""")!);
        var done = acc.Flush(model: null);

        await Assert.That(done.Any(e => e is { Kind: AcpEventKind.AssistantText, Text: "PROBE_OK" })).IsTrue();
    }

    [Test]
    public async Task A_step_flushed_once_is_not_re_emitted_on_a_later_flush() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"ONCE"}}""")!);
        var first = acc.Flush(model: null);
        await Assert.That(first.Count(e => e.Kind == AcpEventKind.AssistantText)).IsEqualTo(1);

        var second = acc.Flush(model: null);
        await Assert.That(second.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Usage_on_a_done_agent_response_step_flushes_alongside_the_aggregated_text() {
        // Verbatim captured line: the DONE step_update carries both the tail of the text_delta
        // stream AND the step's usage block in the same event.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"PROBE_OK"}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":2.494169,"usage":{"input_tokens":16392,"output_tokens":46,"thinking_tokens":42,"cache_read_tokens":0,"total_tokens":16438}}}""")!);

        var envelopes = acc.Flush(model: "gemini-3.5-flash");

        var text = envelopes.Single(e => e.Kind == AcpEventKind.AssistantText);
        await Assert.That(text.Text).IsEqualTo("PROBE_OK\n");

        var usage = envelopes.Single(e => e.Kind == AcpEventKind.Usage);
        await Assert.That(usage.Model).IsEqualTo("gemini-3.5-flash");
        await Assert.That(usage.ContextUsedTokens).IsEqualTo(16392L);
    }

    [Test]
    public async Task A_done_checkpoint_step_with_no_text_flushes_only_usage() {
        // Verbatim captured line: a "checkpoint" step carries usage but no text_delta at all.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":3,"state":"DONE","step_type":"checkpoint","duration_seconds":0.787126,"usage":{"input_tokens":98,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":101}}}""")!);

        var envelopes = acc.Flush(model: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.Usage);
        await Assert.That(envelopes[0].ContextUsedTokens).IsEqualTo(98L);
    }

    [Test]
    public async Task A_done_step_with_neither_text_nor_usage_flushes_nothing() {
        // Verbatim captured line: a bare "user_input" DONE marker carries neither.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":0,"state":"DONE","step_type":"user_input"}}""")!);

        await Assert.That(acc.Flush(model: null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Independent_steps_flush_independently() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"unknown","duration_seconds":0.001982}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"still going"}}""")!);

        var envelopes = acc.Flush(model: null);

        // Step 1 (DONE, content-free) contributes nothing; step 2 (still ACTIVE) is untouched —
        // neither should produce an envelope, and step 2 must survive for a later flush.
        await Assert.That(envelopes.Count).IsEqualTo(0);

        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"!"}}""")!);
        var later = acc.Flush(model: null);
        await Assert.That(later.Any(e => e is { Kind: AcpEventKind.AssistantText, Text: "still going!" })).IsTrue();
    }

    // Captured from agy 1.5.16 (local paths scrubbed to /w): a "tool" step moves through ACTIVE (the
    // call), then DONE (an output, or none at all) or ERROR (an object error).

    const string ReadActiveLine =
        """{"event":"step_update","step_update":{"conversation_id":"f9647d5e-af22-4821-adff-cda42be4ddd4","step_index":3,"state":"ACTIVE","step_type":"tool","tool_name":"read_file","tool_info":{"name":"read_file","parameters":{"Path":"/w/notes.txt"}}}}""";

    const string ReadDoneLine =
        """{"event":"step_update","step_update":{"conversation_id":"f9647d5e-af22-4821-adff-cda42be4ddd4","step_index":3,"state":"DONE","step_type":"tool","tool_name":"read_file","duration_seconds":0.242563,"tool_info":{"name":"read_file","parameters":{"Path":"/w/notes.txt"},"output":"2 lines, 12 bytes"}}}""";

    const string EditDoneLine =
        """{"event":"step_update","step_update":{"conversation_id":"f9647d5e-af22-4821-adff-cda42be4ddd4","step_index":6,"state":"DONE","step_type":"tool","tool_name":"edit_file","duration_seconds":0.202708,"tool_info":{"name":"edit_file","parameters":{"Path":"/w/notes.txt"}}}}""";

    const string ToolErrorLine =
        """{"event":"step_update","step_update":{"step_index":3,"state":"ERROR","step_type":"tool","tool_name":"list_dir","duration_seconds":0.198566,"tool_info":{"name":"list_dir","parameters":{"DirectoryPath":"/w/.gemini"},"error":{"type":"TOOL_ERROR","message":"Permission denied for read_file(...). Matches hardcoded system protection boundary rule."}}}}""";

    static string RunCommandDoneLine(string output) =>
        """{"event":"step_update","step_update":{"conversation_id":"f9647d5e-af22-4821-adff-cda42be4ddd4","step_index":11,"state":"DONE","step_type":"tool","tool_name":"run_command","duration_seconds":0.597878,"tool_info":{"name":"run_command","parameters":{"CommandLine":"false"},"output":"""
      + "\"" + JsonEncodedText.Encode(output) + "\"}}}";

    [Test]
    public async Task An_active_tool_step_flushes_a_translated_tool_call_immediately_not_deferred_to_terminal() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(ReadActiveLine)!);

        var envelopes = acc.Flush(model: null);

        var call = envelopes.Single(e => e.Kind == AcpEventKind.ToolCall);
        await Assert.That(call.ToolCallId).IsEqualTo("3");
        await Assert.That(call.ToolName).IsEqualTo("Read");
        await Assert.That(call.ToolInputJson).IsEqualTo("""{"file_path":"/w/notes.txt"}""");

        // No result yet — the step is still ACTIVE — and flushing again must not repeat the call.
        await Assert.That(envelopes.Any(e => e.Kind == AcpEventKind.ToolResult)).IsFalse();
        await Assert.That(acc.Flush(model: null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_done_tool_step_flushes_a_successful_tool_result() {
        // Observed alone (no preceding ACTIVE reached this accumulator) — both the call and the
        // result must still flush together, call first.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(ReadDoneLine)!);

        var envelopes = acc.Flush(model: null);

        await Assert.That(envelopes.Select(e => e.Kind)).IsEquivalentTo(
            new[] { AcpEventKind.ToolCall, AcpEventKind.ToolResult }, CollectionOrdering.Matching);
        var result = envelopes[1];
        await Assert.That(result.ToolCallId).IsEqualTo("3");
        await Assert.That(result.ToolIsError).IsFalse();
        await Assert.That(result.ToolResult).IsEqualTo("2 lines, 12 bytes");
    }

    /// <summary>agy finishes an <c>edit_file</c> with no output at all; without a result the call reads as
    /// running for the rest of the session.</summary>
    [Test]
    public async Task A_done_tool_step_with_no_output_still_settles_its_call() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(EditDoneLine)!);

        var result = acc.Flush(model: null).Single(e => e.Kind == AcpEventKind.ToolResult);

        await Assert.That(result.ToolCallId).IsEqualTo("6");
        await Assert.That(result.ToolIsError).IsFalse();
        await Assert.That(result.ToolResult).IsEqualTo("");
    }

    [Test]
    public async Task An_error_tool_step_flushes_a_failed_tool_result_not_a_thrown_exception() {
        // ERROR is an ordinary terminal state for a tool step, not a protocol violation.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(ToolErrorLine)!);

        var envelopes = acc.Flush(model: null);

        var result = envelopes.Single(e => e.Kind == AcpEventKind.ToolResult);
        await Assert.That(result.ToolCallId).IsEqualTo("3");
        await Assert.That(result.ToolIsError).IsTrue();
        await Assert.That(result.ToolResult).IsEqualTo(
            "TOOL_ERROR: Permission denied for read_file(...). Matches hardcoded system protection boundary rule.");
    }

    /// <summary>A command's outcome is in its output's status line, under agy's timestamp header, which the
    /// result drops. The status line counts only where it leads the body.</summary>
    [Test]
    [Arguments("Created At: 2026-11-24T17:29:00-05:00\nCompleted At: 2026-11-24T17:29:01-05:00\nCommand exited with code 1.\n\nNo output from the command.",
        true, "Command exited with code 1.\n\nNo output from the command.")]
    [Arguments("Created At: 2026-11-24T17:35:01-05:00\nCompleted At: 2026-11-24T17:35:02-05:00\nCreated At: 2026-11-24T17:35:01-05:00\nCompleted At: 2026-11-24T17:35:01-05:00\nCommand failed to run: /w/missing: no such directory.\n\nNo output from the command.",
        true, "Command failed to run: /w/missing: no such directory.\n\nNo output from the command.")]
    [Arguments("Created At: 2026-11-24T17:35:57-05:00\nCompleted At: 2026-11-24T17:35:57-05:00\nCommand exited with code 0.\n\nCommand output:\nCommand exited with code 2.\r\n",
        false, "Command exited with code 0.\n\nCommand output:\nCommand exited with code 2.\r\n")]
    public async Task A_command_result_is_an_error_when_it_exited_non_zero_or_failed_to_run(string output, bool isError, string shown) {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(RunCommandDoneLine(output))!);

        var result = acc.Flush(model: null).Single(e => e.Kind == AcpEventKind.ToolResult);

        await Assert.That(result.ToolIsError).IsEqualTo(isError);
        await Assert.That(result.ToolResult).IsEqualTo(shown);
    }

    [Test]
    public async Task A_tool_steps_call_and_result_flush_across_separate_flush_calls_for_the_same_step() {
        // The realistic lifecycle: ACTIVE flushes the call on its own Flush call; the step survives
        // in the accumulator; a later terminal state on the SAME step index then flushes the result.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(ReadActiveLine)!);
        var afterActive = acc.Flush(model: null);
        await Assert.That(afterActive.Count(e => e.Kind == AcpEventKind.ToolCall)).IsEqualTo(1);

        acc.Add(AntigravityNdjson.TryParseLine(ReadDoneLine)!);
        var afterDone = acc.Flush(model: null);

        // The call must not repeat; only the result is new.
        await Assert.That(afterDone.Count).IsEqualTo(1);
        await Assert.That(afterDone[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
    }

    [Test]
    public async Task Checkpoint_and_unknown_step_types_remain_tolerated_alongside_tool_steps() {
        // Confirms the full observed step_type set (agent_response/checkpoint/tool/unknown/
        // user_input) coexists without cross-contamination — a tool step in the same batch must not
        // change how a checkpoint or unknown step is handled.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"unknown"}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":4,"state":"DONE","step_type":"checkpoint","usage":{"input_tokens":10,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11}}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(ReadDoneLine)!);

        var envelopes = acc.Flush(model: null);

        await Assert.That(envelopes.Count(e => e.Kind == AcpEventKind.Usage)).IsEqualTo(1);
        await Assert.That(envelopes.Count(e => e.Kind == AcpEventKind.ToolCall)).IsEqualTo(1);
        await Assert.That(envelopes.Count(e => e.Kind == AcpEventKind.ToolResult)).IsEqualTo(1);
    }
}
