using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// What a session's PR card last showed: its links, the PR the user picked (null on the default),
/// the lifecycles its overviews reported, and the overview while its access lasts.
public sealed record PullRequestSnapshot(
    IReadOnlyList<PullRequestLinkDto> Links,
    PullRequestSubjectDto? Picked,
    IReadOnlyDictionary<PullRequestSubjectDto, string> Lifecycles,
    PullRequestRead<PullRequestOverviewDto>? Overview);
