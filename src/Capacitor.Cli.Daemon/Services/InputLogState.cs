namespace Capacitor.Cli.Daemon.Services;

/// <summary>An <see cref="InputLog"/> as its file holds it</summary>
/// <param name="FirstLine">The first transcript line the session's current process can have written</param>
/// <param name="Suspended">Why stamping stopped, or null while it runs</param>
internal sealed record InputLogState(
    int FirstLine, int AcknowledgedThrough, long NextPosition, string? Suspended, DateTimeOffset? SuspendedAt,
    List<InputLogEntry>? Entries, List<InputLogMatch>? Matches);
