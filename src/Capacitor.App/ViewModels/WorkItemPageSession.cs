namespace Capacitor.App.ViewModels;

/// A visible session of a work item. A session the app has not listed is named by its id alone.
public sealed record WorkItemPageSession(string SessionId, string Title, string? Meta);
