using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// Minimal reader for Copilot's per-session <c>workspace.yaml</c>. The file is
/// flat <c>key: value</c> lines, quoted where YAML needs it, a multi-line name as a block scalar — a full
/// YAML dependency would be overkill for the fields we need, and a parse
/// failure must never break discovery (returns null / partial data instead).
/// </summary>
public sealed record CopilotWorkspaceYaml(
        string? Cwd, string? Name, bool UserNamed, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
        string? Repository = null, string? Branch = null) {
    public static CopilotWorkspaceYaml? TryRead(string path) {
        try {
            if (!File.Exists(path)) return null;

            string? cwd        = null;
            string? name       = null;
            string? repository = null;
            string? branch     = null;
            bool userNamed     = false;
            DateTimeOffset? createdAt = null;
            DateTimeOffset? updatedAt = null;

            var lines = File.ReadLinesShared(path).ToList();

            for (var i = 0; i < lines.Count; i++) {
                var line = lines[i];
                var idx  = line.IndexOf(": ", StringComparison.Ordinal);
                if (idx <= 0) continue;

                var key   = line[..idx].Trim();
                var raw   = line[(idx + 2)..].Trim();
                var value = raw is ['|' or '>', ..] ? BlockScalar(lines, ref i, folded: raw[0] == '>') : Unquote(raw);

                if (value.Length == 0) continue;

                switch (key) {
                    case "cwd":        cwd  = value; break;
                    case "name":       name = value; break;
                    case "repository": repository = value; break;
                    case "branch":     branch = value; break;
                    case "user_named": userNamed = value == "true"; break;
                    case "created_at": createdAt = ParseTimestamp(value); break;
                    case "updated_at": updatedAt = ParseTimestamp(value); break;
                }
            }

            return new CopilotWorkspaceYaml(cwd, name, userNamed, createdAt, updatedAt, repository, branch);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Strips matching YAML scalar quotes. Copilot quotes values that contain
    /// YAML-special sequences (e.g. <c>name: 'Reply with exactly: ok'</c>) —
    /// without this the literal quotes leak into imported session titles.
    /// Handles the two YAML escape forms we can hit on one line: doubled
    /// single-quotes inside single-quoted scalars, and backslash-escaped
    /// double-quotes inside double-quoted scalars.
    /// </summary>
    internal static string Unquote(string value) {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') {
            return value[1..^1].Replace("''", "'");
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') {
            return value[1..^1].Replace("\\\"", "\"");
        }

        return value;
    }

    /// <summary>The indented lines after a <c>|</c> or <c>&gt;</c> indicator, which Copilot writes for a multi-line
    /// name. Leaves <paramref name="i"/> on the block's last line.</summary>
    static string BlockScalar(List<string> lines, ref int i, bool folded) {
        var block  = new List<string>();
        int? indent = null;

        while (i + 1 < lines.Count && (lines[i + 1].Length == 0 || lines[i + 1][0] == ' ')) {
            var next = lines[++i];
            if (next.Trim().Length > 0) indent ??= next.Length - next.TrimStart(' ').Length;

            block.Add(next);
        }

        var text = block.Select(l => l.Trim().Length == 0 ? "" : l[Math.Min(indent ?? 0, l.Length)..]);

        return string.Join(folded ? " " : "\n", text).Trim('\n', ' ');
    }

    static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var ts)
            ? ts
            : null;
}
