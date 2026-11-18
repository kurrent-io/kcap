using System.Reflection;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Capacitor.Models.Transcripts.Harness.Muse;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class CanonicalEventJsonTests {
    [Test]
    public async Task ReadsEachConversationalTypeBySnakeCaseName() {
        var user = CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":"hello","brand_new":1}""");
        await Assert.That(user).IsTypeOf<UserMessageReceived>();
        await Assert.That(((UserMessageReceived)user!).Content).IsEqualTo("hello");

        var calls = CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantToolCallsGenerated,
            """{"tool_calls":[{"call_id":"t1","tool_name":"Bash","arguments":{"command":"ls"}}]}""");
        var call = ((AssistantToolCallsGenerated)calls!).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("t1");
        await Assert.That(call.ToolName).IsEqualTo("Bash");
        await Assert.That(call.Arguments!.Fields["command"].StringValue).IsEqualTo("ls");

        var result = CanonicalEventJson.TryParse(CanonicalEventTypes.ToolResultReceived, """{"call_id":"t1","result":"ok"}""");
        await Assert.That(((ToolResultReceived)result!).Result).IsEqualTo("ok");
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantTextGenerated, """{"content":"Hi"}""")).IsTypeOf<AssistantTextGenerated>();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantThinkingGenerated, """{"content":"hm"}""")).IsTypeOf<AssistantThinkingGenerated>();
    }

    [Test]
    public async Task ReadsTheServersSubagentCompletion() {
        var completed = CanonicalEventJson.TryParse(CanonicalEventTypes.SubagentCompleted,
            """{"agent_id":"a9f262478e032f427","timestamp":"2026-09-17T10:01:00Z"}""");
        await Assert.That(completed).IsTypeOf<SubagentCompleted>();
        await Assert.That(((SubagentCompleted)completed!).AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task ReadsTheServersSubagentStart() {
        var started = CanonicalEventJson.TryParse(CanonicalEventTypes.SubagentStarted, """{"agent_id":"a9f262478e032f427","agent_type":"Explore"}""");
        await Assert.That(started).IsTypeOf<SubagentStarted>();
        await Assert.That(((SubagentStarted)started!).AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task ReadsTheServersSessionStartWithItsSourceAndItsOwnTime() {
        var started = CanonicalEventJson.TryParse(CanonicalEventTypes.SessionStarted,
            """{"timestamp":"2026-11-04T17:29:08Z","extensions":{"claude_code":{"source":"Resume"}}}""");
        await Assert.That(started).IsTypeOf<SessionStarted>();
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(started!, "claude_code"), "source")).IsEqualTo("Resume");
        await Assert.That(CanonicalEventTime.Of(started!)).IsEqualTo(new DateTimeOffset(2026, 11, 4, 17, 29, 8, TimeSpan.Zero));
    }

    [Test]
    public async Task UnknownTypesAndMalformedPayloadsReadAsNull() {
        await Assert.That(CanonicalEventJson.TryParse("NotAType", """{"request_id":"r1"}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UsageApplied, """{}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, "not json")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":42}""")).IsNull();
    }

    /// <summary>A stored compaction reaches a remote chat as the note a local one shows.</summary>
    [Test]
    public async Task A_stored_compaction_reads_back_as_its_record() {
        var parsed = CanonicalEventJson.TryParse(CanonicalEventTypes.ContextCompacted, """{"encrypted_content":"x","replacement_history":[]}""");

        await Assert.That(parsed).IsTypeOf<ContextCompactedEvent>();
        await Assert.That(((ContextCompactedEvent)parsed!).EncryptedContent).IsEqualTo("x");
    }

    [Test]
    public async Task A_stored_claude_compaction_reads_back_with_its_counts() {
        var parsed = (ContextCompactedEvent)CanonicalEventJson.TryParse(CanonicalEventTypes.ContextCompacted,
            """{"replacement_history":null,"encrypted_content":null,"pre_tokens":167005,"trigger":"auto"}""")!;

        await Assert.That(parsed.PreTokens).IsEqualTo(167_005L);
        await Assert.That(parsed.PostTokens).IsNull();
        await Assert.That(parsed.Trigger).IsEqualTo("auto");
    }

    static readonly Timestamp At = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 4, 17, 29, 8, TimeSpan.Zero));

    /// One representative payload per stored event type; `UsageApplied` is never stored as itself.
    static Dictionary<string, object> Samples() {
        var args = new Struct();
        args.Fields["command"] = Value.ForString("ls");
        var session = new SessionStarted { Timestamp = At };
        session.Extensions["claude_code"] = new Struct { Fields = { ["source"] = Value.ForString("Resume") } };
        return new() {
            [CanonicalEventTypes.UserMessageReceived]         = new UserMessageReceived { Content = "hello", Timestamp = At },
            [CanonicalEventTypes.AssistantTextGenerated]      = new AssistantTextGenerated { Content = "Hi", Timestamp = At },
            [CanonicalEventTypes.AssistantThinkingGenerated]  = new AssistantThinkingGenerated { Content = "hm", Timestamp = At },
            [CanonicalEventTypes.AssistantToolCallsGenerated] = new AssistantToolCallsGenerated { ToolCalls = { new ToolCallInfo { CallId = "t1", ToolName = "Bash", Arguments = args } }, Timestamp = At },
            [CanonicalEventTypes.ToolResultReceived]          = new ToolResultReceived { CallId = "t1", Result = "ok", Timestamp = At },
            [CanonicalEventTypes.SessionStarted]              = session,
            [CanonicalEventTypes.SubagentCompleted]           = new SubagentCompleted { AgentId = "a1", Timestamp = At },
            [CanonicalEventTypes.SubagentStarted]             = new SubagentStarted { AgentId = "a1", AgentType = "Explore", Timestamp = At },
            [CanonicalEventTypes.ContextCompacted]            = new ContextCompactedEvent { PreTokens = 167_005, Trigger = "auto", EncryptedContent = "x" },
            [CanonicalEventTypes.UserMessageRetracted]        = new UserMessageRetractedEvent { MessageId = Guid.Parse("11111111-2222-3333-4444-555555555555") },
            [CanonicalEventTypes.CodexUsageBackfilled]        = new CodexUsageBackfilledEvent {
                Model = "gpt", InputTokens = 5, OutputTokens = 6, CacheReadTokens = 7, ReasoningTokens = 1, ModelContextWindow = 1000,
                Targets = [new CodexUsageBackfillTarget { EventId = "e1", EventType = CanonicalEventTypes.AssistantTextGenerated, ToolName = "t", IsEcho = true }],
            },
            [CanonicalEventTypes.KiroUsageBackfilled]         = new KiroUsageBackfill { Credits = 1.5, ContextUsagePercentage = 12.5 },
            [CanonicalEventTypes.MuseUsageBackfilled]         = new MuseUsageBackfill(),
            [CanonicalEventTypes.InterruptIssued]             = new InterruptIssued { RequestId = "r1", Kind = "input", ToolName = "AskUserQuestion", Prompt = "which?", Timestamp = At },
            [CanonicalEventTypes.AntigravityUsageBackfilled]  = new AntigravityUsageBackfill { Model = "m", InputTokens = 1, OutputTokens = 2, CacheReadTokens = 3, CacheWriteTokens = 4 },
            [CanonicalEventTypes.CopilotUsageBackfilled]      = new CopilotUsageBackfill { Model = "m", InputTokens = 1, OutputTokens = 2, NanoAiu = 9 },
            [CanonicalEventTypes.CopilotSessionMetrics]       = new CopilotSessionMetrics { TotalNanoAiu = 5, LinesAdded = 3, FilesModified = ["a.cs"] },
        };
    }

    static IEnumerable<string> StoredTypeNames() =>
        typeof(CanonicalEventTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(name => name != CanonicalEventTypes.UsageApplied);

    [Test]
    public async Task Every_stored_event_type_has_a_sample() {
        await Assert.That(Samples().Keys.Order().ToArray()).IsEquivalentTo(StoredTypeNames().Order().ToArray());
    }

    [Test]
    public async Task Every_stored_event_type_writes_and_reads_back_to_the_same_json() {
        foreach (var (type, payload) in Samples()) {
            var json = CanonicalEventJson.Write(payload);
            var back = CanonicalEventJson.TryParse(CanonicalEventTypes.Of(payload), json);

            await Assert.That(CanonicalEventTypes.Of(payload)).IsEqualTo(type);
            await Assert.That(back).IsNotNull().Because(type);
            await Assert.That(back!.GetType()).IsEqualTo(payload.GetType());
            await Assert.That(CanonicalEventJson.Write(back)).IsEqualTo(json).Because(type);
        }
    }

    [Test]
    public async Task Protobuf_payloads_are_written_under_their_snake_case_names() {
        var json = CanonicalEventJson.Write(new ToolResultReceived { CallId = "t1", Result = "ok" });

        await Assert.That(json).Contains("\"call_id\"").And.DoesNotContain("callId");
    }

    [Test]
    public async Task A_payload_with_no_canonical_type_is_refused() {
        await Assert.That(() => CanonicalEventJson.Write(new object())).Throws<ArgumentException>();
    }

    [Test]
    public async Task A_usage_application_is_never_stored_as_itself() {
        var applied = new UsageApplied(new TokenUsage(), Guid.NewGuid(), null, []);

        await Assert.That(() => CanonicalEventJson.Write(applied)).Throws<ArgumentException>();
    }
}
