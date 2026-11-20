using Lucide.Avalonia;

namespace Capacitor.App.ViewModels.Pages;

/// A status a new slice can be added in.
public sealed record WorkItemPageStatus(WorkStatus Status) {
    public string Label => Status.Label();
    public LucideIconKind Icon => Status.Icon();
}
