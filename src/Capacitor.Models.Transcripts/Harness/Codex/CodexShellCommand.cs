namespace Capacitor.Models.Transcripts.Harness.Codex;

/// The message Codex records when the user runs a shell line with `!`: the command, then its result.
public static class CodexShellCommand {
    const string Open = "<user_shell_command>";

    public static bool IsMatch(string text) => text.AsSpan().TrimStart().StartsWith(Open, StringComparison.Ordinal);

    /// The command that was run; null when the record names none.
    public static string? Command(string text) {
        var start = text.IndexOf("<command>", StringComparison.Ordinal);
        var end = text.IndexOf("</command>", StringComparison.Ordinal);

        return start >= 0 && end > start && text[(start + "<command>".Length)..end].Trim() is { Length: > 0 } command ? command : null;
    }
}
