using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// Minimal reader for Copilot's per-session <c>workspace.yaml</c>. The file is
/// flat <c>key: value</c> lines (no nesting, no quoting in practice) — a full
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

            foreach (var line in File.ReadLinesShared(path)) {
                var idx = line.IndexOf(": ", StringComparison.Ordinal);
                if (idx <= 0) continue;

                var key   = line[..idx].Trim();
                var value = Unquote(line[(idx + 2)..].Trim());

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

    static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var ts)
            ? ts
            : null;
}
