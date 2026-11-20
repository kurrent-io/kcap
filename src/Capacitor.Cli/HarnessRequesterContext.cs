using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Answers "which session am I serving, and where is it working?" for a long-lived MCP server, from
/// what the running harness told this process rather than its inherited environment.
/// <para>An MCP server is spawned once from the harness's launching environment. When one session
/// launches another, the child's servers inherit the parent's <c>KCAP_SESSION_ID</c> (the hook writes
/// it only into the shell-env file) and, without a pinned <c>cwd</c>, the parent's checkout.</para>
/// <para>Claude Code sets <c>CLAUDE_CODE_SESSION_ID</c> and <c>CLAUDE_PROJECT_DIR</c> to its own values
/// in every child, so they win when present. A different harness launched inside Claude Code inherits
/// them unchanged, so a co-present signal from another harness makes them unprovable and resolution
/// falls back to the ambient signals.</para>
/// <para>A daemon-hosted agent with no session of its own carries <c>KCAP_DAEMON_AGENT_ID</c>, and its
/// daemon is asked which session it is bound to.</para>
/// </summary>
static class HarnessRequesterContext {
    /// <summary>Set by the running Claude Code process in every child process's environment.</summary>
    internal const string ClaudeSessionIdVar = "CLAUDE_CODE_SESSION_ID";

    /// <summary>The running Claude Code session's project directory, set alongside the id above.</summary>
    internal const string ClaudeProjectDirVar = "CLAUDE_PROJECT_DIR";

    /// <summary>Another harness's own-session signal. Its presence alongside the Claude variables
    /// means one harness is nested inside the other and neither is provably ours.</summary>
    internal const string CodexThreadIdVar = "CODEX_THREAD_ID";

    /// <summary>Set by Grok on every process it starts, its MCP servers included, to its own session.
    /// It is passed through to anything those processes start too, so it is trusted only when Grok is
    /// the nearest harness above this process.</summary>
    internal const string GrokSessionIdVar = "GROK_SESSION_ID";

    internal static string? GrokSessionIn(Func<string, string?> getEnv) =>
        getEnv(GrokSessionIdVar) is { } id && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <param name="SessionId">
    /// The requesting session in the canonical form the server files it under (a GUID as its
    /// 32-hex form, an opaque vendor id unchanged). Null when no harness signal is available.
    /// </param>
    /// <param name="ProjectDir">
    /// The running harness's project directory, or null when the harness did not report one (or
    /// reported one that no longer exists). Null means "no better answer than the process cwd".
    /// </param>
    internal readonly record struct Resolved(string? SessionId, string? ProjectDir);

    internal static Resolved Resolve() => Resolve(Environment.GetEnvironmentVariable, Directory.Exists);

    /// <summary>The running harness's project directory, without asking a hosting daemon. A server reads
    /// it while starting, and Pi binds a hosted agent's session only after that agent's servers answer
    /// their handshake: asking the daemon then waits on the server itself.</summary>
    internal static string? ProjectDir() =>
        Resolve(
            Environment.GetEnvironmentVariable, Directory.Exists, ProcessHelpers.NearestCodingAgent, ProcessHelpers.GetProcessCwd,
            hostedAgentSession: _ => null).ProjectDir;

    internal static Resolved Resolve(Func<string, string?> getEnv, Func<string, bool> directoryExists) =>
        Resolve(getEnv, directoryExists, ProcessHelpers.NearestCodingAgent, ProcessHelpers.GetProcessCwd, HostedAgentSessions.Daemon.For);

    /// <param name="nearestAgent">The nearest coding agent above this process, asked only when a Grok
    /// session is in the environment.</param>
    /// <param name="processCwd">A process's working directory, which for Grok is its project.</param>
    /// <param name="hostedAgentSession">The session the hosting daemon has bound an agent id to, null
    /// when it has none or cannot be asked. Asked only when no harness names its own session.</param>
    internal static Resolved Resolve(
            Func<string, string?> getEnv, Func<string, bool> directoryExists,
            Func<CodingAgentAncestor?> nearestAgent, Func<int, string?> processCwd,
            Func<string, string?> hostedAgentSession) {
        if (GrokSessionIn(getEnv) is { } grokSessionId) {
            switch (nearestAgent()) {
                // Whose the inherited ids are cannot be told, so none of them is claimed.
                case null:
                    return new(SessionId: null, ProjectDir: null);
                case { Vendor: "grok" } grok:
                    var cwd = processCwd(grok.Pid);
                    return new(SessionIds.Canonical(grokSessionId), cwd is { Length: > 0 } && directoryExists(cwd) ? cwd : null);
            }
        }

        var harnessSessionId = getEnv(ClaudeSessionIdVar);
        var nestedHarness    = getEnv(CodexThreadIdVar);

        // No usable per-process harness evidence — either none was reported, or another harness's
        // own-session signal is co-present so ours is unprovable (see the nesting note above). No
        // project directory is claimed, so the caller keeps using the process cwd.
        if (string.IsNullOrWhiteSpace(harnessSessionId) || !string.IsNullOrWhiteSpace(nestedHarness)) {
            // A hosted agent's session outranks a KCAP_SESSION_ID inherited from whatever shell started
            // its daemon. A Codex thread id is that harness's own session, so it outranks the daemon.
            var hosted = string.IsNullOrWhiteSpace(nestedHarness) && getEnv(HostedAgent.DaemonAgentIdVar) is { Length: > 0 } agentId
                ? SessionIds.Canonical(hostedAgentSession(agentId))
                : null;

            return new(hosted ?? ArgParsing.ResolveSessionIdFromEnv(getEnv), ProjectDir: null);
        }

        // A project dir is only usable if it still resolves to a directory — a value pointing at a
        // removed worktree must degrade to the process cwd rather than send the server a path
        // nothing can be checked out at.
        var projectDir = getEnv(ClaudeProjectDirVar)?.Trim();
        var usableProjectDir = !string.IsNullOrEmpty(projectDir) && directoryExists(projectDir)
            ? projectDir
            : null;

        return new(SessionIds.Canonical(harnessSessionId), usableProjectDir);
    }
}
