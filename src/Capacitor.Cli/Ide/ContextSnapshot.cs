namespace Capacitor.Cli.Ide;

internal sealed record ContextSnapshot(string SubscriptionId, string SessionId, WorkSection Work, PlanSection Plan);
