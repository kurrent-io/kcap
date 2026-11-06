using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>How the band reads one engine row: its group, glyph, marks, receipts and wording. Mirrors the web Home
/// band so both surfaces say the same thing about the same row.</summary>
public static partial class NextWorkLaneLogic {
    public const string WorkItemPrefix = "wi:";
    public const string LooseEndPrefix = "le:";

    public static IReadOnlyList<NextWorkRow> Kept(NextWorkSnapshot? snapshot) =>
        snapshot is null ? [] : [.. snapshot.Candidates.Where(c => c.Kept && c.Rank is not null).OrderBy(c => c.Rank)];

    public static NextWorkGroup? GroupOf(NextWorkRow c) => c.Tier switch {
        1 => NextWorkGroup.Waiting,
        2 => NextWorkGroup.Finish,
        3 => NextWorkGroup.PickUp,
        _ => null,
    };

    /// <summary>Which rows get a launch action: kept work items and loose ends. Review, resume and housekeeping rows
    /// have their own actions.</summary>
    public static bool IsLaunchable(NextWorkRow c) => c.Kept && c.TargetKind is "work_item" or "loose_end";

    public static NextWorkGlyph Glyph(NextWorkRow c) {
        var done = c.Disconfirmed;
        var tint = done ? NextWorkGroup.Done : GroupOf(c) ?? NextWorkGroup.All;

        return c.TargetKind switch {
            "pr" when done && c.DisconfirmedReason == "pr_merged" => new("merge", tint, null),
            "pr"        => new("pr", tint, done ? null : c.Arm switch { "ReviewRequested" => "wait", "ApprovedUnmerged" => "ok", _ => null }),
            "session"   => new("session", tint, !done && c.Arm == "ResumeInterrupted" ? "stop" : null),
            "loose_end" => new("loose_end", tint, null),
            "evals"     => new("evals", tint, null),
            _           => new(c.Facets.IsBug ? "bug" : "issue", tint, !done && c.Arm == "BlocksOthers" ? "wait" : null),
        };
    }

    /// <summary>What a row shows without being opened: the tracker priority and whether it is a bug.</summary>
    public static IReadOnlyList<string> RowMarks(NextWorkRow c) {
        var marks = new List<string>(2);
        if (PriorityLabel(c.Facets.TrackerPriority) is { } p) marks.Add(p);
        if (c.Facets.IsBug) marks.Add("Bug");
        return marks;
    }

    /// <summary>Linear's priority number as its name: 1 is the most urgent, 4 the least, 0 none.</summary>
    public static string? PriorityLabel(int? priority) => priority switch {
        1 => "Urgent",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => null,
    };

    public static NextWorkTone PriorityTone(string? label) => label switch {
        "Urgent" => NextWorkTone.Danger,
        "High"   => NextWorkTone.Warning,
        _        => NextWorkTone.Neutral,
    };

    public static NextWorkTone MarkTone(string mark) => mark == "Bug" ? NextWorkTone.Danger : PriorityTone(mark);

