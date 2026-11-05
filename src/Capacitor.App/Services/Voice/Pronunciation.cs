using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// Spellings the voices read wrongly, rewritten just before speaking: acronyms that would be read
/// as words, and abbreviations that would be read letter by letter or skipped.
public static partial class Pronunciation {
    static readonly (Regex Pattern, string Spoken)[] Rules = [
        (Kcap(), "kay-cap"),
        (Word("PRs"), "P Rs"), (Word("PR"), "P R"),
        (Word("JSON"), "jason"), (Word("YAML"), "yammel"),
        (Word("APIs"), "A P Is"), (Word("API"), "A P I"),
        (Word("CLI"), "C L I"), (Word("UI"), "U I"), (Word("URLs"), "U R Ls"), (Word("URL"), "U R L"),
        (Word("MCP"), "M C P"), (Word("SDK"), "S D K"), (Word("CI"), "C I"), (Word("npm"), "N P M"),
        (Word("IDs"), "I Ds"), (Word("ID"), "I D"),
        (Abbreviation("e.g."), "for example"), (Abbreviation("i.e."), "that is"), (Abbreviation("etc."), "et cetera"),
        (Abbreviation("vs."), "versus"), (Word("vs"), "versus"),
    ];

    public static string Apply(string text) {
        foreach (var (pattern, spoken) in Rules) text = pattern.Replace(text, spoken);
        return text;
    }

    static Regex Word(string word) => new($@"\b{Regex.Escape(word)}\b", RegexOptions.CultureInvariant);

    static Regex Abbreviation(string abbreviation) => new($@"\b{Regex.Escape(abbreviation)}(?=\s|$|,)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\bkcap\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kcap();
}
