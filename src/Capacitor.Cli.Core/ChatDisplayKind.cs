namespace Capacitor.Cli.Core;

/// Chat-display marks carried on an envelope's ToolKind. Distinct from AcpToolKind, which
/// classifies a tool call.
public static class ChatDisplayKind {
    public const string Shell = "shell";

    /// A subagent's final report handed back to the session that delegated to it.
    public const string AgentReport = "agent_report";

    /// Any other message one agent sent another inside the same session.
    public const string AgentMessage = "agent_message";

    /// The harness's record that a background subagent or command finished.
    public const string TaskNotification = "task_notification";

    /// The summary the harness carries into a compacted conversation.
    public const string CompactSummary = "compact_summary";

    /// Work something outside the session asked for: a CI event, another session, a scheduled loop.
    /// The row's label rides in ToolName, which a system note does not otherwise use.
    public const string Trigger = "trigger";

    /// The harness's marker that the person stopped the agent mid-turn.
    public const string Interrupted = "interrupted";

    /// A user message the server recorded from a queued input the agent never received.
    public const string Undelivered = "undelivered";
}
