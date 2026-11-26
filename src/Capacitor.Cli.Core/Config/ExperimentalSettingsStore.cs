using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Config;

/// <summary>
/// <c>experimental.json</c>: <c>{ "&lt;profile&gt;": { "&lt;feature-id&gt;": true|false } }</c>. Unknown profiles
/// and ids are kept on write. App-wide flags live in a separate flat object. Unparseable files are never overwritten.
/// </summary>
public sealed class ExperimentalSettingsStore(ConfigRoot root) {
    public const string FileName = "experimental.json";

    const string AppWideFileName = "desktop-experimental.json";

    public string Path { get; } = root.Path(FileName);
    public string AppWidePath { get; } = root.Path(AppWideFileName);

    public ExperimentalSettingsRead Load(string profile) => Load(Path, profile);
    public ExperimentalSettingsRead LoadAppWide() => Load(AppWidePath, profile: null);

    static ExperimentalSettingsRead Load(string path, string? profile) {
        var (document, problem) = ReadDocument(path);
        if (problem is not null) return new(false, ExperimentalSettingsRead.Empty.Features, problem);

        var features = new Dictionary<string, bool>();
        var section = profile is null ? document : document[profile] as JsonObject;
        if (section is not null)
            foreach (var (id, value) in section) {
                if (value is JsonValue v && v.TryGetValue<bool>(out var on))
                    features[id] = on;
            }

        return new(true, features, null);
    }

    /// <returns>False when the file is unreadable and was left untouched.</returns>
    /// <remarks>The lock wait (up to 10 s) and the file I/O run on the thread pool, so a UI thread can call this.</remarks>
    public Task<bool> SetAsync(string profile, string featureId, bool enabled, CancellationToken ct) =>
        Task.Run(() => Set(profile, featureId, enabled), ct);

    bool Set(string profile, string featureId, bool enabled) {
        var appWide = ExperimentalFeatures.Find(featureId)?.AppWide == true;
        var path = appWide ? AppWidePath : Path;
        using var _ = root.AcquireLock(appWide ? AppWideFileName : FileName);

        var (document, problem) = ReadDocument(path);
        if (problem is not null) return false;

        var section = document;
        if (!appWide) {
            if (document[profile] is not JsonObject profileSettings) {
                profileSettings = new JsonObject();
                document[profile] = profileSettings;
            }
            section = profileSettings;
        }
        section[featureId] = enabled;

        Directory.CreateDirectory(root.Directory);
        // Windows refuses File.Move over a file any reader holds open; this rename gets past one that
        // shares delete, as ReadDocument's does, and the daemon's watcher and the app re-read it.
        AtomicFile.Replace(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return true;
    }

    static (JsonObject Document, string? Problem) ReadDocument(string path) {
        var name = System.IO.Path.GetFileName(path);
        string text;
        try {
            if (!File.Exists(path)) return (new JsonObject(), null);
            text = File.ReadAllTextShared(path);
        } catch (IOException e) {
            return (new JsonObject(), $"{name} could not be read: {e.Message}");
        } catch (UnauthorizedAccessException e) {
            return (new JsonObject(), $"{name} could not be read: {e.Message}");
        }

        if (string.IsNullOrWhiteSpace(text)) return (new JsonObject(), $"{name} is empty");

        try {
            // A JsonObject with a duplicate key parses, then throws ArgumentException on first access.
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject obj
                ? (obj, null)
                : (new JsonObject(), $"{name} is not a JSON object");
        } catch (JsonException e) {
            return (new JsonObject(), $"{name} is not valid JSON: {e.Message}");
        }
    }
}
