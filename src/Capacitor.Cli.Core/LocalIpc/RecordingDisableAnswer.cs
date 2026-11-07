namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What one daemon said of a session a person disabled.</summary>
/// <param name="AgentLive">A live hosted agent of that daemon runs the session, and its output goes on reaching the
/// server, whichever runtime carries it, which recording stopping doesn't touch.</param>
/// <param name="Server">The server that daemon sends to, which need not be the one the command deletes from.</param>
public sealed record RecordingDisableAnswer(RecordingDisable Outcome, bool AgentLive = false, string? Server = null);
