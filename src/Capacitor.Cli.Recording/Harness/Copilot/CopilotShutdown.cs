using System.Text.Json;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>A Copilot session ends on its transcript's <c>session.shutdown</c> line, once that line has stayed
/// last long enough to rule out a resume.</summary>
public static class CopilotShutdown {

    /// <summary>How long <c>session.shutdown</c> must stay last before it counts as the session's end: a resume
    /// appends to the same file, and VS Code has written its resume line 0.9s after the shutdown.</summary>
    public static readonly TimeSpan CopilotShutdownGrace = TimeSpan.FromSeconds(30);

    /// <summary>A Local transcript has no shutdown line, and a child's end comes from its session.</summary>
    public static bool TracksCopilotShutdown(string vendor, string? agentId, string transcriptPath) =>
        vendor == "copilot" && agentId is null && !CopilotPaths.IsVsCodeLocalTranscript(transcriptPath);

    /// <summary>A shutdown written before this watcher started belongs to an earlier run: a resumed or forked session
    /// can sit on one until its first new line, with its runtime live. An unreadable timestamp counts as earlier.</summary>
    public static DateTimeOffset? CopilotShutdownAfter(DateTimeOffset? seenAt, IEnumerable<string> lines, DateTimeOffset now, DateTimeOffset watcherStartedAt) =>
        lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) switch {
            null                                                                                                   => seenAt,
            // Buffered lines are read again until they are sent, so a repeat must not restart the clock.
            var last when IsShutdownLine(last) && WrittenSince(last, watcherStartedAt) => seenAt ?? now,
            _                                                                                                      => null,
        };

    static bool WrittenSince(string line, DateTimeOffset since) {
        try {
            using var doc = JsonDocument.Parse(line);

            return doc.RootElement.Str("timestamp") is { } text
                && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                && at >= since;
        } catch (JsonException) {
            return false;
        }
    }

    public static bool CopilotShutdownSettled(DateTimeOffset? seenAt, DateTimeOffset now) =>
        seenAt is { } at && now - at >= CopilotShutdownGrace;

    /// <summary>Whether a transcript line is a <c>session.shutdown</c> envelope, rather than one quoting it.</summary>
    public static bool IsShutdownLine(string line) {
        if (!line.Contains("session.shutdown", StringComparison.Ordinal)) return false;

        try {
            using var doc = JsonDocument.Parse(line);

            return doc.RootElement.Str("type") == "session.shutdown";
        } catch (JsonException) {
            return false;
        }
    }
}
