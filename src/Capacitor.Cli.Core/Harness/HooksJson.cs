using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness;

/// <summary>Detection over a vendor hooks file shaped <c>{ "hooks": { "&lt;event&gt;": [entry, …] } }</c>, where
/// <c>isKcapEntry</c> recognises the vendor's kcap entry.</summary>
public static class HooksJson {
    /// <summary>Whether every one of <paramref name="events"/> carries at least one kcap entry.</summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events, Func<JsonNode?, bool> isKcapEntry) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;
            if (!entries.Any(isKcapEntry)) return false;
        }

        return true;
    }

    /// <summary>The install marker beside <paramref name="hooksPath"/>, or a kcap entry under any event in the
    /// file — the latter covers an install whose marker was lost. Malformed JSON reads as not installed.</summary>
    public static bool IsInstalled(string hooksPath, string markerFileName, Func<JsonNode?, bool> isKcapEntry) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, markerFileName))) return true;
        if (!File.Exists(hooksPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks)
                if (entries is JsonArray arr && arr.Any(isKcapEntry)) return true;
        } catch { /* malformed → not installed */ }

        return false;
    }
}
