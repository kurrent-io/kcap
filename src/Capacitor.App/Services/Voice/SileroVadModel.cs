namespace Capacitor.App.Services.Voice;

/// The Silero voice-activity model, fetched once into the voice models directory. A failed
/// download leaves nothing behind, and listening carries on with the loudness detector.
public sealed class SileroVadModel(string directory) {
    static readonly Uri Source = new("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx");

    public string Path => System.IO.Path.Combine(directory, "silero_vad.onnx");

    public async Task<string> EnsureAsync(CancellationToken ct) {
        if (File.Exists(Path)) return Path;
        Directory.CreateDirectory(directory);
        var partial = Path + ".download";
        try {
            using var http = new HttpClient();
            await using (var source = await http.GetStreamAsync(Source, ct).ConfigureAwait(false))
            await using (var target = File.Create(partial)) {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            File.Move(partial, Path, overwrite: true);
            return Path;
        } finally {
            try { File.Delete(partial); } catch { }
        }
    }

    /// The Silero detector, or the loudness one when the model cannot be fetched or loaded.
    public async Task<IUtteranceDetector> OpenAsync(double sensitivity, CancellationToken ct) {
        try {
            return new SileroUtteranceDetector(await EnsureAsync(ct).ConfigureAwait(false), sensitivity);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return new UtteranceDetector(sensitivity);
        }
    }
}
