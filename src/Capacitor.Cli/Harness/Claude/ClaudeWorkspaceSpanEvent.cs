using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>A Claude hook that moves a workspace span: a prompt opens one, the turn's end closes them,
/// and <c>EnterWorktree</c> opens one in the tree the turn moved into. <see cref="Dir"/> is null for a close.</summary>
sealed record ClaudeWorkspaceSpanEvent(string Command, string SessionId, string? Dir, WorkspaceOpen? Open) {
    public const string Flag = "--workspace-span";

    /// <summary>Null for any other hook, a subagent's, or one without a session id. The session id comes
    /// back dashless. The <c>EnterWorktree</c> destination is read from the tool's response, never from
    /// <c>cwd</c>, which can still name the tree the turn left; with no destination nothing opens.</summary>
    public static ClaudeWorkspaceSpanEvent? Parse(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { return null; }
        if (hook is null || hook["agent_id"] is not null) return null;

        if (Text(hook["session_id"])?.Replace("-", "") is not { Length: > 0 } sessionId) return null;

        return Text(hook["hook_event_name"]) switch {
            "UserPromptSubmit" when Text(hook["cwd"]) is { Length: > 0 } cwd => new("user-prompt-submit", sessionId, cwd, WorkspaceOpen.Turn),
            "Stop" => new("stop", sessionId, null, null),
            "PostToolUse" when Text(hook["tool_name"]) == "EnterWorktree"
                            && Text(hook["tool_response"]?["worktreePath"]) is { Length: > 0 } destination
                => new("post-tool-use", sessionId, destination, WorkspaceOpen.Join),
            _ => null,
        };
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
