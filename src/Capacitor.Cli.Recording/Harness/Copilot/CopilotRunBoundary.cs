using System.Text.Json;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// The runs a Copilot transcript records, each named by the top-level <c>id</c> of the <c>session.start</c> or
/// <c>session.resume</c> line it began with. VS Code's Restore checkpoint rewrites the file in place, so a run is matched
/// by that id, which a rewrite keeping the line keeps, and never by where its line sits.
/// </summary>
public sealed record CopilotRunBoundary(string? LatestId, IReadOnlySet<string> Ids) {
    /// <summary>The runs <paramref name="transcriptPath"/> records, or null when it cannot be read.</summary>
    public static CopilotRunBoundary? Read(string? transcriptPath) {
        try {
            if (transcriptPath is null || !File.Exists(transcriptPath)) return null;

            var    ids    = new HashSet<string>(StringComparer.Ordinal);
            string? latest = null;

            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (RunIdOf(line) is not { } id) continue;

                ids.Add(id);
                latest = id;
            }

            return new CopilotRunBoundary(latest, ids);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>The id of the latest run <paramref name="transcriptPath"/> records, or null when it cannot be read or
    /// records none.</summary>
    public static string? LatestIdOf(string? transcriptPath) => Read(transcriptPath)?.LatestId;

    static string? RunIdOf(string line) {
        if (!line.Contains("\"session.start\"", StringComparison.Ordinal) && !line.Contains("\"session.resume\"", StringComparison.Ordinal)) return null;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.GetString() is "session.start" or "session.resume"
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        } catch (JsonException) {
            return null;
        }
    }
}
