namespace Capacitor.Cli.Core;

/// <summary>This daemon typed the input dispatched as <paramref name="DispatchId"/>. Sent only to a server this daemon
/// advertised <c>SupportsInputAcks</c> to.</summary>
public readonly record struct SendInputAcked(Guid DispatchId, string AgentId);
