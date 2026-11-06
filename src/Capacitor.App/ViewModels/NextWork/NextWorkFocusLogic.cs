using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>Turns the engine's kept rows into <see cref="NextWorkFocus"/>, as the web Home band does. Everything here
/// is a view over the snapshot: folding, quiet and the verbs are read from labels and facets.</summary>
public static partial class NextWorkFocusLogic {
    public const int PickCount = 3;

    /// <summary>A work item with no tracker issue and no activity for this long stops competing with work.</summary>
    public const int QuietDays = 7;

    /// <summary>A loose end untouched for this long stops competing with work.</summary>
    public const int QuietLooseEndDays = 14;

    public static NextWorkFocus Build(NextWorkSnapshot? snapshot, DateTimeOffset now) {
        var rows = NextWorkLaneLogic.Kept(snapshot).Where(r => NextWorkLaneLogic.GroupOf(r) is not null).ToList();
        if (rows.Count == 0) return NextWorkFocus.Empty;

        var tokens = new Tokens();
        var items  = rows.Where(r => r.TargetKind != "loose_end").Select(r => new Draft(r, OwnEnds(r, tokens, now))).ToList();
        var byKey  = items.Where(d => IssueKey(d.Row) is not null).GroupBy(d => IssueKey(d.Row)!, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var loose  = new List<Draft>();

        foreach (var row in rows.Where(r => r.TargetKind == "loose_end")) {
            var text = NextWorkLaneLogic.DisplayLabel(row);
            var id   = LooseEndId(row);

            // Someone waiting on the viewer keeps a row of their own.
            if (NextWorkLaneLogic.GroupOf(row) != NextWorkGroup.Waiting) {
                if (id is not null && items.FirstOrDefault(d => d.Ends.Any(e => e.LooseEndId == id)) is { } same) {
                    same.Same.Add(row);
                    continue;
                }

                if (Mentions(text).FirstOrDefault(byKey.ContainsKey) is { } key) {
                    var owner = byKey[key];
                    owner.Folded.Add(row);
                    owner.Ends.Add(new(id, text, null, NextWorkLaneLogic.NotedAgo(row), Repeats(row) - 1));
                    continue;
                }

                if (FindSimilar(items.Concat(loose), text, tokens) is { } twin) {
                    twin.Draft.Folded.Add(row);
                    twin.Draft.Ends[twin.Index] = twin.Draft.Ends[twin.Index] with { Repeats = twin.Draft.Ends[twin.Index].Repeats + 1 };
                    continue;
                }
            }

            loose.Add(new(row, [new(id, text, null, NextWorkLaneLogic.NotedAgo(row), Repeats(row) - 1)]));
        }

        var all = items.Concat(loose).OrderBy(d => d.Row.Rank ?? int.MaxValue).Select(Finish).ToList();

        var waiting = all.Where(i => i.Kind == NextWorkFocusKind.Work && NextWorkLaneLogic.GroupOf(i.Row) == NextWorkGroup.Waiting).ToList();
        var work    = all.Where(i => i.Kind == NextWorkFocusKind.Work).Except(waiting).ToList();
        var picks   = work.OrderBy(PickOrder).ThenByDescending(CameUp).ThenBy(i => i.Row.Rank ?? int.MaxValue).Take(PickCount).ToList();

        return new(
            waiting,
            picks,
            [.. work.Except(picks)],
            [.. all.Where(i => i.Kind == NextWorkFocusKind.Note)],
            [.. all.Where(i => i.Kind == NextWorkFocusKind.Quiet)]);
    }

    sealed record Draft(NextWorkRow Row, List<NextWorkFocusEnd> Ends) {
        public List<NextWorkRow> Folded { get; } = [];
        public List<NextWorkRow> Same   { get; } = [];
    }

    static List<NextWorkFocusEnd> OwnEnds(NextWorkRow row, Tokens tokens, DateTimeOffset now) {
        var ends = new List<NextWorkFocusEnd>();
        foreach (var r in NextWorkLaneLogic.LooseEnds(row, now)) {
            var i = ends.FindIndex(e => tokens.Similar(e.Text, r.Text));
            if (i >= 0) ends[i] = ends[i] with { Repeats = ends[i].Repeats + 1 };
            else ends.Add(r);
        }
        return ends;
    }

    static (Draft Draft, int Index)? FindSimilar(IEnumerable<Draft> drafts, string text, Tokens tokens) {
        foreach (var d in drafts)
            for (var i = 0; i < d.Ends.Count; i++)
                if (tokens.Similar(d.Ends[i].Text, text)) return (d, i);
        return null;
    }

    static NextWorkFocusItem Finish(Draft d) => new(d.Row, KindOf(d.Row), d.Ends, d.Folded, d.Same);

    static NextWorkFocusKind KindOf(NextWorkRow row) {
        // Someone waiting on the viewer is never quiet or a chore, however long they have waited.
        if (NextWorkLaneLogic.GroupOf(row) == NextWorkGroup.Waiting) return NextWorkFocusKind.Work;

        var days = NextWorkLaneLogic.WholeDays(row.Facets.StalenessDays);

        if (row.TargetKind == "loose_end") {
            if (days >= QuietLooseEndDays) return NextWorkFocusKind.Quiet;
            return Chore().IsMatch(NextWorkLaneLogic.DisplayLabel(row)) ? NextWorkFocusKind.Note : NextWorkFocusKind.Work;
        }

        return row.TargetKind == "work_item" && !Tracked(row) && days >= QuietDays ? NextWorkFocusKind.Quiet : NextWorkFocusKind.Work;
    }

    static int CameUp(NextWorkFocusItem i) => i.Ends.Count == 0 ? 0 : i.Ends.Max(e => e.Repeats);

    // Assigned issues lead, bugs first; then notes that keep coming up; then the engine's order.
    static int PickOrder(NextWorkFocusItem i) =>
        Assigned(i.Row) ? (i.Row.Facets.IsBug ? 0 : 1)
      : i.Ends.Any(e => e.Repeats >= 1) ? 2
      : 3;

    [GeneratedRegex(@"^\s*(?<key>[A-Z][A-Z0-9]+-\d+)\b")]
    private static partial Regex Key();

    /// <summary>Anything shaped like an issue key; which of them is one is decided by the items in the band.</summary>
    [GeneratedRegex(@"\b(?<key>[A-Za-z][A-Za-z0-9]+-\d+)\b")]
    private static partial Regex Mention();

    /// <summary>Leading words that make a loose end a tracker chore rather than work.</summary>
    [GeneratedRegex(@"^\s*(?:(?:file|open)\s+(?:an?\s+)?(?:new\s+)?(?:small\s+|separate\s+)?issue\b|file\s|add evidence\b|update\s+[a-z]+-\d+\b|post\s|review\s|decide\s|ask\s|install\s)", RegexOptions.IgnoreCase)]
    private static partial Regex Chore();

    static string? IssueKey(NextWorkRow row) =>
        row.TargetKind == "work_item" && Key().Match(NextWorkLaneLogic.DisplayLabel(row)) is { Success: true } m ? m.Groups["key"].Value : null;

    static IEnumerable<string> Mentions(string text) => Mention().Matches(text).Select(m => m.Groups["key"].Value);

    static string? LooseEndId(NextWorkRow row) =>
        row.TargetId ?? (row.TargetKey.StartsWith(NextWorkLaneLogic.LooseEndPrefix, StringComparison.Ordinal)
            ? row.TargetKey[NextWorkLaneLogic.LooseEndPrefix.Length..] : null);

    static int Repeats(NextWorkRow row) => Math.Max(1, row.Facets.SightingCount);

    static bool Tracked(NextWorkRow row) =>
        row.Evidence.Select(e => NextWorkLaneLogic.TrackerState(e.Kind, e.Summary)).FirstOrDefault(s => s is not null) is { StateType: not "unenriched" };

    static bool Assigned(NextWorkRow row) =>
        row.Evidence.Any(e => e.Kind == "identity" && e.Summary.Contains("assigned to you", StringComparison.OrdinalIgnoreCase));

    static readonly HashSet<string> Stop = new(StringComparer.Ordinal) {
        "the", "a", "an", "to", "and", "or", "of", "in", "on", "for", "with", "when", "is", "it", "its", "not", "only", "any",
        "so", "that", "from", "by", "as", "be", "this",
    };

    [GeneratedRegex("[A-Za-z0-9][A-Za-z0-9._/-]*")]
    private static partial Regex Word();

    /// <summary>Notes shorter than this are the same only when they read the same.</summary>
    const int SimilarMinWords = 5;

    /// <summary>Two notes say the same thing when most of the shorter one's words are in the longer one, neither is
    /// more than twice the other's length, and they name the same things (a word with a digit, an inner capital, or a
    /// dot, slash or underscore).</summary>
    public static bool Similar(string a, string b) => new Tokens().Similar(a, b);

    sealed class Tokens {
        readonly Dictionary<string, (HashSet<string> Words, HashSet<string> Names)> _seen = new(StringComparer.Ordinal);

        (HashSet<string> Words, HashSet<string> Names) Of(string text) {
            if (_seen.TryGetValue(text, out var t)) return t;

            var words = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in Word().Matches(text).Select(m => m.Value.TrimEnd('.', '/', '-', '_'))) {
                var lower = w.ToLowerInvariant();
                if (lower.Length == 0 || Stop.Contains(lower)) continue;
                words.Add(lower);
                if (IsName(w)) names.Add(lower);
            }
            return _seen[text] = (words, names);
        }

        static bool IsName(string w) =>
            w.Any(char.IsDigit) || w.IndexOfAny(['.', '/', '_']) >= 0 || w.Skip(1).Any(char.IsUpper);

        public bool Similar(string a, string b) {
            var (x, y) = (Of(a), Of(b));
            if (Math.Min(x.Words.Count, y.Words.Count) < SimilarMinWords) return x.Words.SetEquals(y.Words) && x.Words.Count > 0;
            if (Math.Max(x.Words.Count, y.Words.Count) > 2 * Math.Min(x.Words.Count, y.Words.Count)) return false;
            if (!x.Names.SetEquals(y.Names)) return false;
            return (double)x.Words.Intersect(y.Words).Count() / Math.Min(x.Words.Count, y.Words.Count) >= 0.6;
        }
    }
}
