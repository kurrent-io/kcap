using System.Reactive;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One part's card. <see cref="Detail"/> is its second line: what a blocked part waits on, its links, or that it has
/// none. <see cref="NeedsYou"/> comes from the viewer's own sessions, so it never moves the part between groups.
public sealed record WorkItemPagePart(
        string Id, string Title, WorkStatus Status, string Detail, bool DetailIsWaiting, bool NeedsYou,
        IReadOnlyList<WorkItemPageSession> Sessions, ReactiveCommand<string, Unit> SetStatusCommand) {
    public LucideIconKind Icon => Status.Icon();
    public string StatusLabel => Status.Label();
    public bool HasSessions => Sessions.Count > 0;
    public string SessionCountText => Sessions.Count == 1 ? "1 session" : $"{Sessions.Count} sessions";
}
