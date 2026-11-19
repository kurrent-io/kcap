namespace Capacitor.App.ViewModels.Pages;

/// One session on the sessions page. Meta is its repo, then its work item and part when it has them. OwnerName is
/// null on the viewer's own sessions.
public sealed record SessionsPageRow(string SessionId, string Title, string Meta, string Age, AgentStatusPresentation Status,
        string? Vendor = null, string? OwnerName = null, string? OwnerInitials = null, string? OwnerAvatarUrl = null) {
    public bool HasVendor => Vendor is not null;
    public bool ShowsOwner => OwnerName is not null;
}
