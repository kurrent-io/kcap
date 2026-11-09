using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Config;

/// <summary>
/// <c>experimental.json</c>: <c>{ "&lt;profile&gt;": { "&lt;feature-id&gt;": true|false } }</c>. Unknown profiles
/// and ids are kept on write, and an unparseable file is never overwritten.
/// </summary>
public sealed class ExperimentalSettingsStore(ConfigRoot root) {
    public const string FileName = "experimental.json";

    public string Path { get; } = root.Path(FileName);

    public ExperimentalSettingsRead Load(string profile) {
        var (document, problem) = ReadDocument();
        if (problem is not null) return new(false, ExperimentalSettingsRead.Empty.Features, problem);

        var features = new Dictionary<string, bool>();
        if (document[profile] is JsonObject section)
            foreach (var (id, value) in section)
                if (value is JsonValue v && v.TryGetValue<bool>(out var on))
                    features[id] = on;

        return new(true, features, null);
    }

    /// <returns>False when the file is unreadable and was left untouched.</returns>
    /// <remarks>The lock wait (up to 10 s) and the file I/O run on the thread pool, so a UI thread can call this.</remarks>
    public Task<bool> SetAsync(string profile, string featureId, bool enabled, CancellationToken ct) =>
        Task.Run(() => Set(profile, featureId, enabled), ct);

    bool Set(string profile, string featureId, bool enabled) {
        using var _ = root.AcquireLock(FileName);

        var (document, problem) = ReadDocument();
        if (problem is not null) return false;

        if (document[profile] is not JsonObject section) {
            section = new JsonObject();
            document[profile] = section;
        }
        section[featureId] = enabled;

        Directory.CreateDirectory(root.Directory);
        // Windows refuses File.Move over a file any reader holds open; this rename gets past one that
        // shares delete, as ReadDocument's does, and the daemon's watcher and the app re-read it.
        AtomicFile.Replace(Path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return true;
    }

    (JsonObject Document, string? Problem) ReadDocument() {
        string text;
        try {
            if (!File.Exists(Path)) return (new JsonObject(), null);
            text = File.ReadAllTextShared(Path);
        } catch (IOException e) {
            return (new JsonObject(), $"{FileName} could not be read: {e.Message}");
        } catch (UnauthorizedAccessException e) {
            return (new JsonObject(), $"{FileName} could not be read: {e.Message}");
        }

        if (string.IsNullOrWhiteSpace(text)) return (new JsonObject(), $"{FileName} is empty");

        try {
            // A JsonObject with a duplicate key parses, then throws ArgumentException on first access.
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject obj
                ? (obj, null)
                : (new JsonObject(), $"{FileName} is not a JSON object");
        } catch (JsonException e) {
            return (new JsonObject(), $"{FileName} is not valid JSON: {e.Message}");
        }
    }
}
