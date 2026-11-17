using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>Whether the session an MCP server runs in is outside the profile's capture scope, judged once at server start
/// by the hook's rules against the harness's project directory. Out of scope, the server never receives the session, so
/// the write-side servers drop the instructions that would have the agent write against it; the tools stay registered.
/// The setting therefore applies from the next session start.</summary>
sealed class McpCaptureScope(
        ConfigRoot config, ProfileContext profiles, GitProviderRouter router, UserHome home, WorkingDirectory workdir, TimeProvider time) {
    public Task<bool> IsOutOfScopeAsync() => IsOutOfScopeAsync(HarnessRequesterContext.ProjectDir() ?? workdir.Path);

    internal Task<bool> IsOutOfScopeAsync(string cwd) =>
        SessionScope.IsOutOfScopeAsync(router, config, home, profiles.Effective, $"{{\"cwd\":\"{JsonEncodedText.Encode(cwd)}\"}}", time, budget: null);
}
