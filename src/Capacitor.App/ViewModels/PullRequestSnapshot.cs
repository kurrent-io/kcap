using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// What a session's PR card last showed: its links, the PR the user picked (null on the default),
/// the PRs its overviews reported merged, and the overview while its access lasts.
public sealed record PullRequestSnapshot(
    IReadOnlyList<PullRequestLinkDto> Links,
    PullRequestSubjectDto? Picked,
    IReadOnlySet<PullRequestSubjectDto> Merged,
    PullRequestRead<PullRequestOverviewDto>? Overview);
