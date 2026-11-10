namespace Capacitor.Cli.Recording;

/// <summary>One transcript a recorder sends to the server. A subagent's transcript is its own source.</summary>
public sealed record RecordingSource(string SessionId, string? AgentId, string TranscriptPath, string Vendor, string? Cwd) {
    public string Key => AgentId is null ? SessionId : $"{SessionId}-{AgentId}";
}
