using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>
/// Claude Code reopening a session records a <c>stopped</c> task notification for every background agent or shell
/// that never reported an end, and starts no turn on it, so an idle agent that lost work stays idle until something
/// else wakes it. Each notice is written once, so what counts is whether a turn followed it, not when it was written:
/// a second restart landing before anything answered it must not lose the work. Stateless, and never throws.
/// </summary>
public static class ClaudeLostWork {
    const string Stopped = "stopped";

    // The notices land as the session reopens, so they are the last thing in the file.
    const int TailBytes = 256 * 1024;

    /// <summary>Whether a notice was recorded with no turn after it. False for a file it cannot read.</summary>
    public static bool IsReported(string transcriptPath) {
        try {
            // The agent owns this file and appends to it live: the open must not deny writers.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            var unanswered = false;

            while (reader.ReadLine() is { } line) {
                if (IsTurn(line)) unanswered = false;
                else if (IsNotice(line)) unanswered = true;
            }

            return unanswered;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static bool IsTurn(string line) {
        if (!line.Contains("assistant", StringComparison.Ordinal)) return false;

        try {
            using var doc = JsonDocument.Parse(line);

            return doc.RootElement.Str("type") == "assistant";
        } catch (JsonException) {
            return false;
        }
    }

    static bool IsNotice(string line) {
        if (!line.Contains("task-notification", StringComparison.Ordinal)) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") == "user"
                && root.Obj("origin")?.Str("kind") == "task-notification"
                && root.Obj("message")?.Str("content") is { } text
                && TaskNotificationParser.TryParse(text, out var notice) && notice.Status == Stopped;
        } catch (JsonException) {
            return false;
        }
    }
}
