namespace Capacitor.Cli.Core;

/// A fact about a subagent or background command that a vendor's rules read off one envelope,
/// carried beside the rows rather than on the wire envelope. Vendor-neutral: which tool names
/// start a run stays in the rules.
public abstract record RunSignal {
    /// A tool call that may run on. A provisional start makes no row until a Detached for its
    /// call says the run went to the background.
    public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At, RunKind Kind = RunKind.Agent, bool Provisional = false) : RunSignal;

    /// The call's result was only a launch acknowledgement; the id is the run's handle from then
    /// on.
    public sealed record Detached(string CallId, string AgentId) : RunSignal;

    /// The subagent's id, which its own transcript is filed under. A null call id names the oldest
    /// subagent still unnamed: the source knows the agent but not the call that started it.
    public sealed record Identified(string? CallId, string AgentId) : RunSignal;

    /// A message started a stopped subagent again under its id: a second run, whose handle is the
    /// message's call from then on.
    public sealed record Resumed(string CallId, string AgentId, DateTimeOffset At) : RunSignal;

    /// The run stopped with background work of its own still going and resumes when that reports,
    /// so it has not ended. At least one key is set.
    public sealed record Waiting(string? CallId, string? AgentId, DateTimeOffset At) : RunSignal;

    /// A stop record for an agent whose runs the transcript starts, naming neither which run stopped
    /// nor how. A Claude agent also stops each time it pauses on background work of its own.
    public sealed record Stopped(string AgentId, DateTimeOffset At) : RunSignal;

    /// The vendor's process came back onto this session, so nothing started before At is still going,
    /// and what was may never say so: a Claude agent paused on its own work gets no notification.
    public sealed record Restarted(DateTimeOffset At) : RunSignal;

    /// The vendor's process came back onto this session, known only from a record the new process wrote
    /// at At, possibly after starting runs of its own. A run paused before At was the old process's, and
    /// nothing is left to resume it.
    public sealed record Reopened(DateTimeOffset At) : RunSignal;

    /// The run ended outside its tool result. At least one key is set. A null outcome means the
    /// source knows only that it ended.
    public sealed record Finished(string? CallId, string? AgentId, RunOutcome? Outcome, DateTimeOffset At) : RunSignal;
}
