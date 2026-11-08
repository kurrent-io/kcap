using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

public static class ClaudePluginWriter {
    static readonly string[] LegacyMarketplaces = ["kurrent", "kapacitor"];
    static readonly string[] LegacyPlugins      = ["kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"];

    public static SettingsEdit Install(string settingsPath, string marketplacePath) {
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

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) ClaudePluginInstaller.WriteMarker(settingsPath);

        return result;
    }

    /// <summary>Enables the steering mod unless the file already says whether it is enabled. The <c>kcap</c>
    /// marketplace entry is written only when there is no usable one: it belongs to the kcap plugin's install.</summary>
    public static SettingsEdit InstallSteering(string settingsPath, string marketplacePath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var marketplaces = ObjectOrNew(root, "extraKnownMarketplaces");
            var enabled      = ObjectOrNew(root, "enabledPlugins");
            var changed      = false;

            if (RegisteredPath(marketplaces) is null) {
                marketplaces["kcap"] = new JsonObject {
                    ["source"] = new JsonObject { ["source"] = "directory", ["path"] = marketplacePath }
                };
                changed = true;
            }

            if (!enabled.ContainsKey(ClaudePluginInstaller.SteeringPluginKey)) {
                enabled[ClaudePluginInstaller.SteeringPluginKey] = true;
                changed = true;
            }

            return changed;
        });

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) ClaudePluginInstaller.WriteSteeringMarker(settingsPath);

        return result;
    }

    static JsonObject ObjectOrNew(JsonObject root, string key) {
        if (root[key] is JsonObject existing) return existing;
        if (root.ContainsKey(key)) throw JsonSettingsFile.Refuse();

        var created = new JsonObject();
        root[key] = created;

        return created;
    }

    static string? RegisteredPath(JsonObject marketplaces) =>
        marketplaces["kcap"] is JsonObject kcap && kcap["source"] is JsonObject source
     && source["path"] is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : null;

    public static SettingsEdit Remove(string settingsPath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var changed = false;
            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kcap@kcap");
                changed |= enabled.Remove(ClaudePluginInstaller.SteeringPluginKey);
                foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);
            }
            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kcap");
                foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);
            }
            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) {
            ClaudePluginInstaller.DeleteMarker(settingsPath);
            ClaudePluginInstaller.DeleteSteeringMarker(settingsPath);
        }

        return result;
    }
}
