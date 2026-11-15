using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Harness.MuseAssistant;

/// <summary>
/// Projects a Muse Assistant chat into canonical events. <c>kcap muse-assistant ingest</c> posts one
/// <c>chat.read_messages</c> item per line, with <c>chat_id</c> and <c>turn_id</c> added. An item with content
/// becomes one event by its role: <c>user</c> a prompt, <c>assistant</c> a reply, <c>commentary</c> (Muse
/// narrating its own work) a thought. Any other role, <c>developer</c> included, yields nothing.
/// </summary>
public sealed class MuseAssistantTranscriptEvents : ITranscriptProjection {
    public static readonly MuseAssistantTranscriptEvents Instance = new();

    static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    MuseAssistantTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new MuseAssistantContext(agentId is { Length: > 0 } ? $"{sessionId}:{agentId}" : sessionId);

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (MuseAssistantContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var item = doc.RootElement;
            if (!item.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (ctx.IdScope.Length == 0
             || item.Str("turn_id") is not { Length: > 0 } turnId
             || item.Num("seq") is not { } seq
             || item.Str("content") is not { Length: > 0 } content)
                return ProjectionResult.Empty;

            // An instant out of range costs the item its own time, never the item itself.
            var ms    = item.Num("occurred_at_ms") is { } n && n >= MinUnixMs && n <= MaxUnixMs ? n : (long?)null;
            var at    = ms is { } own ? DateTimeOffset.FromUnixTimeMilliseconds(own) : receivedAt;
            var stamp = Timestamp.FromDateTimeOffset(at);

            IMessage? payload = item.Str("role") switch {
                "user"       => new UserMessageReceived { Content = content, Timestamp = stamp },
                "assistant"  => new AssistantTextGenerated { Content = content, Timestamp = stamp },
                "commentary" => new AssistantThinkingGenerated { Content = content, Timestamp = stamp },
                _            => null,
            };
            if (payload is null) return ProjectionResult.Empty;

            var id = TranscriptIds.MuseAssistantItem(ctx.IdScope, turnId, seq);
            return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, at, ms is null ? null : at.ToString("O"))]);
        }
    }
}
