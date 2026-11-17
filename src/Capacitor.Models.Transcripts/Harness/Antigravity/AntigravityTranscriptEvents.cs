using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// <summary>
/// Projects Antigravity's conversation transcript (<c>brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c>)
/// into canonical events. Every line is a step record, keyed by <c>type</c>:
/// <list type="bullet">
///   <item><c>USER_INPUT</c>: a user message from the text inside its <c>&lt;USER_REQUEST&gt;</c> wrapper; the
///   blocks Antigravity appends after it were never typed.</item>
///   <item><c>PLANNER_RESPONSE</c>: thinking, text, and per <c>tool_calls</c> entry a call, each a tick after the
///   one before; <c>ask_question</c> and <c>ask_permission</c> become interrupts instead.</item>
///   <item><c>RUN_COMMAND</c>, <c>VIEW_FILE</c>, <c>LIST_DIRECTORY</c>, <c>CODE_ACTION</c>: the result of the oldest
///   call still waiting, since calls carry no id and each gets one derived from its step.</item>
///   <item><c>USAGE</c>: written by the recorder per generation row of the conversation database, which is where
///   Antigravity keeps tokens and model.</item>
/// </list>
/// A result can be written before its planner step, in the same flush or an earlier one. Such a result is held,
/// and the planner that owns it emits it after its calls, stored under the result's own line.
/// </summary>
public sealed partial class AntigravityTranscriptEvents : ITranscriptProjection, IStepOrderedProjection {
    public static readonly AntigravityTranscriptEvents Instance = new();

    AntigravityTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new AntigravityContext(agentId is { Length: > 0 } ? $"{sessionId}:{agentId}" : sessionId);

