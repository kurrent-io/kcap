using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>Claude Code's own plugin records under <c>&lt;claude home&gt;/plugins/</c>. kcap repoints the
/// ones it owns so they never name a plugin folder that has moved.</summary>
public static class ClaudePluginRecords {
    static readonly string[] MarketplaceKeys = ["kcap", "kurrent", "kapacitor"];
    static readonly string[] PluginKeys      = ["kcap@kcap", "kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"];

    /// <summary>Repoints the kcap directory marketplaces, and the install records that live under their
    /// old folder, at <paramref name="pluginPath"/>. Records are found by key; a record that is not
    /// there, or a cache path Claude copied a plugin to, is left alone and creates nothing.</summary>
    public static SettingsEdit Repoint(string claudeHome, string pluginPath) {
        var pluginsDir = Path.Combine(claudeHome, "plugins");
        var oldFolders = new List<string>();

        var marketsPath = Path.Combine(pluginsDir, "known_marketplaces.json");

        // A read-only pass first: a file that cannot be edited must stop the repoint before the other is written.
        var probe = JsonSettingsFile.Edit(marketsPath, root => {
            foreach (var entry in KcapMarketplaces(root)) {
                if (StringOf(entry["installLocation"]) is { Length: > 0 } location) oldFolders.Add(location);
                if (StringOf(entry["source"]!["path"]) is { Length: > 0 } path) oldFolders.Add(path);
            }

            return false;
        }, createIfMissing: false);

        if (probe is SettingsEdit.Malformed or SettingsEdit.Failed) return probe;

        var installs = JsonSettingsFile.Edit(Path.Combine(pluginsDir, "installed_plugins.json"), root => {
            if (oldFolders.Count == 0 || root["plugins"] is not JsonObject plugins) return false;

            var changed = false;
            foreach (var key in PluginKeys) {
                if (plugins[key] is not JsonArray records) continue;

                foreach (var record in records.OfType<JsonObject>()) {
                    if (StringOf(record["installPath"]) is not { Length: > 0 } installPath) continue;
                    if (Moved(installPath, oldFolders, pluginPath) is { } moved) changed |= Set(record, "installPath", moved);
                }
            }

            return changed;
        }, createIfMissing: false);

        if (installs is SettingsEdit.Malformed or SettingsEdit.Failed) return installs;

        var markets = JsonSettingsFile.Edit(marketsPath, root => {
            var changed = false;
            foreach (var entry in KcapMarketplaces(root)) {
                changed |= Set((JsonObject)entry["source"]!, "path", pluginPath);
                changed |= Set(entry, "installLocation", pluginPath);
            }

            return changed;
        }, createIfMissing: false);

        if (markets is SettingsEdit.Malformed or SettingsEdit.Failed) return markets;

        return markets == SettingsEdit.Changed || installs == SettingsEdit.Changed ? SettingsEdit.Changed : SettingsEdit.Unchanged;
    }

    static IEnumerable<JsonObject> KcapMarketplaces(JsonObject root) {
        foreach (var key in MarketplaceKeys) {
            if (root[key] is JsonObject { } entry && entry["source"] is JsonObject source && StringOf(source["source"]) == "directory")
                yield return entry;
        }
    }

    static string? Moved(string installPath, List<string> oldFolders, string pluginPath) {
        foreach (var folder in oldFolders) {
            if (!CanonicalPath.IsWithin(installPath, folder)) continue;

            // From the resolved paths the containment check used: two spellings of one tree give a `..` suffix.
            var relative = Path.GetRelativePath(CanonicalPath.Resolve(folder), CanonicalPath.Resolve(installPath));
            return relative == "." ? pluginPath : Path.Combine(pluginPath, relative);
        }

        return null;
    }

    static bool Set(JsonObject node, string name, string value) {
        if (StringOf(node[name]) == value) return false;
        node[name] = value;

        return true;
    }

    static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
