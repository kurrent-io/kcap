namespace Capacitor.Cli.Core.LocalIpc;

/// The in-place resume probe's request, snake_case on the wire. AccountId and ConfigDir are the session's recorded
/// store; a null ConfigDir is an unknown store.
public sealed record ResumeProbeRequestDto(
    string SessionId,
    string Vendor,
    string Directory,
    string? AccountId,
    string? ConfigDir);
