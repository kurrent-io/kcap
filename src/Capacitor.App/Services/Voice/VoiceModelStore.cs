using Whisper.net.Ggml;

namespace Capacitor.App.Services.Voice;

/// The downloaded Whisper models, one file per size in one directory. A model is fetched from
/// Hugging Face the first time voice control needs it, written beside its final name and moved
/// into place whole, so an interrupted download is never mistaken for a model.
public sealed class VoiceModelStore(string directory) {
    public string Directory { get; } = directory;

    public string PathFor(VoiceModelSize size) => Path.Combine(Directory, size.FileName());

    public bool Has(VoiceModelSize size) => File.Exists(PathFor(size));

    /// <param name="progress">Fraction downloaded, when the server says how large the file is.</param>
    public async Task<string> EnsureAsync(VoiceModelSize size, IProgress<double>? progress, CancellationToken ct) {
        var path = PathFor(size);
        if (File.Exists(path)) return path;
        System.IO.Directory.CreateDirectory(Directory);
        var partial = path + ".download";
        try {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(size.GgmlType(), QuantizationType.NoQuantization, ct)
                             .ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true)) {
                var expected = TryLength(source) ?? ExpectedBytes(size);
                var buffer = new byte[1 << 16];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(Math.Min(1, (double)copied / expected));
                }
            }
            File.Move(partial, path, overwrite: true);
            return path;
        } finally {
            try { File.Delete(partial); } catch { }
        }
    }

    static long? TryLength(Stream stream) {
        try {
            return stream.CanSeek && stream.Length > 0 ? stream.Length : null;
        } catch (NotSupportedException) {
            return null;
        }
    }

    // The published sizes, for a progress fraction when the response carries no length.
    static long ExpectedBytes(VoiceModelSize size) => size switch {
        VoiceModelSize.TinyEn => 77_704_715,
        VoiceModelSize.SmallEn => 487_614_201,
        _ => 147_964_211,
    };
}
