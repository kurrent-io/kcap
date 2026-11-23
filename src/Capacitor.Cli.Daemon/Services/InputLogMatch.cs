namespace Capacitor.Cli.Daemon.Services;

/// <summary>A row already placed against its entry, kept until the server has its line, so the line delivered again
/// takes the same stamp and consumes nothing</summary>
/// <param name="LineHash">The SHA-256 of the raw line, which a line delivered again must still match</param>
internal sealed record InputLogMatch(int Line, string LineHash, Guid? DispatchId, bool Local);
