namespace Capacitor.App.Services.Voice;

/// Turns one utterance of 16 kHz mono samples into text, on this machine.
public interface ISpeechRecognizer : IDisposable {
    /// <param name="vocabulary">Words the utterance probably contains (the wake word), to bias the decoder.</param>
    Task<string> TranscribeAsync(float[] samples, string? vocabulary, CancellationToken ct);
}
