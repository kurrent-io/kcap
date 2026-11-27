namespace Capacitor.Cli.Ide;

/// <param name="SessionId">With or without dashes; the host reads it in the form the server files sessions under.</param>
internal sealed record ContextSubscribeParams(string SubscriptionId, string SessionId);
