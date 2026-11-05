namespace Capacitor.App.Services.Voice;

/// The platform's microphone, its own speech voices, and raw playback for generated speech.
/// Each call opens a fresh device; a microphone is opened by name, or the system default for null.
public sealed record VoiceDevices(
    Func<string?, IMicrophone> OpenMicrophone, Func<ISpeechOutput> OpenSpeech, Func<IPcmPlayer>? OpenPlayer = null,
    Func<IReadOnlyList<string>>? ListMicrophones = null, Func<string?>? DefaultMicrophone = null);
