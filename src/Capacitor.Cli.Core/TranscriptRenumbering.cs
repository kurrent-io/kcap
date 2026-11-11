using System.Globalization;

namespace Capacitor.Cli.Core;

/// <summary>
/// Where a watcher began sending a rewritten transcript's lines under numbers other than their file positions, kept
/// per server so <c>kcap import</c> resumes at the file line after the last one the server took rather than at that
/// line's number. Only the latest renumbering is kept: every file line from it on was sent under its number plus the
/// shift. Best effort: without it an import resumes by file position, as it would for any transcript.
/// </summary>
public sealed class TranscriptRenumbering(SpoolPartition partition) {
    public const string DirectoryName = "transcript-renumbering";

    public TranscriptRenumbering(ConfigRoot config, string? server)
        : this(SpoolPartition.Server(config.Path(DirectoryName), server)) { }

    public void Record(string sessionId, int fileLine, int shift) {
        if (PathFor(sessionId) is not { } path) return;

        try {
            Directory.CreateDirectory(partition.DirectoryPath);
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"{fileLine} {shift}\n"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best effort, as the summary says.
        }
    }

    public (int FileLine, int Shift)? Read(string sessionId) {
        if (PathFor(sessionId) is not { } path) return null;

        try {
            var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.TrimEntries);

            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fileLine)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shift)
                    ? (fileLine, shift)
                    : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    string? PathFor(string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(char.IsAsciiLetterOrDigit)
            ? Path.Combine(partition.DirectoryPath, sessionId)
            : null;
}
