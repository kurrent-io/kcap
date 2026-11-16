using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Capacitor.Models.Transcripts.Harness.Muse;
using Google.Protobuf;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// Reads a persisted payload back into its message or record, and writes one in the form it is
/// persisted in. Unknown fields are ignored on read: a newer server may add one, and the client
/// must still read the event.
public static class CanonicalEventJson {
    static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    /// The stored JSON of a payload, with the snake_case names the server persists. `UsageApplied` is
    /// never stored as itself, so it has no form.
    public static string Write(object payload) {
        _ = CanonicalEventTypes.Of(payload);
        return payload switch {
            IMessage message                  => Formatter.Format(message),
            ContextCompactedEvent p           => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.ContextCompactedEvent),
            UserMessageRetractedEvent p       => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.UserMessageRetractedEvent),
            CodexUsageBackfilledEvent p       => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.CodexUsageBackfilledEvent),
            KiroUsageBackfill p               => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.KiroUsageBackfill),
            MuseUsageBackfill p               => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.MuseUsageBackfill),
            AntigravityUsageBackfill p        => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.AntigravityUsageBackfill),
            CopilotUsageBackfill p            => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.CopilotUsageBackfill),
            CopilotSessionMetrics p           => JsonSerializer.Serialize(p, TranscriptEventJsonContext.Default.CopilotSessionMetrics),
            _ => throw new ArgumentException($"{payload.GetType().Name} is never stored as itself", nameof(payload)),
        };
    }

    /// A usage in the same form as a payload; it is not an event, so <see cref="Write"/> refuses it.
    public static string WriteUsage(TokenUsage usage) => Formatter.Format(usage);

    /// Null for a type the chat does not read or a payload that does not parse as it.
    public static object? TryParse(string eventType, string json) {
        try {
            return eventType switch {
                CanonicalEventTypes.UserMessageReceived         => Parser.Parse<UserMessageReceived>(json),
                CanonicalEventTypes.AssistantTextGenerated      => Parser.Parse<AssistantTextGenerated>(json),
                CanonicalEventTypes.AssistantThinkingGenerated  => Parser.Parse<AssistantThinkingGenerated>(json),
                CanonicalEventTypes.AssistantToolCallsGenerated => Parser.Parse<AssistantToolCallsGenerated>(json),
                CanonicalEventTypes.ToolResultReceived          => Parser.Parse<ToolResultReceived>(json),
                CanonicalEventTypes.SubagentCompleted           => Parser.Parse<SubagentCompleted>(json),
                CanonicalEventTypes.SubagentStarted             => Parser.Parse<SubagentStarted>(json),
                CanonicalEventTypes.SessionStarted              => Parser.Parse<SessionStarted>(json),
                CanonicalEventTypes.InterruptIssued             => Parser.Parse<InterruptIssued>(json),
                CanonicalEventTypes.ContextCompacted            => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.ContextCompactedEvent),
                CanonicalEventTypes.UserMessageRetracted        => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.UserMessageRetractedEvent),
                CanonicalEventTypes.CodexUsageBackfilled        => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.CodexUsageBackfilledEvent),
                CanonicalEventTypes.KiroUsageBackfilled         => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.KiroUsageBackfill),
                CanonicalEventTypes.MuseUsageBackfilled         => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.MuseUsageBackfill),
                CanonicalEventTypes.AntigravityUsageBackfilled  => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.AntigravityUsageBackfill),
                CanonicalEventTypes.CopilotUsageBackfilled      => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.CopilotUsageBackfill),
                CanonicalEventTypes.CopilotSessionMetrics       => JsonSerializer.Deserialize(json, TranscriptEventJsonContext.Default.CopilotSessionMetrics),
                _ => null,
            };
        } catch (JsonException) {
            return null;
        } catch (InvalidJsonException) {
            return null;
        } catch (InvalidProtocolBufferException) {
            return null;
        }
    }
}
