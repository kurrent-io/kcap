using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// One row of the part chip's menu: a part of the session's work item, or the work item itself with no part.
public sealed record SessionPartOption(string WorkItemId, string Title, WorkStatus? Status, bool IsCurrent) {
    public bool HasStatus => Status is not null;
    public LucideIconKind StatusIcon => (Status ?? WorkStatus.Open).Icon();
}
