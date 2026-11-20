using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The session-start and session-end bodies a routed import posts in place of the live hooks. Property order is
/// part of the shape: a re-import posts the same bytes the first import did.
/// </summary>
internal static class SyntheticHookPayloads {
    /// <param name="hookEventName">The event name the vendor's live hook sends, which its route binds.</param>
    /// <param name="source">The <c>source</c> field, left off when null.</param>
    /// <param name="model">The <c>model</c> field, left off when null.</param>
    public static JsonObject SessionStart(
            string hookEventName, string sessionId, string? cwd, DateTimeOffset? startedAt,
            string? source = "startup", string? model = null) {
        var payload = new JsonObject {
            ["hook_event_name"] = hookEventName,
            ["session_id"]      = sessionId,
        };
        if (source is not null) payload["source"] = source;
        // The two fields the live hook forwards, so an imported session attributes to a repository as a captured one does.
        if (cwd is not null) {
            payload["cwd"] = cwd;
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        }
        if (model is not null) payload["model"] = model;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    public static JsonObject SessionEnd(string hookEventName, string sessionId, string reason, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = hookEventName,
            ["session_id"]      = sessionId,
            ["reason"]          = reason,
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (endedAt is { } ts) payload["ended_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }
}
