using Capacitor.App.Services;
using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// A GitHub pull request or issue link as a chip: its glyph, `owner/repo#n`, and the state word once
/// it is known. Until then, and when it cannot be had, the chip is the neutral glyph and label alone.
public sealed record GitHubLinkChipViewModel(GitHubLink Link, GitHubLinkState? State) {
    bool IsPullRequest => Link.Kind == GitHubLinkKind.PullRequest;

    /// An issue is only ever Open or Closed, whatever the item turned out to be.
    GitHubLinkState? Shown => IsPullRequest ? State : State switch {
        GitHubLinkState.Draft => GitHubLinkState.Open,
        GitHubLinkState.Merged => GitHubLinkState.Closed,
        _ => State,
    };

    /// The pull request reader's lifecycle status, whose glyphs and tones the chip shares.
    PullRequestStatus? ReaderStatus => !IsPullRequest ? null
        : StatusText is { } text ? new PullRequestStatus(text, text.ToLowerInvariant()) : new PullRequestStatus("Unknown");

    public string Label => Link.Label;

    public string Url => Link.Url;

    public string? StatusText => Shown switch {
        GitHubLinkState.Open => "Open",
        GitHubLinkState.Draft => "Draft",
        GitHubLinkState.Merged => "Merged",
        GitHubLinkState.Closed => "Closed",
        _ => null,
    };

    public bool HasStatus => StatusText is not null;

    public LucideIconKind Glyph => ReaderStatus switch {
        null => LucideIconKind.CircleDot,
        { IconKind: { } kind } => kind,
        { IsClosed: true } => LucideIconKind.CircleX,
        _ => LucideIconKind.GitPullRequest,
    };

    public bool IsOpen => Shown == GitHubLinkState.Open;

    public bool IsMerged => ReaderStatus?.IsMerged == true;

    public bool IsClosed => ReaderStatus?.IsClosed == true;

    public string AccessibleName => $"{(IsPullRequest ? "Pull request" : "Issue")} {Label}{(HasStatus ? ", " + StatusText : "")}";
}
