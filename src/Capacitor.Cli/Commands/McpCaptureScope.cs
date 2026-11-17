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

        // The hook marks the id its payload carries, which for Claude is the vendor's spelling, not the canonical one.
        string?[] sessionIds = [harness.SessionId, Environment.GetEnvironmentVariable(HarnessRequesterContext.ClaudeSessionIdVar)];

        return IsOutOfScopeAsync(harness.ProjectDir ?? workdir.Path, sessionIds);
    }

    internal Task<bool> IsOutOfScopeAsync(string cwd, params string?[] sessionIds) =>
        sessionIds.Any(id => !string.IsNullOrWhiteSpace(id) && DisabledSessions.IsDisabled(id, config))
            ? Task.FromResult(true)
            : SessionScope.IsOutOfScopeAsync(router, config, home, profiles.Effective, $"{{\"cwd\":\"{JsonEncodedText.Encode(cwd)}\"}}", time, budget: null);
}
