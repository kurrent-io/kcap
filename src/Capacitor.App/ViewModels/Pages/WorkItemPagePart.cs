using System.Globalization;
using System.Reactive;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// One slice's row. <see cref="Tip"/> names what a blocked slice waits on. <see cref="NeedsYou"/> comes from the
/// viewer's own sessions, so it never moves the slice between groups.
public sealed class WorkItemPagePart : ReactiveObject {
    public string Id { get; }
    public string Title { get; }
    public WorkStatus Status { get; }
    public string? Tip { get; }
    public bool NeedsYou { get; }
    public IReadOnlyList<WorkItemPageLink> Links { get; }
    public IReadOnlyList<WorkItemPageSession> Sessions { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public ReactiveCommand<string, Unit> SetStatusCommand { get; }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public WorkItemPagePart(string id, string title, WorkStatus status, string? tip, bool needsYou,
            IReadOnlyList<WorkItemPageLink> links, IReadOnlyList<WorkItemPageSession> sessions, bool isExpanded,
            Action<bool> expandedChanged, ReactiveCommand<string, Unit> setStatusCommand) {
        Id = id;
        Title = title;
        Status = status;
        Tip = tip;
        NeedsYou = needsYou;
        Links = links;
        Sessions = sessions;
        _isExpanded = isExpanded;
        SetStatusCommand = setStatusCommand;
        ToggleCommand = ReactiveCommand.Create(() => {
            IsExpanded = !IsExpanded;
            expandedChanged(IsExpanded);
        });
    }

    public LucideIconKind Icon => Status.Icon();
    public string StatusLabel => Status.Label();
    public bool HasSessions => Sessions.Count > 0;
    public bool HasLinks => Links.Count > 0;
    int Prs => Links.Count(link => link.IsPr);
    int Merged => Links.Count(link => link.IsPr && link.IsMerged);
    int Issues => Links.Count(link => !link.IsPr);
    public bool HasPrs => Prs > 0;
    public bool HasIssues => Issues > 0;
    public string PrSummary => $"{Merged}/{Prs}";
    public string IssueSummary => Issues.ToString(CultureInfo.InvariantCulture);
    public string LinkTip => string.Join(" · ", new[] {
        Prs switch {
            0 => null,
            1 => Merged == 1 ? "1 PR, merged" : "1 PR, none merged",
            _ => $"{Prs} PRs, {(Merged == 0 ? "none" : Merged)} merged",
        },
        Issues switch { 0 => null, 1 => "1 issue", _ => $"{Issues} issues" },
    }.OfType<string>());
}
