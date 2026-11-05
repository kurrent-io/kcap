namespace Capacitor.App.Services.Voice;

/// Picks the session a spoken name means: the most title words heard, near-misses counted, so
/// "open the login session" finds "Fix login redirect loop".
public static class SessionMatcher {
    static readonly HashSet<string> Filler = new(StringComparer.Ordinal) { "the", "a", "an", "one", "session", "agent", "chat", "about", "for", "on", "in", "to", "of" };

    public static VoiceSession? Best(string query, IReadOnlyList<VoiceSession> sessions) {
        var wanted = Words(query).Where(w => !Filler.Contains(w)).ToList();
        if (wanted.Count == 0) return null;
        if (OrdinalIndex(wanted) is { } index && index < sessions.Count) return sessions[index];

        VoiceSession? best = null;
        var bestScore = 0.0;
        foreach (var session in sessions) {
            var title = Words(session.Title).ToList();
            if (title.Count == 0) continue;
            var score = wanted.Sum(w => title.Select(t => Similarity(w, t)).DefaultIfEmpty(0).Max()) / wanted.Count;
            if (score > bestScore) {
                bestScore = score;
                best = session;
            }
        }
        return bestScore >= 0.6 ? best : null;
    }

    // "open session two", "the first one".
    static int? OrdinalIndex(List<string> words) {
        if (words.Count != 1) return null;
        return words[0] switch {
            "1" or "one" or "first" => 0,
            "2" or "two" or "second" => 1,
            "3" or "three" or "third" => 2,
            "4" or "four" or "fourth" => 3,
            "5" or "five" or "fifth" => 4,
            "6" or "six" or "sixth" => 5,
            _ => null,
        };
    }

    static double Similarity(string a, string b) {
        if (a == b) return 1;
        if (a.Length >= 4 && b.StartsWith(a, StringComparison.Ordinal)) return 0.9;
        var longest = Math.Max(a.Length, b.Length);
        return longest < 4 ? 0 : 1 - (double)WakeWordMatcher.Distance(a, b) / longest;
    }

    static IEnumerable<string> Words(string text) =>
        VoiceCommandParser.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
