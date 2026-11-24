using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public static class PullRequestTones {
    /// A closed or merged PR is past its checks; among live ones a failure outranks a conflict,
    /// which outranks a run in progress, which outranks the draft marker.
    public static PullRequestTone From(PullRequestOverviewDto overview) {
        switch (overview.Lifecycle) {
            case "merged": return PullRequestTone.Merged;
            case "closed": return PullRequestTone.Closed;
            case "open" or "draft": break;
            default: return PullRequestTone.None;
        }
        var rollup = overview.Checks?.Availability.Status == "ready" ? overview.Checks.Rollup : null;
        if (rollup == "failure") return PullRequestTone.ChecksFailed;
        if (overview.Mergeable == false) return PullRequestTone.Conflict;
        if (rollup == "pending") return PullRequestTone.ChecksRunning;
        if (overview.Lifecycle == "draft" || overview.IsDraft == true) return PullRequestTone.Draft;
        return PullRequestTone.Ready;
    }

    /// The tone of a set of PRs is the one most in need of attention, which the enum's order encodes.
    public static PullRequestTone Strongest(IEnumerable<PullRequestTone> tones) {
        var strongest = PullRequestTone.None;
        foreach (var tone in tones) if (tone > strongest) strongest = tone;
        return strongest;
    }

    public static string Label(PullRequestTone tone) => tone switch {
        PullRequestTone.Ready => "Ready to merge",
        PullRequestTone.Draft => "Draft",
        PullRequestTone.ChecksRunning => "Checks running",
        PullRequestTone.ChecksFailed => "Checks failed",
        PullRequestTone.Conflict => "Merge conflicts",
        PullRequestTone.Merged => "Merged",
        PullRequestTone.Closed => "Closed",
        _ => "",
    };

    /// The lifecycle word alone: conflicts have their own status row beside checks and reviews.
    public static PullRequestStatus LifecycleStatus(PullRequestOverviewDto? overview) => overview is null ? new("Unknown") : overview.Lifecycle switch {
        "draft" => new("Draft", "draft"),
        "open" when overview.IsDraft == true => new("Draft", "draft"),
        "open" => new("Open", "open"),
        "merged" => new("Merged", "merged"),
        "closed" => new("Closed", "closed"),
        _ => new("Unknown"),
    };

    internal static PullRequestStatus ReviewStatus(PullRequestOverviewDto? overview) => overview?.ReviewDecision switch {
        "approved" => new("Approved", "success", "All required reviews are approved."),
        "changes_requested" => new("Changes requested", "failure", "A reviewer requested changes before merge.") {
            PreviewOverride = StatusGlyphKind.Attention,
        },
        "review_required" => new("Review required", "warning", "Waiting on a required review before this PR can merge."),
        _ => new("Review decision unknown", "neutral", "GitHub has not reported a review decision yet."),
    };

    internal static PullRequestStatus ChecksStatus(PullRequestOverviewDto? overview) =>
        overview?.Checks?.Availability.Status != "ready"
            ? new("Checks unavailable", Detail: "Checks could not be loaded for this pull request.")
            : overview.Checks.Rollup switch {
                "success" => new("Checks passing", "success", "All checks have passed."),
                "failure" => new("Checks failing", "failure", "One or more checks failed."),
                "pending" => PendingChecksStatus(Progress(overview.Checks.Counts)),
                _ => new("Checks unknown", Detail: "GitHub has not reported a check summary yet."),
            };

    internal static PullRequestStatus PendingChecksStatus(string progress, bool running = false, string? counts = null) {
        var detail = counts is null ? "." : $" ({counts}).";
        return new("Checks pending" + progress, "pending", "Checks are still running" + detail) {
            PreviewOverride = running ? StatusGlyphKind.Activity : null,
            PreviewDetail = running ? null : "Checks are pending" + detail,
        };
    }

    internal static PullRequestStatus ThreadsStatus(PullRequestOverviewDto? overview) =>
        overview?.Threads is { Availability.Status: "ready", Unresolved: { Value: { } count } unresolved }
            ? count == 0
                ? unresolved.Kind == "exact" ? new("No unresolved threads", "success", "Every review thread is resolved.") : new("")
                : new($"{count}{(unresolved.Kind == "exact" ? "" : "+")} unresolved thread{(count == 1 && unresolved.Kind == "exact" ? "" : "s")}",
                      "warning", "Review threads are still waiting on a reply or a fix.")
            : new("");

    static string Progress(Dictionary<string, PullRequestCountDto>? counts) {
        if (counts is not { Count: > 0 } || counts.ContainsKey("unknown") || counts.Values.Any(count => count is not { Kind: "exact", Value: not null })) return "";
        var total = counts.Values.Sum(count => count.Value!.Value);
        var done = total - (counts.GetValueOrDefault("pending")?.Value ?? 0);
        return $" · {done}/{total}";
    }
}
