namespace Capacitor.Cli.Ide;

internal sealed record InitializeResult(int ProtocolVersion, string CliVersion, string[] Capabilities);
