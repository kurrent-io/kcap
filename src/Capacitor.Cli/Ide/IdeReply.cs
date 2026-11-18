using System.Text.Json.Nodes;

namespace Capacitor.Cli.Ide;

/// <summary>A handler's result, and what to send once that result is on the wire.</summary>
/// <param name="Then">Runs after the result is written and before the next request is read, so what it sends
/// follows the reply. Skipped when the peer is gone.</param>
internal sealed record IdeReply(JsonNode? Result, Func<Task>? Then = null);
