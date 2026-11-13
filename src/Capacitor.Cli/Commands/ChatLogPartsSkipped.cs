namespace Capacitor.Cli.Commands;

/// <summary>Parts of a VS Code chat log the importer could not read, so some of the session's usage or its title was
/// left out.</summary>
public sealed record ChatLogPartsSkipped(string SessionId, string? AgentId, string Detail)
    : ImportWarning(SessionId, AgentId) {
    public override string Message => $"Usage or title partly left out for {SessionId}: {Detail}";
}
