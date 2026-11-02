using System.Collections.Concurrent;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

sealed class FakeHeldMessagesApi : IHeldMessagesApi {
    public readonly ConcurrentQueue<string> Reads = new();
    public readonly ConcurrentQueue<(Guid DispatchId, HeldMessageDecision Decision)> Decisions = new();
    public Func<string, Task<HeldMessagesRead>> ListHandler = _ => Task.FromResult(HeldMessagesRead.None);
    public Func<HeldMessageDecision, Task<HeldMessageDecisionOutcome>> DecideHandler =
        _ => Task.FromResult(new HeldMessageDecisionOutcome(HeldMessageDecisionKind.Decided, "dispatched"));

    public Task<HeldMessagesRead> ListAsync(string agentId, CancellationToken ct) {
        Reads.Enqueue(agentId);
        return ListHandler(agentId);
    }

    public Task<HeldMessageDecisionOutcome> DecideAsync(string agentId, Guid dispatchId, HeldMessageDecision decision, CancellationToken ct) {
        Decisions.Enqueue((dispatchId, decision));
        return DecideHandler(decision);
    }

    public static ListedHeldMessageDto Message(Guid id, string state = "held", string? outcome = null) => new() {
        DispatchId = id, SenderUserId = "u2", SenderName = "Bea", Text = "run the tests", State = state, Outcome = outcome,
        HeldAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
    };
}
