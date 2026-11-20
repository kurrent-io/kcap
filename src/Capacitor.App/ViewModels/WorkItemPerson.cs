namespace Capacitor.App.ViewModels;

/// Someone who owns a session in a work item.
public sealed record WorkItemPerson(string UserId, string? Name, string? AvatarUrl);
