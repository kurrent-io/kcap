namespace Capacitor.Cli.Ide;

/// <param name="Origin"><c>local</c>, <c>remote</c>, <c>pending</c> or <c>server</c>.</param>
/// <param name="CreatedAt">Times serialize as ISO 8601 UTC.</param>
internal sealed record SessionRow(
    string Key,
    string Origin,
    string Id,
    string? SessionId,
    string Kind,
    string Vendor,
    string Status,
    string? Title,
    string? Model,
    string? Branch,
    string RepoLabel,
    string CheckoutLabel,
    string? WorktreePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? EndedAt,
    bool? AwaitingInput,
    int? LiveSubagents,
    bool IsStale,
    bool IsTerminalSession,
    string? OwnerName,
    bool? OwnerIsViewer,
    string? MachineBadge);
