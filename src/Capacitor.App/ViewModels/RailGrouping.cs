namespace Capacitor.App.ViewModels;

/// How the rail groups its sessions.
public enum RailGrouping {
    None,
    Repository,
    Date,
    Status,
    /// Stored as 4 by app versions that grouped by work item; read as Repository.
    WorkItem,
}
