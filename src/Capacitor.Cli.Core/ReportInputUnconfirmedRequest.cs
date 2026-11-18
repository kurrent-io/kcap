namespace Capacitor.Cli.Core;

/// <summary>An attached agent's daemon cannot tell whether the dispatch <paramref name="DispatchId"/>, handed to the mod,
/// landed. <paramref name="Reason"/> is a short token; the server keeps at most 100 characters of it. JSON mirror of
/// the server's record of the same name.</summary>
public sealed record ReportInputUnconfirmedRequest(Guid DispatchId, string AgentId, string Reason);
