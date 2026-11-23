using Capacitor.App.Services;
using Capacitor.App.ViewModels.Pages;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// A place the main pane showed: a page when `Page` is set, the launcher when `AgentId` is null,
/// otherwise a session and the tab it was on. `Pin` is set when the session was on the rail only
/// through a search pin, so returning to it pins it again.
public sealed record NavigationEntry(
    string? AgentId,
    string? SessionId,
    bool Terminal,
    bool PullRequest,
    bool Artefacts,
    PullRequestSubjectDto? Subject,
    PinPresentation? Pin,
    PageKey? Page) {
    public static NavigationEntry Launcher { get; } = new(null, null, false, false, false, null, null, null);

    public static NavigationEntry ForPage(PageKey page) => new(null, null, false, false, false, null, null, page);

    public bool IsLauncher => AgentId is null && Page is null;
}
