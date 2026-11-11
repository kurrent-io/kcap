using System.Globalization;

namespace Capacitor.Cli.Core;

/// <summary>
/// How a watcher numbered a rewritten transcript's lines, kept per server so <c>kcap import</c> resumes at the file
/// line after the last one the server took, and numbers what it sends the same way: from each segment's file line on,
/// a line went out under its file number plus that segment's shift. Best effort: without it an import resumes by
/// file position, as it would for any transcript.
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

    string? PathFor(string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(char.IsAsciiLetterOrDigit)
            ? Path.Join(partition.DirectoryPath, sessionId)
            : null;
}
