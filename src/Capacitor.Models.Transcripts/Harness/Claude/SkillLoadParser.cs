namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The message Claude Code delivers on the user channel when a skill loads: the skill's whole body,
/// headed by the directory it was read from.
public static class SkillLoadParser {
    const string Header = "Base directory for this skill:";

    /// True when the message opens with the header, which nobody types, however it continues.
    public static bool IsMatch(string? text) =>
        text is not null && text.AsSpan().TrimStart().StartsWith(Header, StringComparison.Ordinal);

    /// The skill's name, its directory's last segment; null when the header names no directory.
    public static string? SkillName(string text) {
        var start = text.IndexOf(Header, StringComparison.Ordinal);
        if (start < 0) return null;

        var rest = text[(start + Header.Length)..];
        var end = rest.IndexOf('\n');
        var dir = (end < 0 ? rest : rest[..end]).Trim().TrimEnd('/', '\\');
        var slash = dir.LastIndexOfAny(['/', '\\']);
        var name = slash < 0 ? dir : dir[(slash + 1)..];

        return name.Length > 0 ? name : null;
    }

    public static string DisplayLabel(string text) =>
        SkillName(text) is { } name ? $"Loaded skill: {name}" : "Loaded a skill";
}
