namespace Capacitor.App.ViewModels;

/// One session status. Earlier values outrank later ones: a row resolves to exactly one of these.
public enum AgentStatusKind {
    Failed,
    DaemonDown,
    Answer,
    NeedsYou,
    Interrupted,
    Starting,
    Resuming,
    Working,
    Idle,
    Done,
    Other,
}

public static class AgentStatusKindExtensions {
    /// The agent is on its way to an answer: launching, resuming, or mid-turn.
    public static bool IsProgressing(this AgentStatusKind kind) =>
        kind is AgentStatusKind.Working or AgentStatusKind.Starting or AgentStatusKind.Resuming;

    /// The agent has stopped and the next move is the user's.
    public static bool WaitsOnUser(this AgentStatusKind kind) =>
        kind is AgentStatusKind.Idle or AgentStatusKind.NeedsYou or AgentStatusKind.Answer;
}
