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

    public void Record(string sessionId, IReadOnlyList<(int FileLine, int Shift)> segments) {
        if (PathFor(sessionId) is not { } path) return;

        // Replaced, never rewritten in place, so an import reading it meanwhile reads one whole version.
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try {
            Directory.CreateDirectory(partition.DirectoryPath);
            File.WriteAllText(temp, string.Concat(segments.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.FileLine} {s.Shift}\n"))));
            File.Move(temp, path, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best effort, as the summary says.
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The segments in file order, or null when there are none or they cannot be read whole.</summary>
    public IReadOnlyList<(int FileLine, int Shift)>? Read(string sessionId) {
        if (PathFor(sessionId) is not { } path) return null;

        try {
            var segments = new List<(int, int)>();

            foreach (var line in File.ReadAllLines(path)) {
                var parts = line.Split(' ');
                if (parts.Length != 2
                 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fileLine)
                 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shift)) {
                    return null;
                }

                segments.Add((fileLine, shift));
            }

            return segments.Count > 0 ? segments : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    string? PathFor(string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(char.IsAsciiLetterOrDigit)
            ? Path.Combine(partition.DirectoryPath, sessionId)
            : null;
}
