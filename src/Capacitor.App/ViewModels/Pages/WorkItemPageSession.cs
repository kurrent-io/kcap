namespace Capacitor.App.ViewModels.Pages;

/// A visible session of a work item, from the app's session list when it has one, else as the work item's read names it.
/// <see cref="Arrives"/> when it was just dropped where it now sits.
public sealed record WorkItemPageSession(string SessionId, string Title, string? Meta, string? Vendor, bool Arrives = false);
