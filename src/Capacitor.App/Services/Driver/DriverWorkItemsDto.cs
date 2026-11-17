namespace Capacitor.App.Services.Driver;

/// <summary>The body of <c>GET /api/driver/work-items</c>: the viewer's work items in flight, each with its parts.</summary>
public sealed record DriverWorkItemsDto(IReadOnlyList<DriverWorkItemsDto.WorkItem> WorkItems) {
    public sealed record WorkItem(
        string                  WorkItemId,
        string                  Title,
        string?                 Goal,
        Workspace?              Workspace,
        string                  Status,
        bool                    SetByHand,
        DateTimeOffset?         LastActivityAt,
        IReadOnlyList<Link>     Links,
        IReadOnlyList<Session>  Sessions,
        IReadOnlyList<Part>     Parts);

    public sealed record Part(
        string                 WorkItemId,
        string                 Title,
        string                 Status,
        bool                   SetByHand,
        IReadOnlyList<string>  WaitsOn,
        IReadOnlyList<Link>    Links,
        IReadOnlyList<Session> Sessions);

    /// <summary>The work item's project, which the app calls a workspace.</summary>
    public sealed record Workspace(string Id, string Name);

    public sealed record Link(string Kind, string Provider, string Value, string ShortKey, string? Url, string? Title, string? State);

    public sealed record Session(string SessionId, string? Title, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
}
