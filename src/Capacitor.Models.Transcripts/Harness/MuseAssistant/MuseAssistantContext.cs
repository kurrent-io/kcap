namespace Capacitor.Models.Transcripts.Harness.MuseAssistant;

/// Per-stream state: the scope every item's id is derived under.
public sealed class MuseAssistantContext(string idScope) : TranscriptContext {
    /// The session id, or <c>{sessionId}:{agentId}</c> on a subagent stream.
    public string IdScope { get; } = idScope;
}
