using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>How the kcap plugin's <c>hooks/hooks.json</c> has Claude Code start kcap.</summary>
public static class ClaudePluginHooks {
    /// <summary>True when any hook is shell form (a <c>command</c> string with no <c>args</c>), which Claude runs
    /// through PowerShell on Windows without Git Bash. A manifest that cannot be read counts as shell form.</summary>
    public static bool StartThroughShell(string pluginDir) {
        JsonNode? manifest;
        try {
            manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginDir, "hooks", "hooks.json")));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return true;
        }

        if (manifest?["hooks"] is not JsonObject events) return true;

        foreach (var (_, groups) in events) {
            if (groups is not JsonArray groupList) return true;

            foreach (var group in groupList) {
                if (group?["hooks"] is not JsonArray hooks) return true;

                foreach (var hook in hooks)
                    if (hook?["args"] is not JsonArray) return true;
            }
        }

        return false;
    }
}
