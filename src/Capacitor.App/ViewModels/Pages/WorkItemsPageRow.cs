namespace Capacitor.App.ViewModels.Pages;

/// One open work item on the work items page. Meta counts its parts done and its sessions; People are the owners of
/// those sessions, at most three shown.
public sealed record WorkItemsPageRow(string WorkItemId, string Title, string Meta, WorkStatus Status, IReadOnlyList<WorkItemsPageRowPerson> People) {
    public Lucide.Avalonia.LucideIconKind Icon => Status.Icon();
}
