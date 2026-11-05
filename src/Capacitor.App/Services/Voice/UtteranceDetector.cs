namespace Capacitor.App.Services.Voice;

/// Cuts a continuous 16 kHz stream into utterances by energy against an adaptive noise floor, so
/// the recognizer only ever sees speech. Not thread-safe: one capture thread feeds it.
public sealed class UtteranceDetector : IUtteranceDetector {
    public const int FrameSamples = IMicrophone.SampleRate * 30 / 1000;
    const int StartFrames = 3;
    const int PreRollFrames = 500 / 30;
    // Whispered speech leaves long gaps between words; a shorter hangover splits one prompt in two.
    const int HangoverFrames = 900 / 30;
    const int MinVoicedFrames = 350 / 30;
    const int MaxFrames = 28_000 / 30;

    readonly Queue<float[]> _preRoll = new();
    readonly List<float[]> _utterance = [];
    readonly float[] _frame = new float[FrameSamples];
    int _frameFill;
    double _noiseFloor = 0.002;
    int _onsetRun;
    int _silentRun;
    int _voiced;
    bool _inSpeech;
    double _sensitivity;

    public UtteranceDetector(double sensitivity) => Sensitivity = sensitivity;

    /// 0 needs a loud, close voice; 1 picks up a whisper (and more of the room).
    public double Sensitivity {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 0, 1);
    }

    /// True while an utterance is being collected.
    public bool InSpeech => _inSpeech;

    /// Feeds samples; returns every utterance they completed, oldest first.
    public IReadOnlyList<float[]> Push(ReadOnlySpan<float> samples) {
        List<float[]>? done = null;
        while (samples.Length > 0) {
            var take = Math.Min(FrameSamples - _frameFill, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill < FrameSamples) break;
            _frameFill = 0;
            if (OnFrame((float[])_frame.Clone()) is { } utterance) (done ??= []).Add(utterance);
        }
        return done ?? (IReadOnlyList<float[]>)[];
    }

    public void Dispose() { }

    /// Drops anything collected: what the microphone heard while the app was talking is not the user.
    public void Reset() {
        _preRoll.Clear();
        _utterance.Clear();
        _frameFill = 0;
        _onsetRun = 0;
        _silentRun = 0;
        _voiced = 0;
        _inSpeech = false;
    }

    float[]? OnFrame(float[] frame) {
        var rms = Rms(frame);
        var ratio = Lerp(6.0, 1.8, _sensitivity);
        // Exponential, because loudness is: each step of sensitivity divides the floor by the same factor,
        // so the top of the range reaches a whisper at arm's length.
        var floorMin = 0.02 * Math.Pow(0.01, _sensitivity);
        var loud = rms > Math.Max(_noiseFloor * ratio, floorMin);

        if (!_inSpeech) {
            _preRoll.Enqueue(frame);
            if (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            if (loud) {
                if (++_onsetRun < StartFrames) return null;
                _inSpeech = true;
                _utterance.AddRange(_preRoll);
                _preRoll.Clear();
                _voiced = _onsetRun;
                _silentRun = 0;
                return null;
            }
            _onsetRun = 0;
            _noiseFloor = Math.Max(0.0003, _noiseFloor * 0.95 + rms * 0.05);
            return null;
        }

        _utterance.Add(frame);
        if (loud) {
            _voiced++;
            _silentRun = 0;
        } else {
            _silentRun++;
        }
        if (_silentRun < HangoverFrames && _utterance.Count < MaxFrames) return null;

        var keep = _voiced >= MinVoicedFrames;
        var samples = keep ? Concat(_utterance) : null;
        _utterance.Clear();
        _inSpeech = false;
        _onsetRun = 0;
        _silentRun = 0;
        _voiced = 0;
        return samples;
    }

    /// Scales an utterance so its peak sits near full scale; a whisper reaches the recognizer at
    /// the level of ordinary speech. The gain is capped so a near-silent clip stays near silent.
    public static float[] Amplify(float[] samples, float maxGain = 24f) {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 0) return samples;
        var gain = Math.Min(maxGain, 0.9f / peak);
        if (gain <= 1) return samples;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) result[i] = samples[i] * gain;
        return result;
    }

    /// Loudness on a log scale from a quiet room (0) to a raised voice (1), so a whisper still
    /// registers rather than sitting at the bottom of a linear scale.
    public static double Loudness(ReadOnlySpan<float> samples) {
        const double quiet = 0.0005;
        const double loud = 0.2;
        var rms = Rms(samples);
        return rms <= quiet ? 0 : Math.Clamp(Math.Log(rms / quiet) / Math.Log(loud / quiet), 0, 1);
    }

    internal static double Rms(ReadOnlySpan<float> frame) {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return Math.Sqrt(sum / Math.Max(1, frame.Length));
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static float[] Concat(List<float[]> frames) {
        var result = new float[frames.Count * FrameSamples];
        for (var i = 0; i < frames.Count; i++) frames[i].CopyTo(result, i * FrameSamples);
        return result;
    }
}
