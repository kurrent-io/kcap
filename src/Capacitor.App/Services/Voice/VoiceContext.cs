namespace Capacitor.App.Services.Voice;

/// What an interpreter is told about the app, so "the login one" can name a session.
public sealed record VoiceContext(
    IReadOnlyList<VoiceSession> Sessions,
    string? OpenSession,
    string Launcher,
    string? PendingPermission,
    string? ReplyParts,
    IReadOnlyList<VoiceExchange>? Recent = null);