    [GeneratedRegex(@"^(?:\S+\s+\S+:\s+)?state\s+(?<name>.+?)\s+\((?<type>[a-z_]+)\),\s+priority\s+(?<prio>\d+|none)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackerStateText();

    /// <summary>A tracker-state receipt's text read back into its parts, or null when it is not one.</summary>
    public static NextWorkTrackerState? TrackerState(string kind, string text) {
        if (kind != "tracker_state") return null;
        var m = TrackerStateText().Match(text);
        if (!m.Success) return null;

        var name = m.Groups["name"].Value;
        int? prio = int.TryParse(m.Groups["prio"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new(string.Equals(name, "unknown", StringComparison.OrdinalIgnoreCase) ? null : name, m.Groups["type"].Value, prio);
    }

    /// <summary>When a loose end was noted, from the staleness facet: "Noted today", "Noted 4d ago".</summary>
    public static string NotedAgo(NextWorkRow c) => Age(c) is var a && a == "today" ? "Noted today" : $"Noted {a} ago";

    /// <summary>How long the target has been quiet, from the staleness facet.</summary>
    public static string Age(NextWorkRow c) => WholeDays(c.Facets.StalenessDays) is var d and > 0 ? $"{d}d" : "today";

    /// <summary>Whole days in a staleness facet; a non-finite or negative value reads as none.</summary>
    public static int WholeDays(double days) => double.IsFinite(days) && days >= 1 ? (int)Math.Min(days, 9999) : 0;

    /// <summary>The chip a row carries: only where the arm says more than the group header.</summary>
    public static string? ArmChip(NextWorkRow c) => c.Arm switch {
        "BlocksOthers"      => "Blocks others",
        "ResumeInterrupted" => "Resume",
        _                   => null,
    };

    /// <summary>The loose ends a row's evidence names, as its focus item lists them: each one's text, when it was last
    /// noted and the session it was noted in, when that is not the row's own session. A loose end's own row, whose
    /// excerpt is its label, says only when it was noted.</summary>
    public static IReadOnlyList<NextWorkFocusEnd> LooseEnds(NextWorkRow row, DateTimeOffset now) =>
        [.. row.Evidence.Where(e => e.Kind == "loose_end")
            .Select(e => {
                var (text, note) = LooseEndText(e, row.TargetLabel, now);
                var session = e.SessionId is { Length: > 0 } id && id != RowSessionId(row) ? id : null;
                return new NextWorkFocusEnd(e.LooseEndId, text, session, note, 0);
            })
            .DistinctBy(e => (e.Text, e.Noted, e.SessionId, e.LooseEndId))];

    public static string? RowSessionId(NextWorkRow row) =>
        row.TargetKind != "session" ? null
        : row.TargetId ?? (row.TargetKey.StartsWith("session:", StringComparison.Ordinal) ? row.TargetKey["session:".Length..] : null);

    // The engine's loose-end summary is a trace; the receipt keeps only when it was last noted. Its excerpt names the
    // loose end, which a loose end's own row already does as its label.
    static (string Text, string? Note) LooseEndText(NextWorkEvidence e, string rowLabel, DateTimeOffset now) {
        var stamps = UtcStamp().Matches(e.Summary);
        var noted  = stamps.Count > 0 && TryStamp(stamps[^1].Value, out var at) ? $"Noted {Relative(at, now)}" : "Noted";

        return e.Excerpt?.Trim() is { Length: > 0 } excerpt && !string.Equals(excerpt, rowLabel.Trim(), StringComparison.OrdinalIgnoreCase)
            ? (excerpt, noted)
            : (noted, null);
    }

    /// <summary>The because-clause; when it is blank or only repeats the row's label, what the facets can say instead.</summary>
    public static string? Reason(NextWorkRow c) => BecauseIsLabel(c) ? FacetReason(c) : c.Because.Trim();

    /// <summary>The because-clause says nothing of its own: blank, or only the row's label again.</summary>
    public static bool BecauseIsLabel(NextWorkRow c) {
        var because = c.Because.Trim();
        return because.Length == 0 || string.Equals(because, DisplayLabel(c), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A loose end's because-clause is its own text, so its row says how it was seen.</summary>
    static string? FacetReason(NextWorkRow c) {
        if (c.TargetKind != "loose_end") return null;

        var f     = c.Facets;
        var seen  = f.SightingCount > 1 ? $"Sighted {f.SightingCount} times" : "Sighted once";
        var quiet = WholeDays(f.StalenessDays) is var d and > 0 ? $"quiet {d}d" : "today";
        return $"{seen}, {quiet}";
    }

    [GeneratedRegex(@",\s*active\s+(?:\d+\s+days?\s+ago|today|yesterday)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveAgo();

    /// <summary>The engine ends some reasons with how long ago the work was active, which the facts line already shows.</summary>
    public static string? WithoutAge(string? why) => why is null ? null : ActiveAgo().Replace(why, "");

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z")]
    private static partial Regex UtcStamp();

    static readonly string[] UtcStampFormats = [
        "yyyy-MM-dd HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    static bool TryStamp(string value, out DateTimeOffset at) =>
        DateTimeOffset.TryParseExact(value, UtcStampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);

    /// <summary>Seconds, minutes, hours, then days ago, as the web band counts them.</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now) {
        var delta = now - when;
        return delta.TotalSeconds < 60 ? $"{Math.Max(0, (int)delta.TotalSeconds)}s ago"
            :  delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes}m ago"
            :  delta.TotalHours   < 24 ? $"{(int)delta.TotalHours}h ago"
            :                            $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>What settled lately, as the web's "Already done" counts it: one bucket per target, the strongest reason
    /// winning, so a merged PR whose review request was also cleared is one PR merged. A target another arm still
    /// keeps is not done.</summary>
    public static IReadOnlyList<string> RecentlyDone(NextWorkSnapshot snapshot) {
        var kept = snapshot.Candidates.Where(c => c.Kept).Select(c => c.TargetKey).ToHashSet(StringComparer.Ordinal);
        var buckets = snapshot.Candidates
            .Where(c => c.Disconfirmed && c.DisconfirmedReason is not null && !kept.Contains(c.TargetKey))
            .GroupBy(c => c.TargetKey, StringComparer.Ordinal)
            .Select(g => g.Select(c => DoneBucket(c.DisconfirmedReason)).Min())
            .ToList();

        int Of(int bucket) => buckets.Count(b => b == bucket);
        var parts = new List<string>(4);
        if (Of(0) is var merged and > 0)    parts.Add(Plural(merged, "PR merged", "PRs merged"));
        if (Of(1) is var settled and > 0)   parts.Add(Plural(settled, "item settled", "items settled"));
        if (Of(2) is var withdrawn and > 0) parts.Add(Plural(withdrawn, "review request withdrawn", "review requests withdrawn"));
        if (Of(3) is var ended and > 0)     parts.Add(Plural(ended, "session wrapped up", "sessions wrapped up"));
        return parts;
    }

    static int DoneBucket(string? reason) => reason switch {
        "pr_merged"                                                                                                    => 0,
        "item_settled" or "subject_settled" or "tracker_state_terminal" or "blocked_item_settled" or "loose_end_closed" => 1,
        "review_request_withdrawn"                                                                                     => 2,
        "session_ended"                                                                                                => 3,
        _                                                                                                              => int.MaxValue,
    };

    static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    public static IReadOnlySet<string> NewKeys(IEnumerable<NextWorkRow> rows, IReadOnlySet<string>? previous) =>
        previous is null
            ? FrozenSet<string>.Empty
            : rows.Select(r => r.TargetKey).Where(k => !previous.Contains(k)).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The end a loose-end row is, or null for any other row.</summary>
    public static string? LooseEndIdOf(NextWorkRow row) =>
        row.TargetKey.StartsWith(LooseEndPrefix, StringComparison.Ordinal) && row.TargetKey.Length > LooseEndPrefix.Length
            ? row.TargetKey[LooseEndPrefix.Length..]
            : null;

    /// <summary>A target with no seed issue link carries its raw id as its label, a 32-character hash as a title, so
    /// that case reads as its kind plus a short id.</summary>
    public static string DisplayLabel(NextWorkRow c) => DisplayLabel(c.TargetLabel, c.TargetKind, c.TargetId);

    public static string DisplayLabel(string rawLabel, string kind, string? targetId) {
        var label = rawLabel.Trim();
        if (label.Length > 0 && !IsRawId(label, targetId)) return label;

        var id = targetId is { Length: > 0 } t ? t : label;
        return id.Length == 0 ? KindName(kind) : $"{KindName(kind)} {id[..Math.Min(8, id.Length)]}";
    }

    static bool IsRawId(string label, string? targetId) =>
        string.Equals(label, targetId, StringComparison.Ordinal) || (label.Length == 32 && label.All(Uri.IsHexDigit));

    static string KindName(string kind) => kind switch {
        "work_item" => "Work item",
        "session"   => "Session",
        "loose_end" => "Loose end",
        "pr"        => "Pull request",
        "evals"     => "Evals",
        _           => "Item",
    };

    /// <summary>The repository name alone; the owner is on the row's link. An unknown hash reads as its short prefix.</summary>
    public static string? ShortRepo(IReadOnlyDictionary<string, string> names, string? hash) {
        if (hash is null) return null;
        var name = names.TryGetValue(hash, out var n) ? n : hash.Length > 8 ? hash[..8] : hash;
        return name[(name.LastIndexOf('/') + 1)..];
    }
}
