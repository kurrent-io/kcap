using System.Globalization;
using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Grok;

/// <summary>
/// Projects Grok Build's <c>updates.jsonl</c> into canonical events. Every line is one JSON-RPC notification:
/// <list type="bullet">
///   <item><c>session/update</c>: an ACP update. A user, reply or thought text run, each written whole on one
///   line; a tool call; and a tool result, on the call's terminal <c>tool_call_update</c> only, since the
///   updates before it enrich the call and carry no outcome.</item>
///   <item><c>_x.ai/session/update</c>: Grok's own. Only <c>turn_completed</c> emits, the turn's usage per model,
///   with <c>inputTokens</c> including the cached read.</item>
/// </list>
/// Every other update emits nothing. The session's start reaches the server from Grok's hooks, not this log.
/// </summary>
public sealed class GrokTranscriptEvents : ITranscriptProjection {
    public static readonly GrokTranscriptEvents Instance = new();

    GrokTranscriptEvents() { }

    static readonly long MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new GrokContext(agentId is not null);

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (GrokContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Obj("params") is not { } prms || prms.Obj("update") is not { } update) return ProjectionResult.Empty;

            var meta   = prms.Obj("_meta");
            // Unchecked, as the server's normalizer multiplies: a root time that overflows wraps, and the server
            // stores the line at the wrapped instant, so rejecting it would drop an event the server keeps.
            var whenMs = meta?.Num("agentTimestampMs") ?? (root.Num("timestamp") is { } seconds ? seconds * 1000 : null);
            if (whenMs < MinMs || whenMs > MaxMs) return ProjectionResult.Reject("timestamp out of range");

            DateTimeOffset? at     = whenMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            var             origin = new Origin(meta?.Str("eventId") ?? TranscriptIds.GrokLineEventId(lineNumber, line), at ?? receivedAt, at?.ToString("O", CultureInfo.InvariantCulture));

            TrackTurn(meta, ctx);

