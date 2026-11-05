using System.Globalization;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>
/// Claude Code reopening a session records a <c>stopped</c> task notification for every background agent or shell
/// that never reported an end, and starts no turn on it, so an idle agent that lost work stays idle until something
/// else wakes it. Stateless, and never throws.
/// </summary>
public static class ClaudeLostWork {
    const string Stopped = "stopped";

    // The notices land as the session reopens, so they are the last thing in the file.
    const int TailBytes = 256 * 1024;

    /// <summary>Whether a notice was recorded at or after <paramref name="since"/>. False for a file it cannot read.</summary>
    public static bool IsReported(string transcriptPath, DateTimeOffset since) {
        try {
            // The agent owns this file and appends to it live: the open must not deny writers.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
                if (IsNotice(line, since)) return true;

            return false;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static bool IsNotice(string line, DateTimeOffset since) {
        if (!line.Contains("task-notification", StringComparison.Ordinal)) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") == "user"
                && root.Obj("origin")?.Str("kind") == "task-notification"
                && DateTimeOffset.TryParse(root.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at >= since
                && root.Obj("message")?.Str("content") is { } text
                && TaskNotificationParser.TryParse(text, out var notice) && notice.Status == Stopped;
        } catch (JsonException) {
            return false;
        }
    }
}
