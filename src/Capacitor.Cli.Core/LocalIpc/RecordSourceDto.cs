namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>A source a hosted agent's hook asks its daemon to record.</summary>
/// <param name="AgentId">The hosted agent the hook runs under.</param>
/// <param name="SubagentId">Null for the session's own transcript.</param>
/// <param name="SkipTitle">The session has its title already, as a resumed one does.</param>
public sealed record RecordSourceDto(string AgentId, string SessionId, string? SubagentId, string TranscriptPath, string Vendor, string? Cwd, bool SkipTitle);
