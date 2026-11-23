namespace Capacitor.Models.Transcripts.Harness.GrokBot;

/// Per-stream state: the scope every entry's id is derived under.
public sealed class GrokBotContext(string idScope) : TranscriptContext {
    /// The session id, or <c>{sessionId}:{agentId}</c> on a subagent stream.
    public string IdScope { get; } = idScope;
}
