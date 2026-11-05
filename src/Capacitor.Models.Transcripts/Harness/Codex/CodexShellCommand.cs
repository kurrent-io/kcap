using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// The message Codex records when the user runs a shell line with `!`: the command, then its result.
public static class CodexShellCommand {
    /// The message is the record and nothing else: words after it are the person writing about one.
    public static bool IsMatch(string text) => WholeFrame.Is(text, "<user_shell_command>", "</user_shell_command>");

    /// The command that was run; null when the record names none.
    public static string? Command(string text) {
        var start = text.IndexOf("<command>", StringComparison.Ordinal);
        var end = text.IndexOf("</command>", StringComparison.Ordinal);

        return start >= 0 && end > start && text[(start + "<command>".Length)..end].Trim() is { Length: > 0 } command ? command : null;
    }
}
