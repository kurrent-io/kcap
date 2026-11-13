namespace Capacitor.Cli.Ide;

/// <summary>The <c>data</c> of an <c>unsupported_protocol</c> error: the versions this side speaks, and the CLI that speaks them.</summary>
internal sealed record UnsupportedProtocolData(int[] Supported, string CliVersion);
