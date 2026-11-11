using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// Which run of a Copilot session a point in its transcript belongs to: the byte offset of the latest <c>session.start</c>
/// or <c>session.resume</c> line. Copilot appends one as each run begins, before it fires that run's sessionStart hook, and
/// never rewrites the file, so a later run always has a later boundary.
/// </summary>
public static class CopilotRunBoundary {
    /// <summary>The latest boundary in the first <paramref name="upTo"/> bytes, or in the whole file; null when the
    /// transcript cannot be read or marks no run.</summary>
    public static long? Latest(string? transcriptPath, long? upTo = null) {
        try {
            if (transcriptPath is null || !File.Exists(transcriptPath)) return null;

            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var limit  = Math.Min(upTo ?? stream.Length, stream.Length);
            var line   = new MemoryStream();
            long start = 0, offset = 0;
            long? latest = null;
            int next;

            while (offset < limit && (next = stream.ReadByte()) >= 0) {
                offset++;

                if (next != '\n') {
                    line.WriteByte((byte) next);

                    continue;
                }

                if (StartsRun(line)) latest = start;
                line.SetLength(0);
                start = offset;
            }

            if (StartsRun(line)) latest = start;

            return latest;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static bool StartsRun(MemoryStream line) {
        if (line.Length == 0) return false;

        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int) line.Length);
        if (!text.Contains("\"session.start\"", StringComparison.Ordinal) && !text.Contains("\"session.resume\"", StringComparison.Ordinal)) return false;

        try {
            using var doc = JsonDocument.Parse(text);

            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.GetString() is "session.start" or "session.resume";
        } catch (JsonException) {
            return false;
        }
    }
}
