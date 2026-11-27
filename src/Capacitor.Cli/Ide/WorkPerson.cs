namespace Capacitor.Cli.Ide;

/// <param name="Name">Absent for the viewer and for a person the server names only by an opaque id: the client words
/// those itself.</param>
internal sealed record WorkPerson(string? Name, string? AvatarUrl, DateTimeOffset? LastActivityAt, bool IsViewer);
