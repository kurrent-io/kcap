namespace Capacitor.App.Services;

public enum HeldMessageDecision { Approve, Reject, Withdraw }

public static class HeldMessageDecisionExtensions {
    public static string RouteSegment(this HeldMessageDecision decision) => decision switch {
        HeldMessageDecision.Approve => "approve",
        HeldMessageDecision.Reject => "reject",
        _ => "withdraw",
    };
}
