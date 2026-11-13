using Capacitor.Client;

namespace Capacitor.Cli.Ide;

internal static class SessionRowMapping {
    public static SessionRow From(AgentRow row) => new(
        Key: row.Key,
        Origin: Origin(row.Origin),
        Id: row.Id,
        SessionId: row.SessionId,
        Kind: row.Kind,
        Vendor: row.Vendor,
        Status: row.Status,
        Title: row.Title,
        Model: row.Model,
        Branch: row.Branch,
        RepoLabel: row.RepoGroupLabel,
        WorktreePath: row.WorktreePath,
        CreatedAt: Utc(row.CreatedAt),
        LastActivityAt: row.LastActivityAt is { } lastActivity ? Utc(lastActivity) : null,
        EndedAt: row.EndedAt is { } ended ? Utc(ended) : null,
        AwaitingInput: row.AwaitingInput,
        LiveSubagents: row.LiveSubagents,
        IsStale: row.IsStale,
        // A failed local agent is not ended: it stays live because it needs the user.
        IsEnded: row.IsEndedSession || row is { Origin: AgentOrigin.Local, Status: "Completed" },
        IsTerminalSession: row.IsTerminalSession,
        OwnerName: row.OwnerName,
        OwnerIsViewer: row.OwnerIsViewer,
        MachineBadge: row.MachineBadge);

    static string Origin(AgentOrigin origin) => origin switch {
        AgentOrigin.Local   => "local",
        AgentOrigin.Remote  => "remote",
        AgentOrigin.Pending => "pending",
        AgentOrigin.Server  => "server",
        _                   => origin.ToString().ToLowerInvariant(),
    };

    // The services keep UTC instants in DateTime; an unspecified kind is read as UTC, not as local time.
    static DateTimeOffset Utc(DateTime value) =>
        new(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
