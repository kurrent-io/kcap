using System.Globalization;
using System.Reactive;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// One slice's row. <see cref="Tip"/> names what a blocked slice waits on. <see cref="NeedsYou"/> comes from the
/// viewer's own sessions, so it never moves the slice between groups. <see cref="Arrives"/> when it was just added.
public sealed class WorkItemPagePart : ReactiveObject {
    public string Id { get; }
    public string Title { get; }
    public WorkStatus Status { get; }
    public string? Tip { get; }
    public bool NeedsYou { get; }
    public bool Arrives { get; }
    /// Shown closed, to be opened once on screen so it unfolds.
    public bool Opens { get; }
    public IReadOnlyList<WorkItemPageLink> Links { get; }
    public IReadOnlyList<WorkItemPageSession> Sessions { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public ReactiveCommand<string, Unit> SetStatusCommand { get; }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public WorkItemPagePart(string id, string title, WorkStatus status, string? tip, bool needsYou,
            IReadOnlyList<WorkItemPageLink> links, IReadOnlyList<WorkItemPageSession> sessions, bool isExpanded,
            Action<bool> expandedChanged, ReactiveCommand<string, Unit> setStatusCommand, bool arrives = false, bool opens = false) {
        Id = id;
        Title = title;
        Status = status;
        Tip = tip;
        NeedsYou = needsYou;
        Arrives = arrives;
        Opens = opens;
        Links = links;
        Sessions = sessions;
        _isExpanded = isExpanded;
        SetStatusCommand = setStatusCommand;
        _expandedChanged = expandedChanged;
        ToggleCommand = ReactiveCommand.Create(() => {
            if (HasSessions) Expand(!IsExpanded);
        });
    }

    readonly Action<bool> _expandedChanged;

    public void Open() {
        if (!IsExpanded) Expand(true);
    }

    void Expand(bool expanded) {
        IsExpanded = expanded;
        _expandedChanged(expanded);
    }

    public LucideIconKind Icon => Status.Icon();
    public bool HasSessions => Sessions.Count > 0;
    public string SessionSummary => Sessions.Count == 1 ? "1 session" : $"{Sessions.Count} sessions";
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
