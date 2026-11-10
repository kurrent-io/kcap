using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

public static class ClaudePluginWriter {
    static readonly string[] LegacyMarketplaces = ["kurrent", "kapacitor"];
    static readonly string[] LegacyPlugins      = ["kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"];

    /// <param name="claudeHome">The user-scope Claude home whose plugin records follow the move; null for a
    /// project-scope install, which has none.</param>
    public static SettingsEdit Install(string settingsPath, string? claudeHome, string marketplacePath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var marketplaces = root["extraKnownMarketplaces"] as JsonObject ?? [];
            root["extraKnownMarketplaces"] = marketplaces;
            var entry = new JsonObject {
                ["source"] = new JsonObject { ["source"] = "directory", ["path"] = marketplacePath }
            };
            var changed = marketplaces["kcap"]?.ToJsonString() != entry.ToJsonString();
            marketplaces["kcap"] = entry;
            foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);

            var enabled = root["enabledPlugins"] as JsonObject ?? [];
            root["enabledPlugins"] = enabled;
            changed |= enabled["kcap@kcap"]?.ToJsonString() != "true";
            enabled["kcap@kcap"] = true;
            foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);

            return changed;
        });

        if (result is not (SettingsEdit.Changed or SettingsEdit.Unchanged)) return result;

        if (claudeHome is not null) {
            var records = ClaudePluginRecords.Repoint(claudeHome, marketplacePath);
            if (records is SettingsEdit.Malformed or SettingsEdit.Failed) return SettingsEdit.Failed;
            if (records == SettingsEdit.Changed) result = SettingsEdit.Changed;
        }

        ClaudePluginInstaller.WriteMarker(settingsPath);

        return result;
    }

    public static SettingsEdit Remove(string settingsPath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var changed = false;
            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kcap@kcap");
                foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);
            }
            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kcap");
                foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);
            }
            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) ClaudePluginInstaller.DeleteMarker(settingsPath);

        return result;
    }
}
