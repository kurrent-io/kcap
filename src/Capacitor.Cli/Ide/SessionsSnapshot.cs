namespace Capacitor.Cli.Ide;

internal sealed record SessionsSnapshot(string SubscriptionId, SessionRow[] Rows);
