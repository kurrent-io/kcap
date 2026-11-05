namespace Capacitor.App.Services.Voice;

public enum VoiceState {
    Off,
    /// Downloading or loading the model, or opening the microphone.
    Preparing,
    /// Waiting for the wake word.
    Listening,
    /// The wake word was heard alone; the next utterance is the command.
    AwaitingCommand,
    /// A follow-up question is open: a yes or no, or a new session's prompt.
    AwaitingAnswer,
    /// Transcribing or carrying out a command.
    Working,
    Speaking,
    Failed,
}
