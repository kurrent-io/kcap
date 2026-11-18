using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// <summary>
/// Projects Cursor's agent transcript (<c>~/.cursor/projects/{workspace}/agent-transcripts/{id}/{id}.jsonl</c>)
/// into canonical events. A line is <c>{"role":…,"message":{"content":[…]}}</c> with Anthropic-style blocks:
/// <list type="bullet">
///   <item><c>user</c>: one result per <c>tool_result</c> block, and one user message from the first non-empty
///   <c>text</c> block, its query unwrapped from Cursor's envelope; in block order.</item>
///   <item><c>assistant</c>: one event per non-empty <c>text</c> block and per <c>tool_use</c>, in block order.</item>
/// </list>
/// Thinking blocks, other roles and records without content emit nothing: the session's start and end and the
/// model's reasoning reach the server from Cursor's hooks, not from this transcript.
/// </summary>
public sealed class CursorTranscriptEvents : ITranscriptProjection {
    public static readonly CursorTranscriptEvents Instance = new();

    const string Source = "cursor-live";

    CursorTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new CursorContext(sessionId, agentId is { Length: > 0 } ? $"{sessionId}:{agentId}" : sessionId);

    /// The transcript times nothing but a prompt Cursor wrapped with its send time, so every other event takes the
    /// receive time.
    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (CursorContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("role") is not { } role || root.Obj("message")?.Arr("content") is not { } content || content.GetArrayLength() == 0)
                return ProjectionResult.Empty;

            if (ctx.SessionId.Length == 0) return ProjectionResult.Reject("no session id to scope the line's ids");

            var at = new LineAt(ctx, lineNumber, receivedAt);
            return role switch {
                "user"      => User(content, at),
                "assistant" => Assistant(content, at),
                _           => ProjectionResult.Empty,
            };
        }
    }

    static ProjectionResult User(JsonElement content, LineAt at) {
        var events      = new List<CanonicalEvent>();
        var textEmitted = false;

        for (var i = 0; i < content.GetArrayLength(); i++) {
            var block = content[i];

            switch (block.Str("type")) {
                case "tool_result": {
                    var result = block.Prop("content") is { } raw ? raw.IsString ? raw.GetString()! : raw.GetRawText() : "";
                    var ts     = Timestamp.FromDateTimeOffset(at.Received);

                    var received = new ToolResultReceived {
                        CallId = block.Str("tool_use_id") ?? "", Result = result, MessageId = at.MessageId, CreatedAt = ts, Timestamp = ts,
                    };
                    events.Add(at.Event(received, "tool_result", i, at.Received));
                    break;
                }
                // Guarded on the raw text, not the query: an attachment-only prompt unwraps to an empty query and
                // still has a turn to pair with.
                case "text" when !textEmitted && block.Str("text") is { Length: > 0 } text: {
                    var parsed = CursorUserQuery.Parse(text);
                    var sentAt = parsed.Timestamp ?? at.Received;
                    var ts     = Timestamp.FromDateTimeOffset(sentAt);

                    var message = new UserMessageReceived { Content = parsed.Query, MessageId = at.MessageId, CreatedAt = ts, Timestamp = ts };
                    if (at.Context.TakeTurn?.Invoke(parsed.Query) is { } turn) message.Extensions[CursorExtension.Slug] = CursorExtension.Turn(turn);

                    events.Add(at.Event(message, "user", i, sentAt));
                    textEmitted = true;
                    break;
                }
            }
        }

        return ProjectionResult.Of(events);
    }

    static ProjectionResult Assistant(JsonElement content, LineAt at) {
        var events = new List<CanonicalEvent>();
        var ts     = Timestamp.FromDateTimeOffset(at.Received);

        for (var i = 0; i < content.GetArrayLength(); i++) {
            var block = content[i];

            switch (block.Str("type")) {
                case "text" when block.Str("text") is { Length: > 0 } text:
                    events.Add(at.Event(new AssistantTextGenerated { Content = text, MessageId = at.MessageId, CreatedAt = ts, Timestamp = ts }, "assistant_text", i, at.Received));
                    break;
                case "tool_use": {
                    var name = block.Str("name") ?? "";
                    // The paired result names the block's own id; Cursor writes none on most calls, and then the
                    // block's place stands in.
                    var callId = block.Str("id") is { Length: > 0 } id ? id : string.Create(CultureInfo.InvariantCulture, $"line:{at.LineNumber}:{i}");
                    var call   = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = CursorToolKinds.Of(name) };
                    if (block.Prop("input") is { } input && Arguments(input) is { } arguments) call.Arguments = arguments;

                    var calls = new AssistantToolCallsGenerated { MessageId = at.MessageId, CreatedAt = ts, Timestamp = ts };
                    calls.ToolCalls.Add(call);
                    events.Add(at.Event(calls, "tool_use", i, at.Received));
                    break;
                }
            }
        }

        return ProjectionResult.Of(events);
    }

    /// An input that is not an object leaves the call without arguments rather than wrapped.
    static Struct? Arguments(JsonElement input) {
        try {
            return Struct.Parser.ParseJson(input.GetRawText());
        } catch (InvalidJsonException) {
            return null;
        } catch (InvalidProtocolBufferException) {
            return null;
        }
    }

    /// What every event of one line shares: its stream, its number, its receive time and the metadata the server
    /// stores beside each of them.
    sealed class LineAt(CursorContext context, int lineNumber, DateTimeOffset received) {
        public CursorContext  Context    { get; } = context;
        public int            LineNumber { get; } = lineNumber;
        public DateTimeOffset Received   { get; } = received;
        public string         MessageId  { get; } = string.Create(CultureInfo.InvariantCulture, $"line:{lineNumber}");

        readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal) {
            ["$source"] = Source,
            ["line"]    = lineNumber.ToString(CultureInfo.InvariantCulture),
        };

        public CanonicalEvent Event(IMessage payload, string kind, int blockIndex, DateTimeOffset at) {
            var type = CanonicalEventTypes.Of(payload);
            return new(type, payload, TranscriptIds.CursorBlock(Context.IdScope, LineNumber, kind, blockIndex, type), at) { VendorMetadata = _metadata };
        }
    }
}
