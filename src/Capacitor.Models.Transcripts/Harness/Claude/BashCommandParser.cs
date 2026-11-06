using System.Text.RegularExpressions;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The user messages Claude Code records when the user runs a shell command with the `!` prefix: the
/// command in `bash-input`, its output in `bash-stdout` / `bash-stderr`.
public static partial class BashCommandParser {
    public enum BashKind { None, Input, Output }

    public readonly record struct Result(
        BashKind Kind,
        string?  Command,
        string?  OutputText,
        string   RemainingText
    );

    public static Result Parse(string? text) {
        if (string.IsNullOrEmpty(text)) return new(BashKind.None, null, null, "");

        // The input needs its closing tag, so a prompt that opens with the bare tag stays a prompt.
        var input = Input().Match(text);
        if (input.Success) return new(BashKind.Input, FirstLine(input.Groups[1].Value).Trim(), null, StripAllTags(text));

        var stdout = Stdout().Match(text);
        var stderr = Stderr().Match(text);
        if (stdout.Success || stderr.Success) {
            var combined = (stdout.Success ? stdout.Groups[1].Value : "") + "\n" + (stderr.Success ? stderr.Groups[1].Value : "");

            return new(BashKind.Output, null, TerminalText.Clean(combined), StripAllTags(text));
        }

        return new(BashKind.None, null, null, text);
    }

    /// True when the text is wholly a payload: a prompt that merely quotes a tag has text left over.
    public static bool IsPure(string? text, out Result result) {
        result = Parse(text);

        return result.Kind != BashKind.None && string.IsNullOrWhiteSpace(result.RemainingText);
    }

    const int OutputLabelMax = 200;

    /// `$ cmd` for an input; the first meaningful output line bounded at 200 characters, or
    /// `Command output` when there is none.
    public static string DisplayLabel(Result result) {
        switch (result.Kind) {
            case BashKind.Input:
                return $"$ {result.Command}";

            case BashKind.Output: {
                var line = TerminalText.FirstMeaningfulLine(result.OutputText ?? "");

                if (line is null) return "Command output";

                return line.Length <= OutputLabelMax ? line : line[..OutputLabelMax] + "…";
            }

            default:
                return result.RemainingText;
        }
    }

    static string StripAllTags(string text) => Stderr().Replace(Stdout().Replace(Input().Replace(text, ""), ""), "").Trim();

    static string FirstLine(string s) {
        var end = s.IndexOfAny(['\r', '\n']);

        return end >= 0 ? s[..end] : s;
    }

    [GeneratedRegex("<bash-input>(.*?)</bash-input>", RegexOptions.Singleline)]
    private static partial Regex Input();

    // Output can be cut off before its closing tag.
    [GeneratedRegex("<bash-stdout>(.*?)(?:</bash-stdout>|$)", RegexOptions.Singleline)]
    private static partial Regex Stdout();

    [GeneratedRegex("<bash-stderr>(.*?)(?:</bash-stderr>|$)", RegexOptions.Singleline)]
    private static partial Regex Stderr();
}
