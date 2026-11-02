namespace Capacitor.App.Services;

/// How the server answered a decision on a held message. State is where a decided message now stands; Message is
/// the server's own line for a refusal it explains.
public sealed record HeldMessageDecisionOutcome(HeldMessageDecisionKind Kind, string? State = null, string? Message = null) {
    /// The line the viewer is shown; null when the decision went through.
    public string? Refusal => Kind switch {
        HeldMessageDecisionKind.Decided => null,
        HeldMessageDecisionKind.AlreadyDecided => "That message was already decided",
        HeldMessageDecisionKind.QuestionPending => Message is { Length: > 0 } message
            ? message
            : "The agent is waiting on a question its sender can't answer",
        HeldMessageDecisionKind.NotFound => "That message is no longer waiting",
        HeldMessageDecisionKind.NotOwner => "Only the agent's owner can do that",
        HeldMessageDecisionKind.Unauthorized => "Sign in to decide held messages",
        _ => "Couldn't reach the server. Try again",
    };
}
