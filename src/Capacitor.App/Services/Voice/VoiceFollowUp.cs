namespace Capacitor.App.Services.Voice;

/// What the next utterance after a response is for. Anything but None is heard without the wake word.
public enum VoiceFollowUp {
    None,
    /// A yes or no for <see cref="VoiceResponse.OnConfirm"/>.
    Confirm,
    /// The prompt for a session the user asked to start.
    Prompt,
    /// Voice control turns itself off.
    StopListening,
}
