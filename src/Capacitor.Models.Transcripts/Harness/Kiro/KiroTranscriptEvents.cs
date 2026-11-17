using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// <summary>
/// Projects Kiro CLI's session log (<c>~/.kiro/sessions/cli/{id}.jsonl</c>) into canonical events. Every line
/// is <c>{"version":"v1","kind":…,"data":{…}}</c>:
/// <list type="bullet">
///   <item><c>Prompt</c>: a user message from its text blocks, the request unwrapped from a Kiro Crew envelope.</item>
///   <item><c>AssistantMessage</c>: one event per non-empty text block and per tool use, in block order.</item>
///   <item><c>ToolResults</c>: one result per tool-result block.</item>
///   <item><c>KiroUsageBackfilled</c>: written by the recorder with a turn's credits when Kiro's sidecar
///   records them after the turn was sent.</item>
/// </list>
/// The session's start, directory and model reach the server from Kiro's spawn hook, not from this log.
/// </summary>
public sealed class KiroTranscriptEvents : ITranscriptProjection {
    public static readonly KiroTranscriptEvents Instance = new();

    KiroTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new KiroContext(sessionId, agentId is { Length: > 0 } ? $"{sessionId}:{agentId}" : sessionId);

    /// The receive time goes unused: a reply or result takes its turn's prompt time, and an event with no prompt
    /// time takes the earliest instant, which is what the server stores for one.
    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (KiroContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("kind") is not { } kind || root.Obj("data") is not { } data) return ProjectionResult.Empty;

