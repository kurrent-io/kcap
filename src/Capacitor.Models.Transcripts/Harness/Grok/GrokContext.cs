namespace Capacitor.Models.Transcripts.Harness.Grok;

/// The turn a stream is in, which the turn's usage reads when it completes. A turn can span batches, so
/// nothing is cleared at a boundary. A caller that resumes a stream mid-turn seeds the turn fields from what
/// it stored, or that turn's usage carries no occupancy and no target.
public sealed class GrokContext : TranscriptContext {
    internal GrokContext(bool isSubagent) => IsSubagent = isSubagent;

    /// A subagent's usage is already inside its parent's turn usage, so a subagent stream records none.
    public bool IsSubagent { get; }

    /// The prompt id of the turn the fields below belong to; a line naming another one starts them over.
    public string? TurnPromptId { get; set; }

    /// The turn's largest request context. A turn's usage sums every model call in it, so this, not that
    /// sum, is the turn's peak occupancy.
    public long TurnPeakTokens { get; set; }

    /// The event the turn's usage targets, so a reader attaches the usage to that turn.
    public GrokTurnAnchor? TurnAnchor { get; set; }
}
