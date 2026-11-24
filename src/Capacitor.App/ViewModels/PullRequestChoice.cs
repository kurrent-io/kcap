using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record PullRequestChoice(PullRequestLinkDto Link, bool IsAvailable = true) {
    public PullRequestSubjectDto Subject { get; } = PullRequestWire.Subject(Link);
    public string Label => $"{Link.Owner}/{Link.RepoName} #{Link.Number}" + (IsAvailable ? "" : " · Unavailable");
    public PullRequestStatus? LinkedLifecycle => Link.Lifecycle?.ToLowerInvariant() switch {
        "open" => new("Open", "open"),
        "draft" => new("Draft", "draft"),
        "merged" => new("Merged", "merged"),
        "closed" => new("Closed", "closed"),
        _ => null,
    };
    public KcapTip ChoiceTip => KcapTip.PullRequest(Link.Number, IsAvailable ? Link.Title : null,
        IsAvailable ? LinkedLifecycle : null, $"{Link.Owner}/{Link.RepoName}",
        IsAvailable ? null : KcapTipDetail.Note("Pull request unavailable"));
    public override string ToString() => Label;
}
