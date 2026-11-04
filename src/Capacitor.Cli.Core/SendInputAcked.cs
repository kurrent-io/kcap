namespace Capacitor.Cli.Core;

/// <summary>This daemon took the input dispatched as <paramref name="DispatchId"/>: typed it, or holds it for an agent
/// still resuming. Sent only to a server this daemon advertised <c>SupportsInputAcks</c> to.</summary>
public readonly record struct SendInputAcked(Guid DispatchId, string AgentId);
