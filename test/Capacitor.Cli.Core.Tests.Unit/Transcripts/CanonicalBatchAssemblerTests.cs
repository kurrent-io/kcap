using System.Text.Json;
using Capacitor.Cli.Core.Transcripts;
using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Codex;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class CanonicalBatchAssemblerTests {
    const string Session = "5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a";

    static readonly DateTimeOffset Received = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    static CanonicalBatchAssembler Claude() => new(ClaudeTranscriptEvents.Instance, ClaudeLineSignals.Instance, Session, null, "claude");

    static CanonicalBatchAssembler Codex() => new(CodexRolloutEvents.Instance, NoSignals.Instance, Session, null, "codex");

    static TranscriptBatch Raw(int first, params string[] lines) =>
        new() { SessionId = Session, Lines = lines, LineNumbers = [.. Enumerable.Range(first, lines.Length)] };

    static string Id(int n) => $"10000000-0000-4000-8000-{n:D12}";

    static string User(int n, string text, int? parent = null) =>
        $$$"""{"type":"user","uuid":"{{{Id(n)}}}","parentUuid":{{{Parent(parent)}}},"timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"role":"user","content":"{{{text}}}"}}""";

    static string Assistant(int n, string text, int? parent, int usage) =>
        $$$$"""{"type":"assistant","uuid":"{{{{Id(n)}}}}","parentUuid":{{{{Parent(parent)}}}},"timestamp":"2026-11-16T08:00:{{{{n:D2}}}}Z","message":{"model":"claude-x","content":[{"type":"text","text":"{{{{text}}}}"}],"usage":{"input_tokens":{{{{usage}}}},"output_tokens":5,"cache_creation_input_tokens":7}}}""";

    static string ToolResult(int n, int parent, string callId) =>
        $$$"""{"type":"user","uuid":"{{{Id(n)}}}","parentUuid":"{{{Id(parent)}}}","timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"content":[{"type":"tool_result","tool_use_id":"{{{callId}}}","content":"done"}]}}""";

    static string ImageBlock(string base64) =>
        $$$"""{"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{base64}}}"}}""";

    static string Image(int n, params string[] base64) =>
        $$$"""{"type":"user","uuid":"{{{Id(n)}}}","timestamp":"2026-11-16T08:00:00Z","message":{"content":[{"type":"text","text":"look"},{{{string.Join(",", base64.Select(ImageBlock))}}}]}}""";

    static string TurnEnd(int n) => $$"""{"type":"system","subtype":"turn_duration","uuid":"{{Id(n)}}","timestamp":"2026-11-16T08:00:{{n:D2}}Z"}""";

    static string Parent(int? parent) => parent is { } p ? $"\"{Id(p)}\"" : "null";

    const string CaptureLoss = """{"type":"kcap_capture_loss","version":1,"reason":"input_limit","input_utf16_length":5000000,"input_utf8_bytes":5000000}""";

    static string Serialize(CanonicalBatchEvent e) => JsonSerializer.Serialize(e, CapacitorJsonContext.Default.CanonicalBatchEvent);

    [Test]
    public async Task Two_lines_become_their_events_in_line_order() {
        var user      = User(1, "hi");
        var assistant = Assistant(2, "hello", parent: 1, usage: 10);

        var batch = Claude().Assemble(Raw(0, user, assistant), Received);

        var context  = ClaudeTranscriptEvents.Instance.CreateContext(Session, null);
        var expected = ClaudeTranscriptEvents.Instance.Project(user, 0, Received, context).Events
            .Concat(ClaudeTranscriptEvents.Instance.Project(assistant, 1, Received, context).Events)
            .Select(e => e.EventId);

        await Assert.That(batch.Events.Select(e => e.Type))
            .IsEquivalentTo([nameof(UserMessageReceived), nameof(AssistantTextGenerated)], CollectionOrdering.Matching);
        await Assert.That(batch.Events.Select(e => e.Line)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(batch.Events.Select(e => e.EventId)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(batch.Lines.Select(l => l.Uuid!)).IsEquivalentTo([Id(1), Id(2)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_line_without_events_is_still_covered() {
        var batch = Claude().Assemble(Raw(4, """{"type":"progress","uuid":"p"}"""), Received);

        await Assert.That(batch.Lines.Length).IsEqualTo(1);
        await Assert.That(batch.Lines[0]).IsEqualTo(new CanonicalBatchLine(4, "p", null, "progress", null));
        await Assert.That(batch.Events).IsEmpty();
    }

    [Test]
    public async Task A_truncated_line_between_good_lines_is_covered_and_produces_nothing() {
        var batch = Claude().Assemble(Raw(0, User(1, "hi"), $$"""{"type":"user","uuid":"{{Id(2)}}","mess""", Assistant(3, "hello", 1, 10)), Received);

        await Assert.That(batch.Lines.Select(l => l.Number)).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
        await Assert.That(batch.Lines[1]).IsEqualTo(new CanonicalBatchLine(1, null, null, null, null));
        await Assert.That(batch.Events.Select(e => e.Line)).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        await Assert.That(batch.CaptureLoss).IsNull();
    }

    /// The marker kcap writes, one with its type further in, and the two forms older CLIs wrote, as the server's parser
    /// takes each. Projected, a marker would clear the usage memory and line 2's repeated usage would count again.
    [Test]
    [Arguments(CaptureLoss)]
    [Arguments("""{"version":1,"type":"kcap_capture_loss","reason":"regex_timeout"}""")]
    [Arguments("""{"type":"redacted_oversize_line","reason":"line exceeded SecretRedactor size limit"}""")]
    [Arguments("""{"type":"redacted_unparsable_line","reason":"redacted line was no longer valid JSON"}""")]
    public async Task A_capture_loss_line_travels_verbatim(string marker) {
        var batch = Claude().Assemble(Raw(0, Assistant(1, "a", null, 10), marker, Assistant(3, "b", 1, 10)), Received);

        await Assert.That(batch.CaptureLoss!).IsEquivalentTo([new CanonicalBatchCaptureLoss(1, marker)], CollectionOrdering.Matching);
        await Assert.That(batch.Lines.Select(l => l.Number)).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
        await Assert.That(batch.Events.Select(e => e.Line)).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        await Assert.That(batch.Events[1].Usage).IsNull();
    }

    [Test]
    public async Task A_line_that_only_mentions_a_capture_loss_type_is_projected() {
        var batch = Claude().Assemble(Raw(0, User(1, "is kcap_capture_loss a redacted_oversize_line?")), Received);

        await Assert.That(batch.CaptureLoss).IsNull();
        await Assert.That(batch.Events.Length).IsEqualTo(1);
    }

    [Test]
    public async Task An_image_line_carries_its_bytes_message_id_and_block_index() {
        byte[] png = [0x89, 0x50, 0x4E, 0x47], gif = [0x47, 0x49, 0x46];

        var batch = Claude().Assemble(Raw(0, Image(1, Convert.ToBase64String(png), Convert.ToBase64String(gif))), Received);

        var message     = batch.Events.Single();
        var attachments = message.Attachments!;
        await Assert.That(attachments.Select(a => a.MessageId)).IsEquivalentTo([message.EventId, message.EventId], CollectionOrdering.Matching);
        await Assert.That(attachments.Select(a => a.BlockIndex)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(attachments[1].Data).IsEquivalentTo(gif, CollectionOrdering.Matching);
        await Assert.That(attachments[1].ContentType).IsEqualTo("image/png");
        await Assert.That(attachments[1].Id).IsEqualTo(TranscriptIds.ClaudeAttachment(Session, message.EventId, 1));
    }

    [Test]
    public async Task One_line_of_five_megabytes_assembles() {
        var bytes = new byte[5 * 1024 * 1024 / 4 * 3];
        new Random(2638).NextBytes(bytes);

        var batch = Claude().Assemble(Raw(0, Image(1, Convert.ToBase64String(bytes))), Received);

        await Assert.That(batch.Events.Length).IsEqualTo(1);
        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.CanonicalTranscriptBatch);
        var sent = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CanonicalTranscriptBatch)!;
        await Assert.That(sent.Events.Single().Attachments!.Single().Data).IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_title_and_a_turn_end_are_reported_with_their_lines() {
        const string title = $$"""{"type":"custom-title","customTitle":"Renamed","sessionId":"{{Session}}"}""";

        var batch = Claude().Assemble(Raw(10, User(1, "hi"), title, Assistant(3, "hello", 1, 10), TurnEnd(4), User(5, "next", 4)), Received);

        await Assert.That(batch.Titles!).IsEquivalentTo([new CanonicalBatchTitle("rename", "Renamed", null, false, 11)], CollectionOrdering.Matching);
        await Assert.That(batch.TurnEndLine).IsEqualTo(13);
    }

    /// Line 5 repeats line 4's usage, so it is an echo only to a projection that has seen line 4.
    [Test]
    public async Task Replay_then_assemble_equals_one_continuous_assemble() {
        string[] lines = [
            User(0, "start"),
            Assistant(1, "a", 0, 10),
            Assistant(2, "b", 1, 10),
            ToolResult(3, 2, "toolu_1"),
            Assistant(4, "c", 3, 20),
            Assistant(5, "d", 4, 20),
            TurnEnd(6),
            User(7, "next", 5),
            User(8, "next again", 5),
            Assistant(9, "e", 8, 30),
        ];

        var continuous = Claude().Assemble(Raw(0, lines), Received);

        var resumed = Claude();
        for (var i = 0; i < 5; i++) resumed.Replay(lines[i], i, Received);
        var tail = resumed.Assemble(Raw(5, lines[5..]), Received);

        await Assert.That(continuous.Events.Single(e => e.Line == 4).Usage).IsNotNull();
        await Assert.That(continuous.Events.Single(e => e.Line == 5).Usage).IsNull();
        await Assert.That(string.Join('\n', tail.Events.Select(Serialize)))
            .IsEqualTo(string.Join('\n', continuous.Events.Where(e => e.Line >= 5).Select(Serialize)));
    }

    static string CodexItem(string payload) => $$"""{"timestamp":"2026-11-16T08:00:00Z","type":"response_item","payload":{{payload}}}""";

    static string CodexEvent(string payload) => $$"""{"timestamp":"2026-11-16T08:00:01Z","type":"event_msg","payload":{{payload}}}""";

    static readonly string CodexThinking = CodexItem("""{"type":"reasoning","summary":[],"encrypted_content":"blob"}""");

    static readonly string CodexText = CodexItem("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"a"}]}""");

    static readonly string CodexTokens =
        CodexEvent("""{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":7,"reasoning_output_tokens":3},"model_context_window":258400}}""");

    [Test]
    public async Task Acknowledged_events_make_a_later_usage_a_backfill_not_a_stamp() {
        var stamped = Codex().Assemble(Raw(0, CodexText, CodexTokens), Received);

        await Assert.That(stamped.Events.Select(e => e.Type)).IsEquivalentTo([nameof(AssistantTextGenerated)], CollectionOrdering.Matching);
        await Assert.That(stamped.Events[0].Usage!.Value.GetProperty("input_tokens").GetString()).IsEqualTo("60");
        await Assert.That(stamped.Events[0].UsageIsEcho).IsFalse();

        var assembler = Codex();
        var text      = assembler.Assemble(Raw(0, CodexText), Received).Events.Single();
        assembler.Acknowledged();
        var backfill = assembler.Assemble(Raw(1, CodexTokens), Received).Events.Single();

        await Assert.That(backfill.Type).IsEqualTo(CanonicalEventTypes.CodexUsageBackfilled);
        await Assert.That(backfill.Line).IsEqualTo(1);
        await Assert.That(backfill.EventId).IsEqualTo(TranscriptIds.Sibling(text.EventId, "usage-backfill"));
        await Assert.That(backfill.Usage).IsNull();
        await Assert.That(backfill.Payload.GetProperty("input_tokens").GetInt64()).IsEqualTo(60L);
        await Assert.That(backfill.Payload.GetProperty("cache_read_tokens").GetInt64()).IsEqualTo(40L);
        await Assert.That(backfill.Payload.GetProperty("model_context_window").GetInt64()).IsEqualTo(258400L);
        var target = backfill.Payload.GetProperty("targets").EnumerateArray().Single();
        await Assert.That(target.GetProperty("event_id").GetString()).IsEqualTo(text.EventId.ToString("N"));
        await Assert.That(target.GetProperty("is_echo").GetBoolean()).IsFalse();
    }

    /// The thinking member echoes the text member; once any target was acknowledged, every stamp in the batch is an echo.
    [Test]
    public async Task A_usage_split_across_an_acknowledgement_stamps_echoes_and_backfills_the_acknowledged() {
        var assembler = Codex();
        var thinking  = assembler.Assemble(Raw(0, CodexThinking), Received).Events.Single();
        assembler.Acknowledged();

        var batch = assembler.Assemble(Raw(1, CodexText, CodexTokens), Received);

        await Assert.That(batch.Events.Select(e => e.Type))
            .IsEquivalentTo([nameof(AssistantTextGenerated), CanonicalEventTypes.CodexUsageBackfilled], CollectionOrdering.Matching);
        await Assert.That(batch.Events[0].Usage).IsNotNull();
        await Assert.That(batch.Events[0].UsageIsEcho).IsTrue();
        var target = batch.Events[1].Payload.GetProperty("targets").EnumerateArray().Single();
        await Assert.That(target.GetProperty("event_id").GetString()).IsEqualTo(thinking.EventId.ToString("N"));
    }

    static readonly string CodexExecEnd =
        CodexEvent("""{"type":"exec_command_end","call_id":"c1","exit_code":2,"duration":{"secs":1,"nanos":500000000},"stdout":"out","stderr":"err"}""");

    static readonly string CodexOutput = CodexItem("""{"type":"function_call_output","call_id":"c1","output":"ok"}""");

    static JsonElement? Exec(CanonicalBatchEvent e) =>
        e.Payload.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("codex", out var codex)
     && codex.TryGetProperty("exec", out var exec) ? exec : null;

    /// A batch holding only an exec end stores nothing, so the end still waits for its output in the next batch, whether
    /// that first batch was sent or replayed.
    [Test]
    public async Task An_exec_end_waits_for_its_output_past_a_batch_that_stored_nothing() {
        var sent = Codex();
        await Assert.That(sent.Assemble(Raw(0, CodexExecEnd), Received).Events).IsEmpty();
        sent.Acknowledged();

        var replayed = Codex();
        replayed.Replay(CodexExecEnd, 0, Received);

        foreach (var assembler in new[] { sent, replayed }) {
            var exec = Exec(assembler.Assemble(Raw(1, CodexOutput), Received).Events.Single())!.Value;
            await Assert.That(exec.GetProperty("exit_code").GetDouble()).IsEqualTo(2d);
            await Assert.That(exec.GetProperty("duration_ms").GetDouble()).IsEqualTo(1500d);
            await Assert.That(exec.GetProperty("stdout").GetString()).IsEqualTo("out");
        }
    }

    [Test]
    public async Task An_exec_end_is_forgotten_after_an_acknowledged_batch_that_stored_something() {
        var assembler = Codex();
        assembler.Assemble(Raw(0, CodexText, CodexExecEnd), Received);
        assembler.Acknowledged();

        await Assert.That(Exec(assembler.Assemble(Raw(2, CodexOutput), Received).Events.Single())).IsNull();
    }

    static double? TaskDuration(CanonicalBatchEvent e) =>
        e.Payload.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("codex", out var codex)
     && codex.TryGetProperty("task", out var task) ? task.GetProperty("duration_ms").GetDouble() : null;

    [Test]
    public async Task An_amendment_lands_on_a_target_in_its_batch_and_is_dropped_otherwise() {
        var taskComplete = CodexEvent("""{"type":"task_complete","duration_ms":4200}""");

        var together = Codex().Assemble(Raw(0, CodexThinking, CodexText, CodexTokens, taskComplete), Received);
        await Assert.That(TaskDuration(together.Events[0])).IsEqualTo(4200d);

        var assembler = Codex();
        var first     = assembler.Assemble(Raw(0, CodexThinking, CodexText, CodexTokens), Received);
        assembler.Acknowledged();
        var later = assembler.Assemble(Raw(3, taskComplete), Received);

        await Assert.That(TaskDuration(first.Events[0])).IsNull();
        await Assert.That(later.Events).IsEmpty();
        await Assert.That(later.Lines.Length).IsEqualTo(1);
    }

    [Test]
    public async Task The_batch_round_trips_through_CapacitorJsonContext() {
        const string title = $$"""{"type":"ai-title","aiTitle":"Fix it","sessionId":"{{Session}}"}""";
        var raw            = Raw(0, Image(1, Convert.ToBase64String([1, 2, 3])), Assistant(2, "hello", 1, 10), title, CaptureLoss, TurnEnd(5)) with {
            Repository = new() { Branch = "main" }, Strict = true, ObservedCommits = [], MoreFollows = true,
        };

        var json = JsonSerializer.Serialize(Claude().Assemble(raw, Received), CapacitorJsonContext.Default.CanonicalTranscriptBatch);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        await Assert.That(root.GetProperty("format").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo(Session);
        await Assert.That(root.GetProperty("turn_end_line").GetInt32()).IsEqualTo(4);
        await Assert.That(root.GetProperty("more_follows").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("observed_commits").GetArrayLength()).IsEqualTo(0);
        await Assert.That(root.GetProperty("events")[1].GetProperty("cache_creation_tokens").GetInt64()).IsEqualTo(7L);
        await Assert.That(root.TryGetProperty("agent_id", out _)).IsFalse();

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CanonicalTranscriptBatch)!;
        await Assert.That(back.Titles!.Single().Line).IsEqualTo(2);
        await Assert.That(back.CaptureLoss!.Single().Raw).IsEqualTo(CaptureLoss);
        await Assert.That(JsonSerializer.Serialize(back, CapacitorJsonContext.Default.CanonicalTranscriptBatch)).IsEqualTo(json);
    }

    sealed class NoSignals : ILineSignalReader {
        public static readonly NoSignals Instance = new();

        public LineSignals Read(string line, string sessionId, string? agentId) => LineSignals.None;
    }
}
