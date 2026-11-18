namespace Capacitor.Cli.Core;

/// <summary>An attached agent's mod saw the dispatch <paramref name="DispatchId"/> land as the transcript row
/// <paramref name="RowUuid"/> of <paramref name="SessionId"/>, which can be a later session than the one it was sent
/// to. JSON mirror of the server's record of the same name.</summary>
public sealed record ReportInputLinkRequest(Guid DispatchId, string AgentId, string SessionId, Guid RowUuid);
