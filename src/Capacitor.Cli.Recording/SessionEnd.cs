using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>Whether a recorder that stops posts its session's end, and the end it posts. <c>kcap watch</c> and the daemon
/// decide with the same rules.</summary>
public static class SessionEnd {
    /// <summary>The session-end reason a recorder posts for how it came to stop, or null when it posts none.</summary>
    public static string? DecideEndReason(bool parentExited, bool crewFinished, bool wedgedCeiling, bool idle, bool copilotShutdown) =>
        parentExited    ? "parent_exited"
      : crewFinished    ? "crew_finished"
      : wedgedCeiling   ? "parent_dead_ceiling"
      : idle            ? "idle_timeout"
      : copilotShutdown ? "session_shutdown"
      :                   null;

    /// <summary>
    /// Whether the recorder posts the session's end itself: only a session's own source does, never a subagent's.
    /// Whether the session had a real turn does not enter into it: the start hook registered the session before the
    /// recorder read a line, so a turnless one left without its end stays Live for good.
    /// </summary>
    public static bool PostsOwnSessionEnd([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? endReason, string? agentId, bool cursorSuppressesEndPost) =>
        endReason is not null && agentId is null && !cursorSuppressesEndPost;

    public static SessionEndAction DecideSessionEnd(bool runsOnElsewhere, HostedAgent? hosted, Func<HostedAgent, bool> resuming) =>
        runsOnElsewhere                                     ? SessionEndAction.RunsOnElsewhere
      : hosted is { AgentId: not null } && resuming(hosted) ? SessionEndAction.HeldForResume
      :                                                       SessionEndAction.Post;

    /// <summary>The end a recorder posts for its own stop. A watcher inherits the environment of the agent whose hook
    /// spawned it, so a hosted agent's end names that agent and its daemon epoch.</summary>
    public static JsonObject ParentExitEndBody(
            string sessionId, string transcriptPath, string? cwd, string reason, DateTimeOffset endedAt, HostedAgent hosted) {
        var end = new JsonObject {
            ["session_id"]      = sessionId,
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = cwd ?? "",
            ["hook_event_name"] = "session_end",
            ["reason"]          = reason,
            // Scopes the server's SessionEnded id to this run: a vendor with no session-end hook ends only
            // here, and a resumed session's second exit would otherwise dedupe against its first.
            ["ended_at"]        = endedAt.ToString("O"),
        };
        SessionEndIncarnation.Stamp(end, hosted);

        return end;
    }
}
