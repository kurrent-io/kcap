namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// Per-stream state: the ids a stream's events are scoped by, and the latest prompt's time.
public sealed class KiroContext(string sessionId, string idScope) : TranscriptContext {
    /// Names every message, tool call and result, a subagent stream's included.
    public string SessionId { get; } = sessionId;

    /// Names a usage backfill: the session id, or <c>{sessionId}:{agentId}</c> on a subagent stream.
    public string IdScope { get; } = idScope;

    /// Kiro times prompts only, so a turn's replies and results take this; it spans batches. A caller that
    /// starts projecting a stream after its last prompt seeds it, or those events get the earliest instant.
    public DateTimeOffset? LastPromptAt { get; set; }
}
