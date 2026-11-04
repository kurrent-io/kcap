using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// Whether a Vibe hook belongs to the session a daemon hosts over ACP, which the daemon already records from
/// its ACP stream. <c>vibe-acp</c> runs kcap's hooks too, under the same session id its ACP session carries,
/// so the hook asks the daemon which session the hosting agent is bound to. The process tree cannot answer
/// it: between the hook and <c>vibe-acp</c> sit a shell, the npm launcher and Python's, as many as the
/// installs make. A <c>vibe</c> the hosted agent starts itself has a session of its own and keeps recording.
/// </summary>
static class MistralVibeHostedSession {
    public static bool IsDaemonHosted(string sessionId) =>
        IsDaemonHosted(sessionId, Environment.GetEnvironmentVariable, HostedAgentSessions.Daemon.For);

    internal static bool IsDaemonHosted(
            string                sessionId,
            Func<string, string?> getEnv,
            Func<string, string?> hostedAgentSession
        ) =>
        getEnv(HostedAgent.DaemonAgentIdVar) is { Length: > 0 } agentId
     && SessionIds.Canonical(hostedAgentSession(agentId)) is { } hostedSession
     && hostedSession == SessionIds.Canonical(sessionId);
}
