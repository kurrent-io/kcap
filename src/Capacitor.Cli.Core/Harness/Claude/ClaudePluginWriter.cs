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

    /// <summary>Enables the steering mod in a file that registers kcap's marketplace, unless the file already says
    /// whether it is enabled. Null, with nothing written, where the marketplace isn't registered: only kcap's own
    /// install writes that entry, because <see cref="ClaudePluginInstaller.IsInstalled"/> reads it as kcap
    /// installed and a refresh would then turn recording on.</summary>
    public static SettingsEdit? InstallSteering(string settingsPath) {
        var registered = false;

        var result = JsonSettingsFile.Edit(settingsPath, root => {
            if (!ClaudePluginInstaller.RegistersKcapMarketplace(root)) return false;

            registered = true;

            if (root["enabledPlugins"] is not JsonObject enabled) {
                if (root.ContainsKey("enabledPlugins")) throw JsonSettingsFile.Refuse();
                root["enabledPlugins"] = enabled = [];
            }

            if (enabled.ContainsKey(ClaudePluginInstaller.SteeringPluginKey)) return false;

            enabled[ClaudePluginInstaller.SteeringPluginKey] = true;

            return true;
        }, createIfMissing: false);

        if (result is SettingsEdit.Malformed or SettingsEdit.Failed) return result;
        if (!registered) return null;

        ClaudePluginInstaller.WriteSteeringMarker(settingsPath);

        return result;
    }

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
