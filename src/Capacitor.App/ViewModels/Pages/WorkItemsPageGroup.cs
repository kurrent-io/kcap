using System.Collections.ObjectModel;

namespace Capacitor.App.ViewModels.Pages;

/// The work items page's rows of one status.
public sealed class WorkItemsPageGroup(WorkStatus status) {
    public WorkStatus Status { get; } = status;
    public string Label => Status.Label();
    public ObservableCollection<WorkItemsPageRow> Rows { get; } = [];
}
