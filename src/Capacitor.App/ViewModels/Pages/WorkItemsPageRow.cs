namespace Capacitor.App.ViewModels.Pages;

/// One open work item on the work items page. Meta counts its parts done and its sessions.
public sealed record WorkItemsPageRow(string WorkItemId, string Title, string Meta);
