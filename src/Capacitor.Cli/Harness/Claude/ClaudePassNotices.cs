using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.Claude;

/// <summary><c>kcap hook --claude --pass-notices</c>: on each <c>UserPromptSubmit</c> of a session whose start
/// was told the server delivers there, hands the agent the pass notices waiting for it.</summary>
static class ClaudePassNotices {
    public const string Flag = "--pass-notices";

    /// <summary>On the plugin's SessionStart command, shipped in the same manifest as the <see cref="Flag"/> entry.</summary>
    public const string CapableFlag = "--pass-notices-capable";

    /// <summary>The capability declared on the SessionStart request, echoed by an ack whose server takes
    /// prompt-time claims for the session, and sent on each prompt's request.</summary>
    internal const string CapabilityVersion = "v1";

    /// <summary>The prompt waits on this hook, so the server gets a second at most.</summary>
    internal static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The dashless session id of a <c>UserPromptSubmit</c>, or null for anything else.</summary>
    public static string? SessionIdOf(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch { return null; }
        if (hook is null || Text(hook["hook_event_name"]) != "UserPromptSubmit") return null;

        var sessionId = Text(hook["session_id"])?.Replace("-", "");
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    /// <summary>Whether the ack of a SessionStart that declared the capability says the server delivers at prompts.</summary>
    public static bool AckDelivers(JsonNode? ack) =>
        ack is JsonObject obj && Text(obj["pass_notices"]) == CapabilityVersion;

    public static string Request(string sessionId) =>
        new JsonObject { ["session_id"] = sessionId, ["capability"] = CapabilityVersion }.ToJsonString();

    /// <summary>The server's texts, verbatim and in order, or empty for anything unreadable.</summary>
    public static IReadOnlyList<string> ReadNotices(string responseBody) {
        try {
            if (JsonNode.Parse(responseBody)?["notices"] is not JsonArray notices) return [];
            return [.. notices.Select(Text).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!)];
        } catch {
            return [];
        }
    }

    public static string RenderHookOutput(IReadOnlyList<string> notices) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "UserPromptSubmit",
                ["additionalContext"] = string.Join("\n", notices)
            }
        }.ToJsonString();

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
