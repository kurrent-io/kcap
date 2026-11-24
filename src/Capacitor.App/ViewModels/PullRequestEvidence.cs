using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record PullRequestEvidence(PullRequestStatus Checks, PullRequestStatus Review,
        PullRequestStatus Conflicts, PullRequestStatus Threads, bool Incomplete, bool Stale = false) {
    internal static PullRequestStatus EarlierSnapshot { get; } = new("Earlier snapshot") { PreviewOverride = StatusGlyphKind.Stale };

    public static PullRequestEvidence From(PullRequestOverviewDto overview, bool stale) {
        var checks = PullRequestTones.ChecksStatus(overview);
        var review = PullRequestTones.ReviewStatus(overview);
        var threads = PullRequestTones.ThreadsStatus(overview);
        var live = overview.Lifecycle is "open" or "draft";
        return new(checks, review,
            live && overview.Mergeable == false ? new("Merge conflicts", "conflict") : new(""),
            threads,
            (overview.Lifecycle is not ("open" or "draft" or "merged" or "closed"))
            || (live && (checks.PreviewGlyph == StatusGlyphKind.Unknown
                || review.PreviewGlyph == StatusGlyphKind.Unknown || overview.Mergeable is null || threads.Text.Length == 0)),
            stale);
    }

    public IEnumerable<PullRequestStatus> Signals {
        get {
            if (Checks.Text.Length > 0) yield return Checks;
            if (Conflicts.Text.Length > 0) yield return Conflicts;
            if (Review.Text.Length > 0) yield return Review;
            if (Threads.Text.Length > 0) yield return Threads;
        }
    }
}
