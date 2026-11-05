namespace Capacitor.App.Services.Voice;

/// The voice control switches, saved in <c>voice.json</c> under the config root.
public sealed record VoicePreferences(
    bool Enabled = false,
    string WakeWord = VoicePreferences.DefaultWakeWord,
    VoiceModelSize Model = VoiceModelSize.BaseEn,
    bool ReadReplies = true,
    double Sensitivity = VoicePreferences.DefaultSensitivity,
    string? VoiceName = null,
    int Rate = 0,
    bool InstantAnswers = true,
    VoiceUnderstanding Understanding = VoiceUnderstanding.Phrases,
    string? Microphone = null,
    bool SaveAudio = false) {
    public const string DefaultWakeWord = "computer";
    public const double DefaultSensitivity = 0.6;
    public const int MinRate = -10;
    public const int MaxRate = 10;

    /// A blank wake word would make every sound a command, so it reads as the default.
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveWakeWord => string.IsNullOrWhiteSpace(WakeWord) ? DefaultWakeWord : WakeWord.Trim();
}