    public long StepOrder(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Num("step_index") ?? long.MaxValue;
        } catch (JsonException) {
            return long.MaxValue;
        }
    }

    /// The receive time stands in only for an interrupt whose step has no time: a step without one is stored at
    /// the earliest instant, as the server stores it.
    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (AntigravityContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") is not { } type) return ProjectionResult.Empty;

            var recordTs = root.Str("created_at");
            if (recordTs is not null) ctx.LastCreatedAt = recordTs;

            var step = new Step(TranscriptIds.AntigravityStep(line), lineNumber, root.Num("step_index") ?? long.MaxValue, Instant(recordTs), recordTs);

            return type switch {
                "USER_INPUT"       => User(root, step),
                "PLANNER_RESPONSE" => Planner(root, step, receivedAt, ctx),
                "RUN_COMMAND" or "CODE_ACTION" or "VIEW_FILE" or "LIST_DIRECTORY" => Result(root, type, step, ctx),
                "USAGE"            => Usage(root, step, ctx),
                _                  => ProjectionResult.Empty,
            };
        }
    }

    readonly record struct Step(Guid Id, int LineNumber, long Order, DateTimeOffset At, string? RecordTimestamp);

    static ProjectionResult User(JsonElement root, Step step) {
        if (RequestText(root.Str("content")) is not { Length: > 0 } text) return ProjectionResult.Empty;

        var message = new UserMessageReceived { Content = text, Timestamp = Timestamp.FromDateTimeOffset(step.At) };
        return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.UserMessageReceived, message, step.Id, step.At, step.RecordTimestamp)]);
    }

    /// The text inside <c>&lt;USER_REQUEST&gt;</c>, else the whole content, trimmed.
    static string? RequestText(string? content) {
        if (string.IsNullOrEmpty(content)) return null;

        const string open  = "<USER_REQUEST>";
        const string close = "</USER_REQUEST>";

        var start = content.IndexOf(open, StringComparison.Ordinal);
        var end   = content.IndexOf(close, StringComparison.Ordinal);
        return start >= 0 && end > start ? content[(start + open.Length)..end].Trim() : content.Trim();
    }

    static ProjectionResult Planner(JsonElement root, Step step, DateTimeOffset receivedAt, AntigravityContext ctx) {
        var events = new List<CanonicalEvent>();

        if (root.Str("thinking") is { Length: > 0 } thinking) {
            var payload = new AssistantThinkingGenerated { Content = thinking, Timestamp = Timestamp.FromDateTimeOffset(step.At) };
            events.Add(new(CanonicalEventTypes.AssistantThinkingGenerated, payload, TranscriptIds.Sibling(step.Id, "thinking"), step.At, step.RecordTimestamp));
        }

        if (root.Str("content") is { Length: > 0 } content) {
            var at      = step.At.AddTicks(events.Count);
            var payload = new AssistantTextGenerated { Content = content, Timestamp = Timestamp.FromDateTimeOffset(at) };
            events.Add(new(CanonicalEventTypes.AssistantTextGenerated, payload, TranscriptIds.Sibling(step.Id, "text"), at, step.RecordTimestamp));
        }

        if (root.Arr("tool_calls") is { } toolCalls) {
            var index = -1;
            foreach (var call in toolCalls.EnumerateArray()) {
                index++;
                if (call.Str("name") is not { } name) continue;

                var key     = index.ToString(CultureInfo.InvariantCulture);
                var callId  = TranscriptIds.Sibling(step.Id, "call:" + key).ToString("N");
                var eventId = TranscriptIds.Sibling(step.Id, "toolcall:" + key);
                var at      = step.At.AddTicks(events.Count);

                if (name is AntigravityInterrupts.AskQuestion or AntigravityInterrupts.AskPermission) {
                    // The server stamps an interrupt with its clock when the step has no time at all.
                    var asked     = at == default ? receivedAt : at;
                    var interrupt = name == AntigravityInterrupts.AskQuestion
                        ? AntigravityInterrupts.Question(callId, call.Obj("args"), asked)
                        : AntigravityInterrupts.Permission(callId, call.Obj("args"), asked);
                    events.Add(new(CanonicalEventTypes.InterruptIssued, interrupt, eventId, asked, step.RecordTimestamp));
                    continue;
                }

                var info = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = AntigravityToolKinds.Of(name) };
                if (call.Obj("args") is { } args) info.Arguments = StructOf(args);

                var calls = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(at) };
                calls.ToolCalls.Add(info);
                events.Add(new(CanonicalEventTypes.AssistantToolCallsGenerated, calls, eventId, at, step.RecordTimestamp));

                if (ctx.QueuedCallIds.Add(callId)) ctx.PendingCallIds.Enqueue(callId);
            }
        }

        if (ctx.Claimed.TryGetValue(step.Id, out var claimed))
            foreach (var held in claimed) events.Add(Held(held, ctx.PairedCallIds[held.EventId]));

        Claim(step, events, ctx);
        return ProjectionResult.Of(events);
    }

    /// Pairs held results with waiting calls, lowest step first, whatever order they were held in. Only a result
    /// at a higher step than this planner's can be one of its calls'; the rest stay held for a later planner.
    static void Claim(Step planner, List<CanonicalEvent> events, AntigravityContext ctx) {
        if (ctx.Held.Count == 0 || ctx.PendingCallIds.Count == 0) return;

        var remaining = new List<AntigravityHeldResult>();
        foreach (var held in ctx.Held.OrderBy(h => h.Step)) {
            if (held.Step <= planner.Order || !ctx.PendingCallIds.TryDequeue(out var callId)) {
                remaining.Add(held);
                continue;
            }

            ctx.PairedCallIds[held.EventId] = callId;
            if (!ctx.Claimed.TryGetValue(planner.Id, out var claimed)) ctx.Claimed[planner.Id] = claimed = [];
            claimed.Add(held);
            events.Add(Held(held, callId));
        }

        ctx.Held = remaining;
    }

    static ProjectionResult Result(JsonElement root, string type, Step step, AntigravityContext ctx) {
        if (ctx.HeldResultIds.Contains(step.Id)) return ProjectionResult.Empty;

        var content = root.Str("content");
        // A viewed file or listing could quote the failure line; only a command's own output means it.
        var failed = root.Str("error") is { Length: > 0 }
                  || (type is "RUN_COMMAND" or "CODE_ACTION" && content is not null && CommandFailed().IsMatch(content));

        if (ctx.PairedCallIds.TryGetValue(step.Id, out var paired) || ctx.PendingCallIds.TryDequeue(out paired)) {
            ctx.PairedCallIds[step.Id] = paired;
            return ProjectionResult.Of([ToolResult(step.Id, paired, content, failed, step.At, step.RecordTimestamp)]);
        }

        ctx.HeldResultIds.Add(step.Id);
        ctx.Held.Add(new(step.Id, step.LineNumber, step.At, step.RecordTimestamp, content, failed, step.Order));
        return ProjectionResult.Empty;
    }

    static CanonicalEvent Held(AntigravityHeldResult held, string callId) =>
        ToolResult(held.EventId, callId, held.Content, held.IsError, held.At, held.RecordTimestamp) with { LineNumber = held.LineNumber };

    static CanonicalEvent ToolResult(Guid id, string callId, string? content, bool failed, DateTimeOffset at, string? recordTs) {
        var result = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(at) };
        if (content is { Length: > 0 }) result.Result = content;
        if (failed) result.Extensions[AntigravityExtension.Slug] = AntigravityExtension.ToolResultFailed();

        return new(CanonicalEventTypes.ToolResultReceived, result, id, at, recordTs);
    }

    /// Keyed by the row, never the line, so a re-sent row counts once; a row the id cannot name is dropped rather
    /// than counted twice under a line hash. A row written without a time takes the last step's the first time it
    /// is projected and keeps that time when it is sent again, after later steps.
    static ProjectionResult Usage(JsonElement root, Step step, AntigravityContext ctx) {
        if (root.Num("gen_row") is not { } row || ctx.IdScope.Length == 0) return ProjectionResult.Empty;

        var id = TranscriptIds.AntigravityUsage(ctx.IdScope, row);
        if (!ctx.UsageTimes.TryGetValue(id, out var first)) ctx.UsageTimes[id] = first = step.RecordTimestamp ?? ctx.LastCreatedAt;
        var recordTs = step.RecordTimestamp ?? first;

        var backfill = new AntigravityUsageBackfill {
            Model            = root.Str("model"),
            InputTokens      = root.Num("input_tokens") ?? 0,
            OutputTokens     = (root.Num("output_tokens") ?? 0) + (root.Num("reasoning_tokens") ?? 0),
            CacheReadTokens  = root.Num("cache_read_tokens") ?? 0,
            CacheWriteTokens = root.Num("cache_write_tokens") ?? 0,
        };

        return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.AntigravityUsageBackfilled, backfill, id, Instant(recordTs), recordTs)]);
    }

    static DateTimeOffset Instant(string? raw) =>
        raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;

    /// Antigravity's marker line for a failed command; the digits keep output that only mentions failing out.
    [GeneratedRegex(@"command failed with exit code:\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandFailed();
}
