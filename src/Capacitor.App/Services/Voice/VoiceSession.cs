namespace Capacitor.App.Services.Voice;

/// A session as voice control names it: the rail's title, or its vendor when it has none.
public sealed record VoiceSession(string AgentId, string Title, string Status, bool IsOpen);
