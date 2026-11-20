using System.Reactive;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// A collapsible group of a work item page: the parts of one status, the viewer's loose
/// ends, or the sessions on no part. <see cref="Status"/> is null except on a status group, the only kind that
/// <see cref="IsComposing"/>: showing the field a new slice in its status is typed into.
public sealed class WorkItemPageGroupViewModel : ReactiveObject {
    public string Key { get; }
    public WorkStatus? Status { get; }
    public string Label { get; }
    public int Count { get; }
    public bool IsComposing { get; }
    public LucideIconKind Icon { get; }
    public IReadOnlyList<WorkItemPagePart> Parts { get; }
    public IReadOnlyList<WorkItemPageSession> Sessions { get; }
    public IReadOnlyList<WorkItemPageLooseEnd> LooseEnds { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public WorkItemPageGroupViewModel(string key, WorkStatus? status, string label, LucideIconKind icon,
            IReadOnlyList<WorkItemPagePart> parts, IReadOnlyList<WorkItemPageSession> sessions,
            IReadOnlyList<WorkItemPageLooseEnd> looseEnds, bool isExpanded, Action<bool> expandedChanged, bool isComposing = false) {
        Key = key;
        Status = status;
        Label = label;
        Icon = icon;
        Parts = parts;
        Sessions = sessions;
        LooseEnds = looseEnds;
        Count = parts.Count + sessions.Count + looseEnds.Count;
        IsComposing = isComposing;
        _isExpanded = isExpanded;
        ToggleCommand = ReactiveCommand.Create(() => {
            IsExpanded = !IsExpanded;
            expandedChanged(IsExpanded);
        });
    }
}
