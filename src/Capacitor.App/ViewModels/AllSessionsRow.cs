namespace Capacitor.App.ViewModels;

/// One session on the all-sessions page. Meta is its repo, then its work item and part when it has them.
public sealed record AllSessionsRow(string SessionId, string Title, string Meta, string Age);