            return kind switch {
                "Prompt"              => Prompt(data, line, ctx),
                "AssistantMessage"    => Assistant(data, line, ctx),
                "ToolResults"         => ToolResults(data, line, ctx),
                "KiroUsageBackfilled" => UsageBackfill(data, ctx),
                _                     => ProjectionResult.Empty,
            };
        }
    }

    static readonly long MinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    static readonly long MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    static ProjectionResult Prompt(JsonElement data, string line, KiroContext ctx) {
        if (TextContent(data.Arr("content")) is not { } text) return ProjectionResult.Empty;

        DateTimeOffset? at = null;
        if (data.Obj("meta")?.Num("timestamp") is { } seconds) {
            if (seconds < MinSeconds || seconds > MaxSeconds) return ProjectionResult.Reject("prompt timestamp out of range");
            at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            ctx.LastPromptAt = at;
        }

        var message = new UserMessageReceived {
            Content   = KiroCrewPromptEnvelope.Unwrap(text),
            Timestamp = Timestamp.FromDateTimeOffset(at ?? DateTimeOffset.MinValue),
        };

        return ProjectionResult.Of([Event(message, Id(ctx, data.Str("message_id"), "user", line), at ?? DateTimeOffset.MinValue, Format(at))]);
    }

    static ProjectionResult Assistant(JsonElement data, string line, KiroContext ctx) {
        if (data.Arr("content") is not { } content) return ProjectionResult.Empty;

        var messageId    = data.Str("message_id");
        var turnAt       = ctx.LastPromptAt ?? DateTimeOffset.MinValue;
        var recordTs     = Format(ctx.LastPromptAt);
        var (slug, used) = TurnUsage(data.Obj("_kcap_usage"));
        var events       = new List<CanonicalEvent>();

        foreach (var block in content.EnumerateArray()) {
            // A tick apart, so the turn's events keep their order on its one time.
            var at = turnAt.AddTicks(events.Count);

            switch (block.Str("kind")) {
                case "text" when block.Str("data") is { Length: > 0 } text: {
                    // Keyed by position among the line's events: one message can hold several text blocks.
                    var key = string.Create(CultureInfo.InvariantCulture, $"text:{events.Count}");
                    Add(new AssistantTextGenerated { Content = text, Timestamp = Timestamp.FromDateTimeOffset(at) }, key);
                    break;
                }
                case "toolUse" when block.Obj("data") is { } use && use.Str("toolUseId") is { } callId && use.Str("name") is { } name: {
                    var call = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = KiroToolKinds.Of(name) };
                    if (use.Obj("input") is { } input) call.Arguments = StructOf(input);

                    var calls = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(at) };
                    calls.ToolCalls.Add(call);
                    Add(calls, "tool:" + callId);
                    break;
                }
            }

            void Add(IMessage payload, string key) {
                // The turn's usage rides its first event, whichever kind that is.
                var first = events.Count == 0;
                if (first && slug is not null) SchemaExtensions.Of(payload)![KiroExtension.Slug] = slug;
                events.Add(Event(payload, Id(ctx, messageId, key, line), at, recordTs) with { Usage = first ? used : null });
            }
        }

        return ProjectionResult.Of(events);
    }

    static ProjectionResult ToolResults(JsonElement data, string line, KiroContext ctx) {
        if (data.Arr("content") is not { } content) return ProjectionResult.Empty;

        var at       = ctx.LastPromptAt ?? DateTimeOffset.MinValue;
        var recordTs = Format(ctx.LastPromptAt);
        var events   = new List<CanonicalEvent>();

        foreach (var block in content.EnumerateArray()) {
            if (block.Str("kind") != "toolResult" || block.Obj("data") is not { } result
             || result.Str("toolUseId") is not { Length: > 0 } callId) continue;

            var received = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(at) };
            if (TextContent(result.Arr("content")) is { } text) received.Result = text;
            if (result.Str("status") is { } status) received.Extensions[KiroExtension.Slug] = KiroExtension.ToolResult(status);

            events.Add(Event(received, Id(ctx, callId, "result", line), at, recordTs));
        }

        return ProjectionResult.Of(events);
    }

    /// Keyed by the turn's anchor message, never by the line: a re-sent backfill on a new id would count the
    /// turn's credits twice, so a line with no anchor is dropped.
    static ProjectionResult UsageBackfill(JsonElement data, KiroContext ctx) {
        if (data.Str("message_id") is not { Length: > 0 } anchor || ctx.IdScope.Length == 0) return ProjectionResult.Empty;

        var backfill = new KiroUsageBackfill {
            Credits                = Dbl(data, "credits") ?? 0,
            ContextUsagePercentage = Dbl(data, "context_usage_percentage"),
        };

        return ProjectionResult.Of([Event(backfill, TranscriptIds.KiroUsage(ctx.IdScope, anchor), ctx.LastPromptAt ?? DateTimeOffset.MinValue, Format(ctx.LastPromptAt))]);
    }

    /// What the recorder copied onto a turn's final reply from Kiro's sidecar: credits and context fill for the
    /// `kiro` slug, and token counts with a model. Kiro stores zero tokens, so usage needs a positive count.
    static (Struct? Slug, TokenUsage? Usage) TurnUsage(JsonElement? recorded) {
        if (recorded is not { } u) return (null, null);

        var credits = Dbl(u, "credits");
        var fill    = Dbl(u, "context_usage_percentage");
        var slug    = credits is null && fill is null ? null : KiroExtension.Usage(credits, fill);

        var input  = u.Num("input_token_count") is > 0 and var i ? i : (long?)null;
        var output = u.Num("output_token_count") is > 0 and var o ? o : (long?)null;
        if ((input is null && output is null) || u.Str("model") is not { Length: > 0 } model) return (slug, null);

        var usage = new TokenUsage { TotalTokens = (input ?? 0) + (output ?? 0), Model = model };
        if (input is { } inputTokens) usage.InputTokens = inputTokens;
        if (output is { } outputTokens) usage.OutputTokens = outputTokens;

        return (slug, usage);
    }

    /// A line missing its session or key is hashed instead, with the kind keeping a line's events apart.
    static Guid Id(KiroContext ctx, string? key, string kind, string line) =>
        ctx.SessionId.Length > 0 && key is { Length: > 0 }
            ? TranscriptIds.KiroRecord(ctx.SessionId, key, kind)
            : TranscriptIds.KiroFallback(line, kind);

    /// Kiro's text blocks are <c>{"kind":"text","data":…}</c>: the non-empty ones, joined by a newline.
    static string? TextContent(JsonElement? content) {
        if (content is not { } blocks) return null;

        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray()) {
            if (block.Str("kind") != "text" || block.Str("data") is not { Length: > 0 } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    static double? Dbl(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    static string? Format(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    static CanonicalEvent Event(object payload, Guid id, DateTimeOffset at, string? recordTs) =>
        new(CanonicalEventTypes.Of(payload), payload, id, at, recordTs);
}
