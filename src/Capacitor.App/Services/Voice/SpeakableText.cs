using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// An agent's Markdown reply as something worth hearing: code is announced rather than read
/// character by character, links read as their text, and a long reply stops at a sentence with a
/// pointer to the screen.
public static partial class SpeakableText {
    public const int DefaultLimit = 700;

    public static string From(string markdown, int limit = DefaultLimit) {
        var text = FencedCode().Replace(markdown, " (code block on screen) ");
        text = Table().Replace(text, " (table on screen) ");
        text = Image().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = InlineCode().Replace(text, m => m.Groups[1].Value.Length <= 40 ? m.Groups[1].Value : "code");
        text = HeadingOrQuote().Replace(text, "");
        text = Bullet().Replace(text, "");
        text = Emphasis().Replace(text, "$2");
        text = BareUrl().Replace(text, "a link");
        text = Html().Replace(text, "");

        var sentences = new StringBuilder();
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sentences.Length > 0) sentences.Append(EndsSentence(sentences) ? " " : ". ");
            sentences.Append(line);
        }
        var spoken = Spaces().Replace(sentences.ToString(), " ").Trim();
        return Truncate(spoken, limit);
    }

    static bool EndsSentence(StringBuilder text) => text.Length > 0 && text[^1] is '.' or '!' or '?' or ':' or ';';

    static string Truncate(string text, int limit) {
        if (text.Length <= limit) return text;
        var cut = text.LastIndexOfAny(['.', '!', '?'], limit);
        var head = cut > limit / 3 ? text[..(cut + 1)] : text[..limit].TrimEnd() + "…";
        return head + " The rest is on screen.";
    }

    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"(^\|.*\|[ \t]*$\n?)+", RegexOptions.Multiline)]
    private static partial Regex Table();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^[ \t]*(#{1,6}|>)[ \t]*", RegexOptions.Multiline)]
    private static partial Regex HeadingOrQuote();

    [GeneratedRegex(@"^[ \t]*([-*+]|\d+[.)])[ \t]+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Html();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
