namespace Capacitor.App.Services.Voice;

/// Words the command model is told to expect: kcap's own, the agents', the session titles, and the
/// end of the open session's latest reply.
public sealed record VoiceHint(IReadOnlyList<string> Terms, string Reply);
