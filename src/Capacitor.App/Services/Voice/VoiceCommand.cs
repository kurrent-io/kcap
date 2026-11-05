namespace Capacitor.App.Services.Voice;

/// What one spoken command asks the app to do.
public abstract record VoiceCommand {
    /// Text for the agent: the open session's composer, or a new session when none is open.
    /// Explicit when the speaker said so ("tell it to …"); otherwise it is only what matched no
    /// command, which an interpreter may read differently.
    public sealed record Prompt(string Text, bool Explicit = false) : VoiceCommand;
    /// Text put in the composer without sending it.
    public sealed record Dictate(string Text) : VoiceCommand;
    public sealed record SendDraft : VoiceCommand;
    /// A new session; a permission level applies to this session only.
    public sealed record NewSession(string? Goal, VoicePermissionLevel? Permissions = null) : VoiceCommand;
    /// The launcher's permission level for new sessions, as its chip sets it.
    public sealed record SetPermissions(VoicePermissionLevel Level) : VoiceCommand;
    public sealed record OpenSession(string Query) : VoiceCommand;
    public sealed record NextSession : VoiceCommand;
    public sealed record PreviousSession : VoiceCommand;
    public sealed record ListSessions : VoiceCommand;
    public sealed record GoHome : VoiceCommand;
    public sealed record ShowTab(VoiceTab Tab) : VoiceCommand;
    public sealed record SelectRepository(string Query) : VoiceCommand;
    public sealed record SelectHarness(string Query) : VoiceCommand;
    public sealed record StopAgent : VoiceCommand;
    public sealed record Interrupt : VoiceCommand;
    public sealed record PermissionReply(PermissionAnswer Answer) : VoiceCommand;
    public sealed record ReadReply : VoiceCommand;
    /// The next unread part of the reply being read. "Continue" with no reply being read is a
    /// prompt, so the words are kept.
    public sealed record ReadMore(string Spoken) : VoiceCommand;
    /// One part of the reply, by its heading or its number.
    public sealed record ReadPart(string Query) : VoiceCommand;
    public sealed record ReadAll : VoiceCommand;
    /// The part read before the one just read.
    public sealed record PreviousPart : VoiceCommand;
    /// Whatever the app said last, said again: a question keeps waiting for its answer.
    public sealed record Repeat : VoiceCommand;
    /// More time to answer what the app is waiting for.
    public sealed record Wait : VoiceCommand;
    public sealed record Status : VoiceCommand;
    public sealed record OpenSettings : VoiceCommand;
    public sealed record Refresh : VoiceCommand;
    public sealed record Zoom(int Direction) : VoiceCommand;
    public sealed record ShowWindow : VoiceCommand;
    public sealed record StopListening : VoiceCommand;
    public sealed record StopSpeaking : VoiceCommand;
    public sealed record Help : VoiceCommand;
    public sealed record Confirm : VoiceCommand;
    /// Words to say back: an interpreter's answer about the app, or its question when unsure.
    public sealed record Say(string Text) : VoiceCommand;
    public sealed record Cancel : VoiceCommand;
}
