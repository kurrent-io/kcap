using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// <summary>
/// Projects OpenCode's transcript into canonical events. The kcap plugin writes one line per message, in
/// the <c>{info, parts}</c> shape <c>opencode export</c> returns, beside its own <c>session_title</c>
/// records, which carry no conversation.
/// <list type="bullet">
///   <item>A user message is one <see cref="UserMessageReceived"/> from its visible <c>text</c> parts.</item>
///   <item>An assistant message is one event per visible <c>reasoning</c> or <c>text</c> part and two per
///   settled <c>tool</c> part, the call and its result, in part order and each a tick after the one
///   before; the message's usage rides on the first.</item>
/// </list>
/// A <c>pending</c> or <c>running</c> tool part emits nothing. Its ids are derived from the part id
/// alone and the server keeps the first event under an id, so a snapshot of the call taken before it
/// settled would freeze its partial arguments.
/// </summary>
public sealed class OpenCodeTranscriptEvents : ITranscriptProjection {
    public static readonly OpenCodeTranscriptEvents Instance = new();

    const long MinUnixMs = -62_135_596_800_000;
    const long MaxUnixMs = 253_402_300_799_999;

    OpenCodeTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new OpenCodeContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") == "session_title" || root.Obj("info") is not { } info || info.Str("role") is not { } role)
                return ProjectionResult.Empty;

            var created = info.Obj("time")?.Num("created");
            if (created is < MinUnixMs or > MaxUnixMs) return ProjectionResult.Reject("time.created is out of range");

            var at       = created is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : receivedAt;
            var recordTs = created is null ? null : at.ToString("O");

            return role switch {
                "user"      => User(info, root, at, recordTs),
                "assistant" => Assistant(info, root, at, recordTs),
                _           => ProjectionResult.Empty,
            };
        }
    }

    static ProjectionResult User(JsonElement info, JsonElement root, DateTimeOffset at, string? recordTs) {
        var text = root.Arr("parts") is { } parts ? VisibleText(parts) : "";
        if (text.Length == 0) return ProjectionResult.Empty;

        var message = new UserMessageReceived { Content = text, Timestamp = Timestamp.FromDateTimeOffset(at) };
        return ProjectionResult.Of([Event(message, TranscriptIds.OpenCode(info.Str("id") ?? text), at, recordTs)]);
    }

    static ProjectionResult Assistant(JsonElement info, JsonElement root, DateTimeOffset at, string? recordTs) {
        if (root.Arr("parts") is not { } parts) return ProjectionResult.Empty;

        var events = new List<CanonicalEvent>();
        foreach (var part in parts.EnumerateArray()) {
            if (part.Str("type") is not { } type || part.Str("id") is not { } partId) continue;

            var partAt = at.AddTicks(events.Count);
            switch (type) {
                case "reasoning" when !IsHidden(part) && part.Str("text") is { Length: > 0 } thought:
                    var thinking = new AssistantThinkingGenerated { Content = thought, Timestamp = Timestamp.FromDateTimeOffset(partAt) };
                    events.Add(Event(thinking, TranscriptIds.OpenCode(partId), partAt, recordTs));
                    break;
                case "text" when !IsHidden(part) && part.Str("text") is { Length: > 0 } text:
                    var reply = new AssistantTextGenerated { Content = text, Timestamp = Timestamp.FromDateTimeOffset(partAt) };
                    events.Add(Event(reply, TranscriptIds.OpenCode(partId), partAt, recordTs));
                    break;
                case "tool":
                    Tool(part, partId, at, recordTs, events);
                    break;
            }
        }

        if (events.Count == 0) return ProjectionResult.Empty;
        if (Usage(info) is { } usage) events[0] = events[0] with { Usage = usage.Usage, CacheCreationTokens = usage.CacheWrite };
        return ProjectionResult.Of(events);
    }

    static void Tool(JsonElement part, string partId, DateTimeOffset at, string? recordTs, List<CanonicalEvent> events) {
        if (part.Str("callID") is not { } callId || part.Str("tool") is not { } name) return;
        if (part.Obj("state") is not { } state || state.Str("status") is not { } status || status is not ("completed" or "error")) return;

        var call = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = OpenCodeToolKinds.Of(name) };
        if (state.Obj("input") is { } input) call.Arguments = StructOf(input);

        var callAt = at.AddTicks(events.Count);
        var calls  = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(callAt) };
        calls.ToolCalls.Add(call);
        if (name == "task" && state.Obj("metadata")?.Str("sessionId") is { Length: > 0 } childSessionId)
            calls.Extensions[OpenCodeExtension.Slug] = OpenCodeExtension.ToolCall(childSessionId);
        events.Add(Event(calls, TranscriptIds.OpenCodeToolCall(partId), callAt, recordTs));

        var resultAt = at.AddTicks(events.Count);
        var result   = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(resultAt) };
        if ((status == "error" ? state.Str("error") : state.Str("output")) is { Length: > 0 } output) result.Result = output;
        result.Extensions[OpenCodeExtension.Slug] = OpenCodeExtension.ToolResult(status, state.Str("title"));
        events.Add(Event(result, TranscriptIds.OpenCodeToolResult(partId), resultAt, recordTs));
    }

    /// The message's counts as the server writes them: the four additive counts summed into the total,
    /// the model id verbatim, and nothing when the message reports neither input, output nor a model.
    static (TokenUsage Usage, long? CacheWrite)? Usage(JsonElement info) {
        var model     = info.Str("modelID");
        var tokens    = info.Obj("tokens");
        var input     = tokens?.Num("input");
        var output    = tokens?.Num("output");
        var reasoning = tokens?.Num("reasoning");
        var cache     = tokens?.Obj("cache");
        var read      = cache?.Num("read");
        var write     = cache?.Num("write");
        if (input is null && output is null && model is null) return null;

        var additive = input is not null || output is not null || read is not null || write is not null;
        if (!additive && reasoning is null) return null;

        var usage = new TokenUsage();
        if (additive) usage.TotalTokens               = (input ?? 0) + (output ?? 0) + (read ?? 0) + (write ?? 0);
        if (input is { } i) usage.InputTokens         = i;
        if (output is { } o) usage.OutputTokens       = o;
        if (read is { } r) usage.CachedInputTokens    = r;
        if (reasoning is { } t) usage.ReasoningTokens = t;
        if (model is not null) usage.Model            = model;
        return (usage, write);
    }

    static string VisibleText(JsonElement parts) => string.Join('\n', parts.EnumerateArray()
        .Where(part => part.Str("type") == "text" && !IsHidden(part))
        .Select(part => part.Str("text"))
        .OfType<string>()
        .Where(text => text.Length > 0));

    /// OpenCode flags the text it injects or elides as `synthetic` or `ignored`.
    static bool IsHidden(JsonElement part) => part.Bool("synthetic") == true || part.Bool("ignored") == true;

    static CanonicalEvent Event(IMessage payload, Guid id, DateTimeOffset at, string? recordTs) =>
        new(CanonicalEventTypes.Of(payload), payload, id, at, recordTs);
}
