namespace Capacitor.Models.Transcripts.UserMessages;

/// Who a user-channel message came from, as far as turn boundaries are concerned.
public enum UserMessageKind {
    /// A person wrote it: a prompt, a slash command, a shell line. Opens a turn.
    Person,

    /// Something outside the session asked for work: a CI event, another session, a scheduled firing.
    /// Opens a turn, labelled as not the person.
    Trigger,

    /// The harness carrying on the work already asked for: skill bodies, notifications, hand-backs,
    /// compaction, interrupts, vendor preambles. Never opens a turn.
    Continuation,
}
