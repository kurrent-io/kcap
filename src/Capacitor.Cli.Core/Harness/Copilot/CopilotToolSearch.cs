using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>Copilot CLI's user-wide <c>toolSearch</c> setting. Off, Copilot sends every MCP tool definition with
/// every request; on, it defers them behind its tool search. kcap writes it only while it is absent, so a value
/// the user chose, either way, stays theirs and a later refresh never flips it back.</summary>
public static class CopilotToolSearch {
    public const string Key = "toolSearch";

    public static SettingsEdit Enable(string settingsPath) =>
        JsonSettingsFile.Edit(settingsPath, root => {
            if (root.ContainsKey(Key)) return false;

            root[Key] = true;

            return true;
        });

    /// <summary>True only for an explicit <c>false</c>; an absent, unreadable or other value is not "off".</summary>
    public static bool IsTurnedOff(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return false;

            return JsonNode.Parse(File.ReadAllTextShared(settingsPath)) is JsonObject root
                && root[Key] is JsonValue value
                && value.TryGetValue(out bool on)
                && !on;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return false;
        }
    }
}
