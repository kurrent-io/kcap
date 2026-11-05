using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Parsing helpers for kcap's Copilot hooks file
/// (<c>~/.copilot/hooks/kcap.json</c>). Copilot's schema is
/// <c>{"version": 1, "hooks": {eventName: [entry…]}}</c> where each command
/// entry carries the shell command under <c>command</c> (cross-platform),
/// <c>bash</c>, or <c>powershell</c>.
/// </summary>
public static class CopilotHooksParser {
    /// <summary>
    /// The Copilot hook events kcap subscribes to. Deliberately minimal —
    /// every hook execution writes a hook.start/hook.end noise pair into the
    /// session's events.jsonl, so per-tool events (preToolUse/postToolUse/
    /// permissionRequest) would double transcript noise for content the
    /// transcript already carries. sessionStart/sessionEnd own lifecycle,
    /// agentStop refreshes watcher liveness each turn, notification forwards
    /// to the server's notification record.
    /// </summary>
    public static readonly string[] CopilotHookEvents = [
        "sessionStart",
        "sessionEnd",
        "agentStop",
        "notification"
    ];

    const string HookArguments = "hook --copilot";

    /// <summary>
    /// The <c>command</c> of one hook entry. VS Code and Copilot run it in PowerShell on Windows, where a quoted
    /// path does not parse without the call operator, and in a POSIX shell elsewhere.
    /// </summary>
    public static string HookCommandLine(string eventName, string binary, bool windows) {
        var invocation = binary == KcapMcpServers.Command ? binary
            : windows ? $"& '{binary.Replace("'", "''")}'"
            :           $"'{binary.Replace("'", @"'\''")}'";

        return $"{invocation} {HookArguments} --event {eventName}";
    }

    /// <summary>
    /// True if <paramref name="entry"/> is a command entry referencing the
    /// kcap Copilot hook dispatcher, in any of the three command fields.
    /// </summary>
    public static bool EntryReferencesCapacitorCopilotHook(JsonNode? entry) {
        return FieldContains(entry, "command")
            || FieldContains(entry, "bash")
            || FieldContains(entry, "powershell");

        static bool FieldContains(JsonNode? entry, string field) =>
            entry?[field] is JsonValue jv
         && jv.TryGetValue<string>(out var cmd)
         && cmd.IndexOf($" {HookArguments}", StringComparison.Ordinal) is > 0 and var at
         && KcapBinaryCommand.IsKcap(Unquote(cmd[..at]));

        // The executable as HookCommandLine writes it; either quoting's escape only changes characters in the path.
        static string Unquote(string executable) {
            var bare = executable.StartsWith("& ", StringComparison.Ordinal) ? executable[2..] : executable;

            return bare is ['\'', .., '\''] ? bare[1..^1].Replace(@"'\''", "'").Replace("''", "'") : bare;
        }
    }

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one
    /// entry referencing the kcap copilot command.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            if (!entries.Any(EntryReferencesCapacitorCopilotHook)) return false;
        }

        return true;
    }
}
