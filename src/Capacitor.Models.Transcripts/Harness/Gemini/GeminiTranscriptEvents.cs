using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Gemini;

/// <summary>
/// Projects Gemini CLI's chat recording into canonical events. The file is a delta log: a header
/// record with no <c>type</c>, re-appended on every resume, and <c>$set</c> patches to Gemini's
/// in-memory document emit nothing; bare message records carry the conversation.
/// <list type="bullet">
///   <item><c>user</c> → <see cref="UserMessageReceived"/> from its text parts, except the
///   <c>&lt;session_context&gt;</c> bootstrap. A record of only <c>functionResponse</c> parts
///   echoes results the preceding <c>gemini</c> record carried, so it emits nothing.</item>
///   <item><c>gemini</c> → thinking, text, and per <c>toolCalls</c> entry a call and its inline
///   result, each a tick after the one before. Usage rides the first part.</item>
/// </list>
/// Gemini writes a tool-using turn as two records under one id, a snapshot without the calls and
/// then the whole turn, so both derive the same part ids. The second leaves out the parts the caller
/// has marked stored, and carries usage only when its first part is not one of them.
/// </summary>
public sealed class GeminiTranscriptEvents : ITranscriptProjection {
    public static readonly GeminiTranscriptEvents Instance = new();

    /// Renamed so the call reads exactly as a Claude elicitation, which the server turns into an
    /// interrupt and the chat into a question card.
    const string AskUser             = "ask_user";
    const string ElicitationToolName = "AskUserQuestion";

    GeminiTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new GeminiContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Prop("$set") is not null || root.Str("type") is not { } type) return ProjectionResult.Empty;

            var record      = TranscriptIds.GeminiRecord(root.Str("id"), line);
            var (at, rawTs) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);

            return type switch {
                "user"   => User(root, record, at, rawTs),
                "gemini" => Gemini(root, record, at, rawTs, (GeminiContext)context),
                _        => ProjectionResult.Empty,
            };
        }
    }

    static ProjectionResult User(JsonElement root, Guid record, DateTimeOffset at, string? rawTs) {
        var text = PartsText(root.Prop("content"), skipFunctionResponses: true);
        if (text.Length == 0 || text.StartsWith("<session_context>", StringComparison.Ordinal)) return ProjectionResult.Empty;

        var message = new UserMessageReceived { Content = text, Timestamp = Timestamp.FromDateTimeOffset(at) };
        return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.Of(message), message, record, at, rawTs)]);
    }

    static ProjectionResult Gemini(JsonElement root, Guid record, DateTimeOffset at, string? rawTs, GeminiContext context) {
        var parts = new List<CanonicalEvent>();

        if (Thoughts(root.Arr("thoughts")) is { Length: > 0 } thoughts) {
            var thinking = new AssistantThinkingGenerated { Content = thoughts, Timestamp = Timestamp.FromDateTimeOffset(at) };
            parts.Add(new(CanonicalEventTypes.Of(thinking), thinking, TranscriptIds.Sibling(record, "thinking"), at, rawTs));
        }

        if (PartsText(root.Prop("content"), skipFunctionResponses: false) is { Length: > 0 } content) {
            var textAt = at.AddTicks(parts.Count);
            var text   = new AssistantTextGenerated { Content = content, Timestamp = Timestamp.FromDateTimeOffset(textAt) };
            parts.Add(new(CanonicalEventTypes.Of(text), text, TranscriptIds.Sibling(record, "text"), textAt, rawTs));
        }

        if (root.Arr("toolCalls") is { } toolCalls) {
            foreach (var call in toolCalls.EnumerateArray()) {
                if (call.Str("id") is not { } callId || call.Str("name") is not { } name) continue;

                var info = new ToolCallInfo {
                    CallId   = callId,
                    ToolName = name == AskUser ? ElicitationToolName : name,
                    ToolKind = GeminiToolKinds.Of(name),
                };
                if (call.Obj("args") is { } args) info.Arguments = StructOf(args);

                var callAt = at.AddTicks(parts.Count);
                var calls  = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(callAt) };
                calls.ToolCalls.Add(info);
                if (GeminiExtension.ToolCall(call.Str("displayName"), call.Str("description"), CanonicalId(call.Str("agentId"))) is { } hints)
                    calls.Extensions[GeminiExtension.Slug] = hints;
                parts.Add(new(CanonicalEventTypes.Of(calls), calls, TranscriptIds.Sibling(record, "tool:" + callId), callAt, rawTs));

                var resultAt = at.AddTicks(parts.Count);
                var result   = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(resultAt) };
                if (ToolOutput(call.Arr("result")) is { } output) result.Result = output;
                if (GeminiExtension.ToolResult(call.Str("status")) is { } status) result.Extensions[GeminiExtension.Slug] = status;
                parts.Add(new(CanonicalEventTypes.Of(result), result, TranscriptIds.Sibling(record, "result:" + callId), resultAt, rawTs));
            }
        }

        if (parts.Count == 0) return ProjectionResult.Empty;

        var survivors = parts.Where(p => !context.IsPersisted(p.EventId)).ToList();
        if (survivors.Count == 0) return ProjectionResult.Empty;

        if (!context.IsPersisted(parts[0].EventId) && Usage(root) is { } usage) survivors[0] = parts[0] with { Usage = usage };
        return ProjectionResult.Of(survivors);
    }

    /// Gemini's <c>input</c> already includes the cached tokens, so input is net of them. Its
    /// <c>tool</c> count, the tool-use prompt, has no canonical slot and rides additional counts.
    /// Null for a record reporting none of input, output and model, or nothing the usage can hold.
    static TokenUsage? Usage(JsonElement root) {
        var model    = root.Str("model");
        var tokens   = root.Obj("tokens");
        var cached   = tokens?.Num("cached");
        var input    = tokens?.Num("input") is { } gross ? Math.Max(0, gross - (cached ?? 0)) : (long?)null;
        var output   = tokens?.Num("output");
        var thoughts = tokens?.Num("thoughts");
        var tool     = tokens?.Num("tool");

        if (input is null && output is null && model is null) return null;
        var additive = input is not null || output is not null || cached is not null;
        if (!additive && thoughts is null) return null;

        var usage = new TokenUsage();
        if (additive) usage.TotalTokens                 = (input ?? 0) + (output ?? 0) + (cached ?? 0);
        if (input is not null) usage.InputTokens        = input.Value;
        if (output is not null) usage.OutputTokens      = output.Value;
        if (cached is not null) usage.CachedInputTokens = cached.Value;
        if (thoughts is not null) usage.ReasoningTokens = thoughts.Value;
        if (model is not null) usage.Model              = model;
        if (tool is > 0) {
            usage.AdditionalCounts = new Struct();
            usage.AdditionalCounts.Fields["tool_use_prompt_tokens"] = Value.ForNumber(tool.Value);
        }
        return usage;
    }

    /// <c>content</c> is a bare string or a list of parts, joined by their non-empty <c>text</c>.
    static string PartsText(JsonElement? content, bool skipFunctionResponses) {
        if (content is not { } c) return "";
        if (c.IsString) return c.GetString() ?? "";
        if (!c.IsArray) return "";

        var pieces = new List<string>();
        foreach (var part in c.EnumerateArray()) {
            if (skipFunctionResponses && part.Prop("functionResponse") is not null) continue;
            if (part.Str("text") is { Length: > 0 } text) pieces.Add(text);
        }
        return string.Join('\n', pieces);
    }

    static string Thoughts(JsonElement? thoughts) {
        if (thoughts is not { } items) return "";

        var pieces = new List<string>();
        foreach (var thought in items.EnumerateArray()) {
            var subject     = thought.Str("subject");
            var description = thought.Str("description");

            if (subject is { Length: > 0 } && description is { Length: > 0 }) pieces.Add($"**{subject}**\n{description}");
            else if (description is { Length: > 0 }) pieces.Add(description);
            else if (subject is { Length: > 0 }) pieces.Add(subject);
        }
        return string.Join("\n\n", pieces);
    }

    /// Each item is normally <c>{functionResponse:{response:{output}}}</c>; a response without an
    /// <c>output</c>, or an item of another shape, is kept as its raw JSON. Null for no items.
    static string? ToolOutput(JsonElement? result) {
        if (result is not { } items) return null;

        var pieces = new List<string>();
        foreach (var item in items.EnumerateArray())
            pieces.Add(item.Obj("functionResponse")?.Obj("response") is { } response ? response.Str("output") ?? response.GetRawText() : item.GetRawText());
        return pieces.Count > 0 ? string.Join('\n', pieces) : null;
    }

    /// The dashless form the server keys a subagent by; an id that is not a Guid passes through.
    static string? CanonicalId(string? id) => id is not null && Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;
}
