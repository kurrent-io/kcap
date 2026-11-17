using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>Whether the session an MCP server runs in is outside the profile's capture scope, judged once at server start.
/// Out of scope, the server never receives the session, so the write-side servers drop the instructions that would have
/// the agent write against it; the tools stay registered. The setting therefore applies from the next session start.
///
/// <para>The hook's own verdict wins when it is already on disk: its repo probe runs under the hook's budget, so a
/// probe that timed out there excluded the session even if this unbounded one would resolve it. A server that starts
/// before the hook has written it falls back to the same rule against the harness's project directory.</para></summary>
sealed class McpCaptureScope(
        ConfigRoot config, ProfileContext profiles, GitProviderRouter router, UserHome home, WorkingDirectory workdir, TimeProvider time) {
    public Task<bool> IsOutOfScopeAsync() {
        // Never the hosting daemon: a Pi-hosted agent's session is bound only after its servers answer the handshake.
        var harness = HarnessRequesterContext.Resolve(
            Environment.GetEnvironmentVariable, Directory.Exists, ProcessHelpers.NearestCodingAgent, ProcessHelpers.GetProcessCwd,
            hostedAgentSession: _ => null);

        return IsOutOfScopeAsync(
            harness.ProjectDir ?? workdir.Path,
            MarkerIds(harness.SessionId, Environment.GetEnvironmentVariable(HarnessRequesterContext.ClaudeSessionIdVar)));
    }

    /// <summary>The ids the hook may have marked for this session. The Claude hook marks its payload's spelling, not the
    /// canonical one, but that spelling is this session's only when the resolver attributed the session to it: an id
    /// inherited by a harness nested inside Claude belongs to the outer session.</summary>
    internal static string[] MarkerIds(string? resolvedSessionId, string? claudeSessionId) =>
        resolvedSessionId is null ? []
        : claudeSessionId is not null && claudeSessionId != resolvedSessionId && SessionIds.Canonical(claudeSessionId) == resolvedSessionId
            ? [resolvedSessionId, claudeSessionId]
            : [resolvedSessionId];

    internal Task<bool> IsOutOfScopeAsync(string cwd, params string[] sessionIds) =>
        sessionIds.Any(id => DisabledSessions.IsDisabled(id, config))
            ? Task.FromResult(true)
            : SessionScope.IsOutOfScopeAsync(router, config, home, profiles.Effective, $"{{\"cwd\":\"{JsonEncodedText.Encode(cwd)}\"}}", time, budget: null);
}
