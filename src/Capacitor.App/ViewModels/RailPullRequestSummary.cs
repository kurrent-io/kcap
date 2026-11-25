using System.Globalization;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record RailPullRequestMenuItem(PullRequestSubjectDto Subject, string? Title, string Lifecycle) {
    public PullRequestEvidence? Evidence { get; init; }
    public bool IsLive => Lifecycle is "Open" or "Draft";
    public bool IsIncomplete => Lifecycle == "Unknown" || (IsLive && (Evidence is null or { Incomplete: true }));
    public bool RequiresAttention => IsLive && Evidence?.Signals.Any(status => status.RequiresAttention) == true;
    public IEnumerable<PullRequestStatus> Signals => IsLive ? Evidence?.Signals ?? [new("Checks unavailable")] : [];
    public string Label => string.IsNullOrWhiteSpace(Title) ? $"PR #{Subject.Number}" : Title;
    public string Repository => $"{Subject.Owner}/{Subject.RepoName}";
    public PullRequestStatus Status => new(Lifecycle, Lifecycle.ToLowerInvariant());
    public KcapTip PickerTip => KcapTip.PullRequest(Subject.Number, Title, Status, Repository,
        signals: Evidence?.Stale == true ? [.. Signals, PullRequestEvidence.EarlierSnapshot] : Signals.ToArray());
    public string Detail => $"{Subject.Owner}/{Subject.RepoName} #{Subject.Number} · {Lifecycle}";
}

public sealed record RailPullRequestSummary(IReadOnlyList<RailPullRequestMenuItem> MenuItems) {
    public bool IncompleteCoverage { get; init; }
    RailPullRequestMenuItem? SingleItem => MenuItems.Count == 1 ? MenuItems[0] : null;
    public int Count => MenuItems.Count;
    public PullRequestSubjectDto? Subject => SingleItem?.Subject;
    public string Lifecycle => SingleItem?.Lifecycle ?? "Unknown";
    public string Label => SingleItem is { } item
        ? $"PR #{item.Subject.Number.ToString(CultureInfo.InvariantCulture)}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} PRs";

    public PullRequestStatus Status => SingleItem?.Status ?? new("Unknown", "unknown");
    public IEnumerable<PullRequestStatus> PreviewSignals {
        get {
            if (SingleItem is { } item) {
                yield return item.Status;
                if (item.IsLive)
                    foreach (var signal in item.Signals.Where(status => status.RequiresAttention
                            || status.PreviewGlyph is StatusGlyphKind.Activity or StatusGlyphKind.Pending or StatusGlyphKind.Unknown))
                        yield return signal;
                if (IncompleteCoverage || item.IsIncomplete) yield return new("Status incomplete");
                if (item.Evidence?.Stale == true) yield return PullRequestEvidence.EarlierSnapshot;
            } else {
                var attention = MenuItems.Count(item => item.RequiresAttention);
                var incomplete = IncompleteCoverage || MenuItems.Any(item => item.IsIncomplete);
                var lowerBound = IncompleteCoverage || MenuItems.Any(item => item.IsIncomplete && !item.RequiresAttention);
                if (attention > 0) yield return new($"{attention}{(lowerBound ? "+" : "")} need attention", "warning");
                if (incomplete) yield return new("Status incomplete");
                if (MenuItems.Any(item => item.Evidence?.Stale == true)) yield return PullRequestEvidence.EarlierSnapshot;
            }
        }
    }
    public bool HasLifecycle => SingleItem is not null;
    public string Description => SingleItem is { } item
        ? $"Pull request #{item.Subject.Number.ToString(CultureInfo.InvariantCulture)} · {item.Lifecycle}\n{item.Subject.Owner}/{item.Subject.RepoName}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} pull requests";
}
