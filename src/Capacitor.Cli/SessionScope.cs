using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli;

/// <summary>Whether a session is outside the profile's capture scope: <see cref="RepoExclusion"/>, then the path lists
/// against the payload's cwd. The Claude hook gates capture on it and the MCP servers their write-side instructions, so
/// the two agree on which sessions the server receives.</summary>
static class SessionScope {
    public static async Task<bool> IsOutOfScopeAsync(
            GitProviderRouter router, ConfigRoot config, UserHome home, Profile? profile, string body, TimeProvider time, TimeSpan? budget) {
        if (await RepoExclusion.IsOutOfScopeAsync(router, config, body, profile?.AllowedRepos, profile?.ExcludedRepos, time, budget))
            return true;

        if (profile?.AllowedPaths is not { Length: > 0 } && profile?.ExcludedPaths is not { Length: > 0 }) return false;

        string? cwd;

        try {
            cwd = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();
        } catch {
            // A body we cannot read yields no cwd rather than a verdict: a denylist shrugs at
            // that, an allowlist does not admit it. Deciding here would hand both the same answer.
            cwd = null;
        }

        return PathExclusion.IsOutOfScope(cwd, profile?.AllowedPaths, profile?.ExcludedPaths, home);
    }
}
