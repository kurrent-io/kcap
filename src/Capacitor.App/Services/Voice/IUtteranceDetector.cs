namespace Capacitor.App.Services.Voice;

/// Cuts the microphone's continuous 16 kHz stream into utterances. One capture thread feeds it.
public interface IUtteranceDetector : IDisposable {
    /// 0 needs a loud, close voice; 1 picks up a whisper (and more of the room).
    double Sensitivity { get; set; }

    /// True while an utterance is being collected.
    bool InSpeech { get; }

    /// Feeds samples; returns every utterance they completed, oldest first.
    IReadOnlyList<float[]> Push(ReadOnlySpan<float> samples);
}
