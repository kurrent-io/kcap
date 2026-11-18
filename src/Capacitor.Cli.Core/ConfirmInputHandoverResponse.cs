namespace Capacitor.Cli.Core;

/// <summary>The server's answer to <c>ConfirmInputHandover</c>. <see cref="Outcome"/> is one of
/// <see cref="InputHandoverOutcome"/>; a value this daemon does not know reads as a hold.</summary>
/// <param name="BlockingDispatchId">On a hold, the dispatch still with the mod that the held one waits behind; null when
/// it waits for the server to learn of the agent.</param>
public sealed record ConfirmInputHandoverResponse(string? Outcome, Guid? BlockingDispatchId = null);
