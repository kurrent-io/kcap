using System.Reactive;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A status group of a work item page, or the last group, On the work item, which holds the sessions on no part and
/// the viewer's loose ends. <see cref="Status"/> is null on that last group.
public sealed class WorkItemPageGroupViewModel : ReactiveObject {
    public string Key { get; }
    public WorkStatus? Status { get; }
    public string Label { get; }
    public int Count { get; }
    public string Heading => Label.ToUpperInvariant();
    public LucideIconKind Icon => Status?.Icon() ?? LucideIconKind.CircleDashed;
    public IReadOnlyList<WorkItemPagePart> Parts { get; }
    public IReadOnlyList<WorkItemPageSession> Sessions { get; }
    public IReadOnlyList<WorkItemPageLooseEnd> LooseEnds { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public WorkItemPageGroupViewModel(string key, WorkStatus? status, string label, IReadOnlyList<WorkItemPagePart> parts,
            IReadOnlyList<WorkItemPageSession> sessions, IReadOnlyList<WorkItemPageLooseEnd> looseEnds,
            bool isExpanded, Action<bool> expandedChanged) {
        Key = key;
        Status = status;
        Label = label;
        Parts = parts;
        Sessions = sessions;
        LooseEnds = looseEnds;
        Count = parts.Count + sessions.Count + looseEnds.Count;
        _isExpanded = isExpanded;
        ToggleCommand = ReactiveCommand.Create(() => {
            IsExpanded = !IsExpanded;
            expandedChanged(IsExpanded);
        });
    }
}
