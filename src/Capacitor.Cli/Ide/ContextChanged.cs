namespace Capacitor.Cli.Ide;

/// <summary>Each section that differs from the one last sent for the subscription, whole; an unchanged one is left out.</summary>
internal sealed record ContextChanged(string SubscriptionId, WorkSection? Work, PlanSection? Plan);
