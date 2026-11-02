namespace Capacitor.App.Services;

/// The server's held-message routes for one hosted agent. Every failure is a value.
public interface IHeldMessagesApi {
    /// A server with agent privileges off, or an agent the viewer cannot see, answers 404, which reads as nothing held.
    Task<HeldMessagesRead> ListAsync(string agentId, CancellationToken ct);
    Task<HeldMessageDecisionOutcome> DecideAsync(string agentId, Guid dispatchId, HeldMessageDecision decision, CancellationToken ct);
}
