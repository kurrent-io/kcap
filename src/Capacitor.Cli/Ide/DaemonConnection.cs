namespace Capacitor.Cli.Ide;

/// <param name="State"><c>connecting</c>, <c>attached</c> or <c>unreachable</c>.</param>
internal sealed record DaemonConnection(string State, string Name, string? Detail = null);
