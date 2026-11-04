namespace Capacitor.Models.Transcripts.UserMessages;

/// A user-channel message's kind, and the compact text to show in its place when it is not the
/// person's words. A null label means the message text itself is shown; the label is for display
/// only, and the flags carry what readers decide on.
/// <param name="IsCommand">The person ran something rather than wrote an ask: a slash command, a shell
/// line, a review action. Running it again is another run.</param>
/// <param name="IsInterrupt">A marker saying the person stopped the agent mid-turn.</param>
public readonly record struct UserMessageClass(UserMessageKind Kind, string? Label, bool IsCommand = false, bool IsInterrupt = false) {
    public bool OpensTurn => Kind is not UserMessageKind.Continuation;
}
