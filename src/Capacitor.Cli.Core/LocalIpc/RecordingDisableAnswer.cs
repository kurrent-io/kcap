namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What one daemon said of a session a person disabled.</summary>
/// <param name="TerminalLive">A live hosted agent of that daemon runs the session, and its remote terminal goes on
/// reaching the server, which recording stopping doesn't touch.</param>
/// <param name="Server">The server that daemon sends to, which need not be the one the command deletes from.</param>
public sealed record RecordingDisableAnswer(RecordingDisable Outcome, bool TerminalLive = false, string? Server = null);
