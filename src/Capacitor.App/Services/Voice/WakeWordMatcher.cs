using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// Finds the wake word at the start of a transcript. Whisper spells a made-up word differently
/// from one utterance to the next ("kcap", "K-Cap", "Kay cap"), so the match runs over the letters
/// alone, with letter names folded to the letter and a small edit distance allowed.
public static partial class WakeWordMatcher {
    /// What may come before the word ("Okay, computer"). Any other word first means the wake word
    /// is part of a sentence ("the computer is slow"), not a call.
    static readonly FrozenSet<string> Fillers = new[] {
        "ok", "okay", "hey", "hi", "hello", "um", "uh", "er", "erm", "so", "oh", "alright", "right", "yo",
    }.ToFrozenSet(StringComparer.Ordinal);
    const int MaxLeadingTokens = 2;

    static readonly FrozenDictionary<string, string> LetterNames = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["ay"] = "a", ["bee"] = "b", ["see"] = "c", ["sea"] = "c", ["dee"] = "d", ["ee"] = "e", ["eff"] = "f",
        ["gee"] = "g", ["aitch"] = "h", ["eye"] = "i", ["jay"] = "j", ["kay"] = "k", ["okay"] = "ok", ["el"] = "l",
        ["em"] = "m", ["en"] = "n", ["oh"] = "o", ["pee"] = "p", ["queue"] = "q", ["cue"] = "q", ["ar"] = "r",
        ["are"] = "r", ["es"] = "s", ["tee"] = "t", ["tea"] = "t", ["you"] = "u", ["vee"] = "v", ["ex"] = "x",
        ["why"] = "y", ["zee"] = "z", ["zed"] = "z",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <returns>Whether the transcript opens with the wake word, and what follows it in the
    /// transcript's own spelling and punctuation.</returns>
    public static bool TryMatch(string transcript, string wakeWord, out string remainder) {
        remainder = "";
        var key = Letters(wakeWord);
        if (key.Length == 0) return false;
        var tokens = Words().Matches(transcript);
        var (_, bestEnd) = Scan(tokens, wakeWord, Tolerance(key));
        if (bestEnd < 0) return false;

        var after = tokens[bestEnd].Index + tokens[bestEnd].Length;
        remainder = transcript[after..].TrimStart(' ', ',', '.', '!', '?', ':', ';', '-', '—', '…').Trim();
        return true;
    }

    /// Whether the transcript opens with something a couple of letters off the wake word and
    /// starting with its letter, worth a second listen by a better model.
    public static bool IsNearMiss(string transcript, string wakeWord) {
        var key = Letters(wakeWord);
        if (key.Length == 0) return false;
        var tokens = Words().Matches(transcript);
        var (start, end) = Scan(tokens, wakeWord, Tolerance(key) + 2);
        return end >= 0 && char.ToLowerInvariant(tokens[start].Value[0]) == key[0];
    }

    static int Tolerance(string key) => key.Length <= 4 ? 0 : key.Length <= 8 ? 1 : 2;

    // The tokens the wake word starts and ends on; End is -1 when it is not there.
    static (int Start, int End) Scan(MatchCollection tokens, string wakeWord, int tolerance) {
        var key = Letters(wakeWord);
        var keyFolded = Letters(Fold(wakeWord));
        var wakeTokens = Math.Max(1, Words().Count(wakeWord));

        var best = int.MaxValue;
        var bestEnd = -1;
        for (var start = 0; start <= MaxLeadingTokens && start < tokens.Count; start++) {
            var raw = "";
            var folded = "";
            for (var end = start; end < tokens.Count && end < start + wakeTokens + 2; end++) {
                var word = tokens[end].Value.ToLowerInvariant().Replace("'", "", StringComparison.Ordinal);
                raw += word;
                folded += LetterNames.TryGetValue(word, out var letter) ? letter : word;
                var distance = Math.Min(
                    Math.Min(Distance(raw, key), Distance(folded, key)),
                    Math.Min(Distance(raw, keyFolded), Distance(folded, keyFolded)));
                // The earliest, then shortest, match wins a tie: the rest is the command.
                if (distance <= tolerance && distance < best) {
                    best = distance;
                    bestEnd = end;
                }
            }
            if (bestEnd >= 0) return (start, bestEnd);
            if (!Fillers.Contains(tokens[start].Value.ToLowerInvariant())) break;
        }
        return (0, -1);
    }

    /// The transcript without Whisper's non-speech annotations ("[BLANK_AUDIO]", "(music)").
    /// Empty when nothing was said.
    public static string Clean(string transcript) {
        var text = Annotations().Replace(transcript, " ");
        text = Spaces().Replace(text, " ").Trim();
        return Words().IsMatch(text) ? text : "";
    }

    static string Fold(string text) =>
        string.Concat(Words().Matches(text).Select(m => {
            var word = m.Value.ToLowerInvariant();
            return LetterNames.TryGetValue(word, out var letter) ? letter : word;
        }));

    static string Letters(string text) =>
        string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit));

    internal static int Distance(string a, string b) {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Annotations();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
