namespace Capacitor.Cli.Ide;

/// <param name="Removes">Keys of the rows that left the subscription.</param>
internal sealed record SessionsChanged(string SubscriptionId, SessionRow[] Upserts, string[] Removes);
