using System.Buffers.Binary;
using System.Globalization;

namespace Capacitor.Cli.Core;

/// <summary>
/// How a watcher numbered a rewritten transcript's lines, kept per server so <c>kcap import</c> resumes at the file
/// line after the last one the server took, and numbers what it sends the same way: from each segment's file line on,
/// a line went out under its file number plus that segment's shift. Beside it, a hash of each line sent, so the next
/// watcher can find a rewrite made while none ran. Best effort: without them an import resumes by file position, and
/// a watcher takes the file as it finds it.
/// </summary>
public sealed class TranscriptRenumbering(SpoolPartition partition) {
    public const string DirectoryName = "transcript-renumbering";

    public TranscriptRenumbering(ConfigRoot config, string? server)
        : this(SpoolPartition.Server(config.Path(DirectoryName), server)) { }

    public void Record(string sessionId, TranscriptLineMap map) {
        if (PathFor(sessionId) is not { } path) return;

        // Replaced, never rewritten in place, so an import reading it meanwhile reads one whole version.
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try {
            Directory.CreateDirectory(partition.DirectoryPath);
            var segments = map.Segments.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.FileLine} {s.Shift}\n"));
            var kept     = map.Kept.Select(k => string.Create(CultureInfo.InvariantCulture, $"k {k.Key} {k.Value}\n"));
            File.WriteAllText(temp, string.Concat(segments.Concat(kept)));
            File.Move(temp, path, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best effort, as the summary says.
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The numbering recorded, or null when there is none or it cannot be read whole.</summary>
    public TranscriptLineMap? Read(string sessionId) {
        if (PathFor(sessionId) is not { } path) return null;

        try {
            var segments = new List<(int, int)>();
            var kept     = new List<KeyValuePair<int, int>>();

            foreach (var line in File.ReadAllLines(path)) {
                var parts = line.Split(' ');
                var isKept = parts is ["k", _, _];
                if (parts.Length != (isKept ? 3 : 2)
                 || !int.TryParse(parts[^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var first)
                 || !int.TryParse(parts[^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var second)) {
                    return null;
                }

                if (isKept) kept.Add(new(first, second));
                else segments.Add((first, second));
            }

            return segments.Count > 0 ? new TranscriptLineMap(segments, kept) : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="hashes"/> up to <paramref name="count"/>, of which the first <paramref name="saved"/> are
    /// on disk already. Only what changed is written; a write cut short leaves fewer hashes, never wrong ones.
    /// </summary>
    public void SaveHashes(string sessionId, IReadOnlyList<ulong> hashes, int saved, int count) {
        if (PathFor(sessionId) is not { } path) return;

        try {
            Directory.CreateDirectory(partition.DirectoryPath);
            using var file = new FileStream(path + HashesSuffix, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            var have = (int)Math.Min(file.Length / 8, Math.Min(saved, count));
            file.SetLength(have * 8L);
            file.Position = have * 8L;

            var buffer = new byte[(count - have) * 8];
            for (var i = have; i < count; i++) BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan((i - have) * 8), hashes[i]);

            file.Write(buffer);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best effort, as the summary says.
        }
    }

    public ulong[] ReadHashes(string sessionId) {
        if (PathFor(sessionId) is not { } path) return [];

        try {
            var bytes  = File.ReadAllBytes(path + HashesSuffix);
            var hashes = new ulong[bytes.Length / 8];
            for (var i = 0; i < hashes.Length; i++) hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8));

            return hashes;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    const string HashesSuffix = ".hashes";

    string? PathFor(string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(char.IsAsciiLetterOrDigit)
            ? Path.Join(partition.DirectoryPath, sessionId)
            : null;
}
