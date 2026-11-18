using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// <c>kcap hook --claude --title-prompt</c>: on a session's first <c>UserPromptSubmit</c>, asks the
/// agent to name the session with <c>kcap set-title --auto</c>, which never replaces a start title or
/// a rename. Local only, and dispatched before repository resolution, because every prompt waits for it.
/// Asked only under <c>bypassPermissions</c>: in any other mode the Bash call stops for approval or is
/// denied outright (<c>claude -p</c>), and a plugin cannot ship the permission rule that would allow it.
/// </summary>
static partial class ClaudeTitlePrompt {
    public const string Flag = "--title-prompt";

    const string BypassPermissions = "bypassPermissions";

    const string Instruction =
        "<system-instructions>\n"
      + "After reading the user's message and understanding the task, silently run this command via Bash (do not mention it to the user):\n"
      + "kcap set-title --auto \"<your concise 5-10 word title summarizing the task>\"\n"
      + "The title should describe WHAT the user wants done, not HOW. Use imperative form (e.g. \"Fix authentication timeout in login flow\"). No period at the end.\n"
      + "</system-instructions>";

    /// <param name="bashTool">False on Windows without Git Bash: Claude has no Bash tool there, and asking for the
    /// command anyway sends it through PowerShell, which a manual-permission session stops to approve.</param>
    public static int Handle(string body, ConfigRoot config, WatcherPaths watchers, bool bashTool, TextWriter stdout, TextWriter stderr) {
        if (!bashTool) return 0;

        string? sessionId;
        string? permissionMode;
        try {
            if (JsonNode.Parse(body) is not JsonObject hook) return 0;
            sessionId      = Str(hook, "session_id");
            permissionMode = Str(hook, "permission_mode");
        } catch (JsonException) {
            return 0;
        }
        if (sessionId is null || !SafeId().IsMatch(sessionId)) return 0;

        // No marker outside bypass, so a session switched to it later is still asked.
        if (permissionMode != BypassPermissions) return 0;

        try {
            if (DisabledSessions.IsDisabled(sessionId.Replace("-", ""), config)) return 0;

            // The marker goes down before the output: a marker that cannot be written must not turn
            // into the same instruction on every prompt.
            var marker = Path.Combine(watchers.Directory, $"{sessionId}.title-requested");
            if (File.Exists(marker)) return 0;
            Directory.CreateDirectory(watchers.Directory);
            File.WriteAllText(marker, "");

            stdout.WriteLine(new JsonObject {
                ["hookSpecificOutput"] = new JsonObject {
                    ["hookEventName"]     = "UserPromptSubmit",
                    ["additionalContext"] = Instruction
                }
            }.ToJsonString());
        } catch (Exception e) {
            stderr.WriteLine($"kcap: title prompt for session {sessionId} skipped: {e.Message}");
        }

        return 0;
    }

    static string? Str(JsonObject hook, string key) =>
        hook[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SafeId();
}
