namespace Capacitor.Cli.Daemon.Services;

/// <summary>A message written, or about to be written, to the terminal, whose row has not been placed yet</summary>
/// <param name="TextHash">The SHA-256 of the text pasted, trailing newlines trimmed. The text itself is never stored</param>
/// <param name="Written">The submit went out. Until then nothing shows whether the paste reached the terminal</param>
internal sealed record InputLogEntry(long Position, Guid? DispatchId, bool Local, string TextHash, bool Written, DateTimeOffset At);
