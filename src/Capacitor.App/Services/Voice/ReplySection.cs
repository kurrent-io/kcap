namespace Capacitor.App.Services.Voice;

/// One part of a reply: its heading (or "part 2" when it has none) and its text as it is spoken.
public sealed record ReplySection(string Title, string Spoken);
