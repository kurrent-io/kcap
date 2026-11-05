using SherpaOnnx;

namespace Capacitor.App.Services.Voice;

/// Kokoro text-to-speech through sherpa-onnx, on the CPU, with espeak-ng turning text into
/// phonemes. The release's lexicon is left out: loading it costs seconds and each call a little more.
public sealed class KokoroSynthesizer : IDisposable {
    readonly OfflineTts _tts;
    readonly Lock _lock = new();

    public KokoroSynthesizer(string modelDirectory, int threads) {
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = ModelFile(modelDirectory) ?? throw new FileNotFoundException("No Kokoro model in " + modelDirectory);
        config.Model.Kokoro.Voices = Path.Combine(modelDirectory, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(modelDirectory, "espeak-ng-data");
        config.Model.Kokoro.Lang = "en-us";
        config.Model.NumThreads = threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        _tts = new OfflineTts(config);
    }

    public int SampleRate => _tts.SampleRate;

    /// The model file a release directory holds: the full-precision one, or the int8 build.
    public static string? ModelFile(string modelDirectory) =>
        ModelFiles.Select(f => Path.Combine(modelDirectory, f)).FirstOrDefault(File.Exists);

    static readonly string[] ModelFiles = ["model.onnx", "model.int8.onnx"];

    /// <param name="speed">1 is the voice's own pace.</param>
    public float[] Generate(string text, KokoroVoice voice, float speed) {
        lock (_lock) {
            // Has a Dispose that frees the native buffer, but does not declare IDisposable.
            var audio = _tts.Generate(text, speed, voice.Id);
            try {
                return audio.Samples;
            } finally {
                audio.Dispose();
            }
        }
    }

    public void Dispose() {
        lock (_lock) _tts.Dispose();
    }
}