            return root.Str("method") switch {
                "session/update"       => FromAcp(update, origin, ctx),
                "_x.ai/session/update" => FromXai(update, origin, ctx),
                _                      => ProjectionResult.Empty,
            };
        }
    }

    /// What every event from one line shares: the update's event id, and its time, the agent's own when the line has one.
    readonly record struct Origin(string EventId, DateTimeOffset At, string? RecordTimestamp);

    /// Runs before the method is read, so any line naming a turn moves the turn, whatever it emits.
    static void TrackTurn(JsonElement? meta, GrokContext ctx) {
        if (meta is not { } m || m.Str("promptId") is not { } promptId) return;

        if (promptId != ctx.TurnPromptId) {
            ctx.TurnPromptId   = promptId;
            ctx.TurnPeakTokens = 0;
            ctx.TurnAnchor     = null;
        }

        if (m.Num("totalTokens") is { } total && total > ctx.TurnPeakTokens) ctx.TurnPeakTokens = total;
    }

    static ProjectionResult FromAcp(JsonElement update, Origin origin, GrokContext ctx) {
        var timestamp = Timestamp.FromDateTimeOffset(origin.At);

        IMessage? payload = update.Str("sessionUpdate") switch {
            "user_message_chunk"  => Text(update) is { } text ? new UserMessageReceived { Content = text, Timestamp = timestamp } : null,
            "agent_message_chunk" => Text(update) is { } text ? new AssistantTextGenerated { Content = text, Timestamp = timestamp } : null,
            "agent_thought_chunk" => Text(update) is { } text ? new AssistantThinkingGenerated { Content = text, Encrypted = false, Timestamp = timestamp } : null,
            "tool_call"           => ToolCall(update, timestamp),
            "tool_call_update"    => ToolResult(update, timestamp),
            _                     => null,
        };
        if (payload is null) return ProjectionResult.Empty;

        var e = Event(payload, TranscriptIds.Grok(origin.EventId), origin);

        // Text outranks a tool call: a reader shows the turn's text.
        if (e.EventType == CanonicalEventTypes.AssistantTextGenerated
         || (e.EventType == CanonicalEventTypes.AssistantToolCallsGenerated && ctx.TurnAnchor?.EventType != CanonicalEventTypes.AssistantTextGenerated))
            ctx.TurnAnchor = new(e.EventId, e.EventType);

        return ProjectionResult.Of([e]);
    }

    static ProjectionResult FromXai(JsonElement update, Origin origin, GrokContext ctx) {
        if (update.Str("sessionUpdate") != "turn_completed" || ctx.IsSubagent || update.Obj("usage")?.Obj("modelUsage") is not { } byModel)
            return ProjectionResult.Empty;

        var sameTurn = update.Str("prompt_id") is { } turn && turn == ctx.TurnPromptId;
        var peak     = sameTurn ? ctx.TurnPeakTokens : 0;
        var anchor   = sameTurn ? ctx.TurnAnchor : null;

        // The peak is one request's context, so only the model that read the most may claim it. It is chosen before
        // the models that record nothing are left out, as the server chooses it: such a model takes the peak with it.
        var peakModel = byModel.EnumerateObject()
            .Where(m => m.Value.IsObject)
            .OrderByDescending(m => m.Value.Num("inputTokens") ?? 0)
            .Select(m => m.Name)
            .FirstOrDefault();

        var events = new List<CanonicalEvent>();
        foreach (var model in byModel.EnumerateObject()) {
            if (!model.Value.IsObject || Usage(model.Name, model.Value) is not { } usage) continue;

            events.Add(Event(usage with {
                OccupancyTokens = model.Name == peakModel ? peak : 0,
                Targets         = anchor is { } a ? [new CodexUsageBackfillTarget { EventId = a.EventId.ToString(), EventType = a.EventType }] : [],
            }, TranscriptIds.GrokUsage(origin.EventId, model.Name), origin));
        }

        return ProjectionResult.Of(events);
    }

    /// One model's share of a turn, with input net of the cached read. Every count is floored at zero, and a
    /// model with no name or nothing counted records nothing.
    static CodexUsageBackfilledEvent? Usage(string model, JsonElement counts) {
        if (model.Length == 0) return null;

        var cached    = Math.Max(0, counts.Num("cachedReadTokens") ?? 0);
        var input     = Math.Max(0, Math.Max(0, counts.Num("inputTokens") ?? 0) - cached);
        var output    = Math.Max(0, counts.Num("outputTokens") ?? 0);
        var reasoning = counts.Num("reasoningTokens") is { } r ? Math.Max(0, r) : (long?)null;
        if (input == 0 && cached == 0 && output == 0 && (reasoning ?? 0) == 0) return null;

        return new() { Model = model, InputTokens = input, OutputTokens = output, CacheReadTokens = cached, ReasoningTokens = reasoning };
    }

    static string? Text(JsonElement update) => update.Obj("content")?.Str("text") is { Length: > 0 } text ? text : null;

    static AssistantToolCallsGenerated? ToolCall(JsonElement update, Timestamp timestamp) {
        if (update.Str("toolCallId") is not { Length: > 0 } callId) return null;

        var tool = update.Obj("_meta")?.Obj("x.ai/tool");
        var call = new ToolCallInfo { CallId = callId, ToolName = tool?.Str("name") ?? update.Str("title") ?? "" };
        if (GrokToolKinds.Of(tool?.Str("kind")) is { } kind) call.ToolKind = kind;
        if (update.Obj("rawInput") is { } input) call.Arguments = StructOf(input);

        var calls = new AssistantToolCallsGenerated { Timestamp = timestamp };
        calls.ToolCalls.Add(call);
        return calls;
    }

    static ToolResultReceived? ToolResult(JsonElement update, Timestamp timestamp) {
        var status = update.Str("status");
        if (status is not ("completed" or "failed") || update.Str("toolCallId") is not { Length: > 0 } callId) return null;

        var result = new ToolResultReceived { CallId = callId, Timestamp = timestamp };
        if (ResultText(update) is { } text) result.Result = text;
        if (status == "failed") result.Extensions[AcpExtension.Slug] = AcpExtension.ToolResultError();
        return result;
    }

    /// The update's text blocks, joined by a newline; else what <c>rawOutput</c> holds: an MCP call's error or
    /// output, a <c>content</c> or <c>text</c> string, or the whole of it as JSON.
    static string? ResultText(JsonElement update) {
        if (update.Arr("content") is { } blocks) {
            var texts = blocks.EnumerateArray().Select(b => b.Obj("content")?.Str("text")).OfType<string>().ToArray();
            if (texts.Length > 0) return string.Join("\n", texts);
        }

        if (update.Prop("rawOutput") is not { } raw) return null;

        if (raw.Obj("output") is { } mcp) {
            if (mcp.Prop("Error") is { } error) return error.IsString ? error.GetString() : error.GetRawText();
            if (mcp.Prop("OkayOutput") is { } ok) return ok.IsString ? ok.GetString() : ok.GetRawText();
        }

        return raw.Str("content") ?? raw.Str("text") ?? raw.GetRawText();
    }

    static CanonicalEvent Event(object payload, Guid id, Origin origin) =>
        new(CanonicalEventTypes.Of(payload), payload, id, origin.At, origin.RecordTimestamp);
}
