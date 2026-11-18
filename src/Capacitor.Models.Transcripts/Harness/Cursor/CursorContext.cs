namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// Per-stream state: the ids a stream's events are scoped by, and where a prompt's hook-reported turn comes from.
public sealed class CursorContext(string sessionId, string idScope) : TranscriptContext {
    public string SessionId { get; } = sessionId;

    /// Every event id hashes it: the session id, or <c>{sessionId}:{agentId}</c> on a subagent stream, whose line
    /// numbers restart at zero.
    public string IdScope { get; } = idScope;

    /// Called once per user message with its unwrapped query, which is the text the <c>beforeSubmitPrompt</c> hook
    /// saw. The caller owns what the hooks queued and decides what a miss does to it; null, or a null answer,
    /// leaves the message without a <c>cursor</c> slug.
    public Func<string, CursorTurn?>? TakeTurn { get; set; }
}
