namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// What a conversation carries between lines, all of it for the stream's life: a tool's result is a step of its
/// own and routinely lands in another flush than the planner step that called it. None of it depends on what the
/// caller stored, so a line delivered again projects as it did the first time: a call is queued once, a result
/// keeps the call it was paired with, a planner emits again the held results it claimed, and a usage row keeps the
/// time it was first given.
public sealed class AntigravityContext(string idScope) : TranscriptContext {
    /// Names a usage row: the session id, or <c>{sessionId}:{agentId}</c> on a subagent stream.
    public string IdScope { get; } = idScope;

    /// The last <c>created_at</c> a step carried, which a usage line written without one takes.
    internal string? LastCreatedAt { get; set; }

    /// The time each usage row was first given, by its event id.
    internal Dictionary<Guid, string?> UsageTimes { get; } = [];

    /// Calls waiting for their result step, oldest first. Only the latest planner's calls wait: a planner drops an
    /// earlier one's, whose result never came.
    internal List<string> PendingCallIds { get; } = [];

    /// The <c>step_index</c> of the planner that made each queued call.
    internal Dictionary<string, long> CallSteps { get; } = new(StringComparer.Ordinal);

    /// The <c>step_index</c> of every planner and prompt: a call is answerable only while none lies between it and
    /// the result.
    internal SortedSet<long> BoundarySteps { get; } = [];

    internal HashSet<string> QueuedCallIds { get; } = new(StringComparer.Ordinal);

    /// The call each result was paired with, by the result's event id.
    internal Dictionary<Guid, string> PairedCallIds { get; } = [];

    /// Results projected before any call was waiting, until a planner claims them.
    internal List<AntigravityHeldResult> Held { get; set; } = [];

    /// Every result ever held: its event rides the line of the planner that claims it, never its own.
    internal HashSet<Guid> HeldResultIds { get; } = [];

    /// The held results each planner claimed, by the planner's id, in the order it emitted them.
    internal Dictionary<Guid, List<AntigravityHeldResult>> Claimed { get; } = [];
}
