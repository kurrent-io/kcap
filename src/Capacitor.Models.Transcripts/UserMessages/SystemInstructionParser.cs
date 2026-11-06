using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.UserMessages;

/// The system blocks harnesses inject into a user message: Claude Code's `system-reminder`,
/// Copilot's `system_reminder`, and the instruction blocks some hooks write.
public static partial class SystemInstructionParser {
    public readonly record struct InstructionBlock(string TagName, string Content);

    public readonly record struct ParseResult(string CleanText, List<InstructionBlock> Blocks);

    /// The text with every block removed and trimmed, plus the blocks. A text with no block comes back
    /// as it is, untrimmed.
    public static ParseResult Parse(string? text) {
        if (string.IsNullOrEmpty(text)) return new(string.Empty, []);

        var blocks = new List<InstructionBlock>();
        var clean = Block().Replace(text, match => {
            blocks.Add(new(match.Groups[1].Value, match.Groups[2].Value.Trim()));
            return "";
        });

        return new(blocks.Count > 0 ? clean.Trim() : text, blocks);
    }

    /// The text after the blocks that open it, so what follows them can be read as it starts; blocks
    /// further on stay.
    public static string StripLeading(string text) {
        var rest = text.TrimStart();
        while (rest.StartsWith('<') && Block().Match(rest) is { Success: true, Index: 0 } block)
            rest = rest[block.Length..].TrimStart();

        return rest.Length == text.Length ? text : rest;
    }

    [GeneratedRegex(
        @"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>([\s\S]*?)</\1>",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Block();
}
