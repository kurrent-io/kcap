namespace Capacitor.Cli.Core;

/// <summary>An attached agent's daemon asks whether it may pass the held dispatch <paramref name="DispatchId"/> to the
/// agent's mod. JSON mirror of the server's record of the same name.</summary>
public sealed record ConfirmInputHandoverRequest(Guid DispatchId, string AgentId);
