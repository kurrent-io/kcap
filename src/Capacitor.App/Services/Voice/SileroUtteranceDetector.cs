using SherpaOnnx;

namespace Capacitor.App.Services.Voice;

/// Finds speech with the Silero voice model through sherpa-onnx, which tells a voice from a fan, a
/// keyboard or music by its sound rather than its loudness. Each utterance is cut from the raw audio,
/// with a little of what came before, so the first word is never clipped.
public sealed class SileroUtteranceDetector : IUtteranceDetector {
    const int Rate = IMicrophone.SampleRate;
    const int PreRollSamples = Rate * 300 / 1000;
    // Longer than a breath between words, so one prompt is not cut in two.
    const float EndSilenceSeconds = 1.0f;
    const float MinSpeechSeconds = 0.25f;
    const float MaxSpeechSeconds = 28f;

    readonly VoiceActivityDetector _vad;
    readonly float[] _history = new float[Rate * 40];
    long _written;
    float _gain;
    double _sensitivity;

    public SileroUtteranceDetector(string modelPath, double sensitivity) {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        // A lower bar lets a whisper through; the gain below does most of the work, so it stays near the default.
        config.SileroVad.Threshold = (float)(0.55 - 0.2 * Math.Clamp(sensitivity, 0, 1));
        config.SileroVad.MinSilenceDuration = EndSilenceSeconds;
        config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = MaxSpeechSeconds;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = Rate;
        config.NumThreads = 1;
        config.Debug = 0;
        _vad = new VoiceActivityDetector(config, 60);
        Sensitivity = sensitivity;
    }

    public double Sensitivity {
        get => _sensitivity;
        set {
            _sensitivity = Math.Clamp(value, 0, 1);
            // The model judges what it is given: a quiet voice is raised so a whisper reads as speech.
            _gain = (float)(1 + 7 * _sensitivity);
        }
    }

    public bool InSpeech => _vad.IsSpeechDetected();

    public IReadOnlyList<float[]> Push(ReadOnlySpan<float> samples) {
        var boosted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) {
            _history[(_written + i) % _history.Length] = samples[i];
            boosted[i] = Math.Clamp(samples[i] * _gain, -1f, 1f);
        }
        _written += samples.Length;
        _vad.AcceptWaveform(boosted);

        List<float[]>? done = null;
        while (!_vad.IsEmpty()) {
            var segment = _vad.Front();
            var start = Math.Max(Math.Max(0, (long)segment.Start - PreRollSamples), _written - _history.Length);
            var end = Math.Min(_written, (long)segment.Start + segment.Samples.Length);
            _vad.Pop();
            if (end <= start) continue;
            var utterance = new float[end - start];
            for (var i = 0; i < utterance.Length; i++) utterance[i] = _history[(start + i) % _history.Length];
            (done ??= []).Add(utterance);
        }
        return done ?? (IReadOnlyList<float[]>)[];
    }

    public void Dispose() => _vad.Dispose();
}
