namespace Capacitor.Client;

/// Who launched the session, as the daemon reports it. It names a contributor the server sends
/// with no display name when that contributor is the same person.
public sealed record WorkContextRequester(string? Id, string? Display);
