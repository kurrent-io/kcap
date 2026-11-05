namespace Capacitor.App.Services.Voice;

/// Voice control's state for the window chip and Settings.
public sealed record VoiceStatus(VoiceState State, string Detail, string? LastHeard = null) {
    /// The last utterance did not open with the wake word, so nothing was done with it.
    public bool MissedWakeWord { get; init; }

    public string WakeWord { get; init; } = VoicePreferences.DefaultWakeWord;

    public static VoiceStatus Off { get; } = new(VoiceState.Off, "Voice control is off.");

    public bool IsActive => State is not (VoiceState.Off or VoiceState.Failed);
}
