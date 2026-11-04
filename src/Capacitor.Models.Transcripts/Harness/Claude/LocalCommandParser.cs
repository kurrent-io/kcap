using System.Net;
using System.Text.RegularExpressions;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The user messages Claude Code records when the user runs a slash or local command: the invocation
/// (`command-name` with its message and args), the command's stdout, or the fixed caveat.
public static partial class LocalCommandParser {
    public enum LocalCommandKind { None, Caveat, Command, Output }

    public readonly record struct Result(
        LocalCommandKind Kind,
        string? CommandName,
        string? CommandArgs,
        string? OutputText,
        string RemainingText
    );

    /// Only the first kind present is read: invocation, then output, then caveat.
    public static Result Parse(string? text) {
        if (string.IsNullOrEmpty(text)) return new(LocalCommandKind.None, null, null, null, "");

        var command = CommandName().Match(text);
        if (command.Success) {
            var name = WebUtility.HtmlDecode(command.Groups[1].Value.Trim());
            var args = CommandArgs().Match(text) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value.Trim()) : null;

            return new(LocalCommandKind.Command, name, args, null, StripCommandTags(text));
        }

        var output = Stdout().Match(text);
        if (output.Success)
            return new(LocalCommandKind.Output, null, null, TerminalText.Clean(output.Groups[1].Value), Stdout().Replace(text, "").Trim());

        if (Caveat().IsMatch(text))
            return new(LocalCommandKind.Caveat, null, null, null, Caveat().Replace(text, "").Trim());

        return new(LocalCommandKind.None, null, null, null, text);
    }

    /// True when the text is wholly a payload: a prompt that merely quotes one of the tags has text left over.
    public static bool IsPure(string? text, out Result result) {
        result = Parse(text);

        return result.Kind != LocalCommandKind.None && string.IsNullOrWhiteSpace(result.RemainingText);
    }

    const int OutputLabelMax = 200;

    /// The command line for an invocation, the first stdout line bounded at 200 characters, fixed text for
    /// the caveat; a None result's remaining text.
    public static string DisplayLabel(Result result) {
        switch (result.Kind) {
            case LocalCommandKind.Command:
                return string.IsNullOrEmpty(result.CommandArgs) ? result.CommandName ?? "" : $"{result.CommandName} {result.CommandArgs}";

            case LocalCommandKind.Output: {
                var stdout = result.OutputText ?? "";
                var newline = stdout.IndexOfAny(['\r', '\n']);
                var line = (newline >= 0 ? stdout[..newline] : stdout).Trim();

                if (line.Length == 0) return "Command output";

                return line.Length <= OutputLabelMax ? line : line[..OutputLabelMax] + "…";
            }

            case LocalCommandKind.Caveat:
                return "Local command context";

            default:
                return result.RemainingText;
        }
    }

    static string StripCommandTags(string text) =>
        CommandArgs().Replace(CommandMessage().Replace(CommandName().Replace(text, ""), ""), "").Trim();

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex("<command-message>(.*?)</command-message>", RegexOptions.Singleline)]
    private static partial Regex CommandMessage();

    [GeneratedRegex("<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    // A stored record can end before its closing tag.
    [GeneratedRegex("<local-command-stdout>(.*?)(?:</local-command-stdout>|$)", RegexOptions.Singleline)]
    private static partial Regex Stdout();

    [GeneratedRegex("<local-command-caveat>(.*?)(?:</local-command-caveat>|$)", RegexOptions.Singleline)]
    private static partial Regex Caveat();
}
