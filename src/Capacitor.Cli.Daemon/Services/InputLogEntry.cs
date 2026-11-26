namespace Capacitor.Cli.Daemon.Services;

/// <summary>A message written, or about to be written, to the terminal, whose row has not been placed yet</summary>
/// <param name="AgentId">The agent a dispatch was sent to. A session's processes share its log, each its own agent</param>
/// <param name="TextHash">The SHA-256 of the text pasted, trailing newlines trimmed. The text itself is never stored</param>
/// <param name="Written">The submit went out. Until then nothing shows whether the paste reached the terminal</param>
internal sealed record InputLogEntry(long Position, Guid? DispatchId, string? AgentId, bool Local, string TextHash, bool Written, DateTimeOffset At);
