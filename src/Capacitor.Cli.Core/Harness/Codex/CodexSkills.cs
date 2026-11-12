using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Maps a <c>skills/list</c> result to picker commands. An absent or unexpected shape yields an
/// empty list rather than throwing, so the picker shows nothing instead of failing a launch.</summary>
public static class CodexSkills {
    /// <summary>Codex runs a skill named as <c>$name</c>, and refuses an unknown <c>/name</c>.</summary>
    public const char Sigil = '$';

    public static IReadOnlyList<HostedAgentCommand> Extract(JsonElement result) {
        var list = new List<HostedAgentCommand>();

        // A flat array of skills, or entries grouping them per cwd; the first array that yields any wins, so none counts twice.
        foreach (var arrayName in new[] { "data", "skills" }) {
            if (result.Arr(arrayName) is not { } arr) continue;

            foreach (var entry in arr.EnumerateArray()) {
                if (!entry.IsObject) continue;

                if (entry.Arr("skills") is { } nested) {
                    foreach (var skill in nested.EnumerateArray()) Add(list, skill);
                } else {
                    Add(list, entry);
                }
            }

            if (list.Count > 0) break;
        }

        return list;
    }

    static void Add(List<HostedAgentCommand> list, JsonElement entry) {
        if (!entry.IsObject || entry.Bool("enabled") == false) return;

        var name = entry.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return;

        var description = entry.Str("description");
        list.Add(new HostedAgentCommand(
            name,
            string.IsNullOrWhiteSpace(description) ? null : description,
            ArgumentHint: null));
    }
}
