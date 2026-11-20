namespace Capacitor.Cli.Ide;

/// <param name="Scope">The server sessions' scope, <c>mine</c> or <c>team</c>, until a <c>sessions/set_scope</c> changes it.</param>
internal sealed record InitializeResult(int ProtocolVersion, string CliVersion, string[] Capabilities, string Scope);
