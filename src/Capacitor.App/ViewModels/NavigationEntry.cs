using Capacitor.App.Services;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// A place the main pane showed: the launcher when `AgentId` is null, otherwise a session and the
/// tab it was on. `Pin` is set when the session was on the rail only through a search pin, so
/// returning to it pins it again.
public sealed record NavigationEntry(
    string? AgentId,
    string? SessionId,
    bool Terminal,
    bool PullRequest,
    bool Artefacts,
    PullRequestSubjectDto? Subject,
    PinPresentation? Pin) {
    public static NavigationEntry Launcher { get; } = new(null, null, false, false, false, null, null);

    public bool IsLauncher => AgentId is null;
}
