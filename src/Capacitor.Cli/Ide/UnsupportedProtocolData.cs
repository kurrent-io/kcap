namespace Capacitor.Cli.Ide;

/// <summary>The <c>data</c> of an <c>unsupported_protocol</c> error: the versions this side speaks.</summary>
internal sealed record UnsupportedProtocolData(int[] Supported);
