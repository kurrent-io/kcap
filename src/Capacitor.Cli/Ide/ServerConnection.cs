namespace Capacitor.Cli.Ide;

/// <param name="State"><c>dormant</c>, <c>connecting</c>, <c>connected</c>, <c>retrying</c> or <c>signed_out</c>.</param>
internal sealed record ServerConnection(string State, string? Detail = null);
