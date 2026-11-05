using System.Formats.Tar;
using ICSharpCode.SharpZipLib.BZip2;

namespace Capacitor.App.Services.Voice;

/// The Kokoro model on disk: sherpa-onnx's full-precision build of Kokoro v1.0, unpacked from its release
/// archive. The archive is unpacked beside the final directory and moved into place whole, and a
/// marker written last, so a half-finished download is never loaded.
public sealed class KokoroModelStore(string modelsDirectory, HttpClient http, string name = KokoroModelStore.DefaultName) {
    // The int8 build is a third of the size but four times slower than real time on a desktop CPU,
    // where this one is a little faster than real time.
    public const string DefaultName = "kokoro-multi-lang-v1_0";
    const string Marker = ".complete";
    // The published archive size, for a progress fraction when the response carries no length.
    const long ArchiveBytes = 349_886_476;

    public string Name { get; } = name;
    Uri Archive => new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Name}.tar.bz2");

    public string Directory { get; } = Path.Combine(modelsDirectory, name);

    public bool IsReady => File.Exists(Path.Combine(Directory, Marker));

    public async Task<string> EnsureAsync(IProgress<double>? progress, CancellationToken ct) {
        if (IsReady) return Directory;
        System.IO.Directory.CreateDirectory(modelsDirectory);
        var download = Path.Combine(modelsDirectory, Name + ".tar.bz2.download");
        var unpacking = Path.Combine(modelsDirectory, Name + ".unpacking");
        try {
            using (var response = await http.GetAsync(Archive, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
                response.EnsureSuccessStatusCode();
                var expected = response.Content.Headers.ContentLength ?? ArchiveBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    // Unpacking takes the last tenth.
                    progress?.Report(Math.Min(0.9, 0.9 * copied / expected));
                }
            }

            if (System.IO.Directory.Exists(unpacking)) System.IO.Directory.Delete(unpacking, recursive: true);
            await Task.Run(() => Unpack(download, unpacking, ct), ct).ConfigureAwait(false);
            var unpacked = Path.Combine(unpacking, Name);
            if (KokoroSynthesizer.ModelFile(unpacked) is null)
                throw new InvalidDataException("The voice archive did not contain the Kokoro model.");
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
            System.IO.Directory.Move(unpacked, Directory);
            await File.WriteAllTextAsync(Path.Combine(Directory, Marker), Archive.ToString(), ct).ConfigureAwait(false);
            progress?.Report(1);
            return Directory;
        } finally {
            try { File.Delete(download); } catch { }
            try { if (System.IO.Directory.Exists(unpacking)) System.IO.Directory.Delete(unpacking, recursive: true); } catch { }
        }
    }

    static void Unpack(string archive, string into, CancellationToken ct) {
        var root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        using var file = File.OpenRead(archive);
        using var bzip = new BZip2InputStream(file);
        using var tar = new TarReader(bzip);
        while (tar.GetNextEntry() is { } entry) {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(into, entry.Name));
            // An entry that climbs out of the target is skipped, never written.
            if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
            switch (entry.EntryType) {
                case TarEntryType.Directory:
                    System.IO.Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                    break;
            }
        }
    }
}
