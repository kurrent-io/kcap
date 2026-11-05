namespace Capacitor.App.Services.Voice;

public enum VoiceCue {
    /// The wake word was heard; the next utterance is the command.
    Listening,
    /// The command was understood and is being carried out.
    Accepted,
    /// Nothing usable was heard, or the command failed.
    Failed,
    /// Done answering: what is said from now on is heard as a new command.
    Ready,
}
