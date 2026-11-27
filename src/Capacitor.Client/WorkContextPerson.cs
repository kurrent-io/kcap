namespace Capacitor.Client;

/// One person with a session attached to the work item; `Name` is "You" when the viewer owns it.
public sealed record WorkContextPerson(string UserId, string Name, string? AvatarUrl, DateTimeOffset? LastActivityAt, bool IsViewer);
