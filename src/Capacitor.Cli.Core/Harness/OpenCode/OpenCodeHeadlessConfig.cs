using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The whole configuration a headless OpenCode run gets: every tool off, every permission denied, and the user's
/// <c>model</c> and <c>provider</c> so the default stays theirs. <see cref="Permission"/> is the
/// <c>OPENCODE_PERMISSION</c> value, which outranks any config's permissions; <see cref="AuthContent"/> is the
/// user's <c>auth.json</c>, null when there is none.
/// </summary>
sealed record OpenCodeHeadlessConfig(string ConfigContent, string Permission, string? AuthContent) {
    const string ConfigFileVariable    = "OPENCODE_CONFIG";
    const string ConfigDirVariable     = "OPENCODE_CONFIG_DIR";
    const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";
    const string TestHomeVariable      = "OPENCODE_TEST_HOME";

    static readonly string[] GlobalFiles  = ["config.json", "opencode.json", "opencode.jsonc"];
    static readonly string[] DirFiles     = ["opencode.json", "opencode.jsonc"];
    static readonly string[] ManagedFiles = ["opencode.json", "opencode.jsonc", "config.json"];

    static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Reads the two keys from OpenCode's own layers, later winning: the XDG global config directory's
    /// files, the file <c>OPENCODE_CONFIG</c> names, <c>~/.opencode</c>, the directory <c>OPENCODE_CONFIG_DIR</c>
    /// names, then <c>OPENCODE_CONFIG_CONTENT</c>. Throws <see cref="HeadlessRunRefusedException"/> naming a layer
    /// that is present but cannot be read or parsed, since OpenCode would refuse it too.</summary>
    public static OpenCodeHeadlessConfig Build(OpenCodePaths paths, IReadOnlyDictionary<string, string?> inheritedEnv) {
        string? model    = null;
        var     provider = new JsonObject();

        foreach (var name in GlobalFiles) Merge(FromFile(Path.Combine(paths.GlobalConfigDir, name)));

        if (Set(ConfigFileVariable) is { } file) Merge(FromFile(file));

        var home = Set(TestHomeVariable) ?? paths.Home;
        foreach (var name in DirFiles) Merge(FromFile(Path.Combine(home, ".opencode", name)));

        if (Set(ConfigDirVariable) is { } dir) {
            foreach (var name in DirFiles) Merge(FromFile(Path.Combine(dir, name)));
        }

        if (Set(ConfigContentVariable) is { } inline) Merge(Parse(inline, ConfigContentVariable));

        var permission = PermissionDocument();
        var content = new JsonObject {
            ["tools"]      = new JsonObject { ["*"] = false },
            ["permission"] = permission.DeepClone(),
        };

        if (model is not null) content["model"] = (JsonNode?)model;
        if (provider.Count > 0) content["provider"] = provider;

        return new(content.ToJsonString(), permission.ToJsonString(), ReadAuth(Path.Combine(paths.DataDir, "auth.json")));

        string? Set(string variable) => inheritedEnv.GetValueOrDefault(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

        void Merge((JsonObject Layer, string Name)? source) {
            if (source is not var (layer, name)) return;

            if (layer["model"] is { } m) {
                model = m is JsonValue v && v.TryGetValue(out string? s)
                    ? s
                    : throw new HeadlessRunRefusedException($"OpenCode config {name}: 'model' is not a string.");
            }

            if (layer["provider"] is not { } p) return;

            if (p is not JsonObject entries) throw new HeadlessRunRefusedException($"OpenCode config {name}: 'provider' is not an object.");

            foreach (var (id, entry) in entries) {
                if (entry is not JsonObject value)
                    throw new HeadlessRunRefusedException($"OpenCode config {name}: provider '{id}' is not an object.");

                provider[id] = provider[id] is JsonObject earlier ? DeepMerge(earlier, value) : value.DeepClone();
            }
        }
    }

    /// <summary>OpenCode's own merge: objects merge key by key, anything else is replaced by the later value.</summary>
    static JsonObject DeepMerge(JsonObject earlier, JsonObject later) {
        var merged = earlier.DeepClone().AsObject();

        foreach (var (key, value) in later) {
            merged[key] = merged[key] is JsonObject a && value is JsonObject b ? DeepMerge(a, b) : value?.DeepClone();
        }

        return merged;
    }

    /// <summary>The directory an administrator's OpenCode config lives in, which outranks every other layer.</summary>
    public static string ManagedConfigDir =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "opencode")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/opencode"
        : "/etc/opencode";

    /// <summary>The first managed config file present in <paramref name="dir"/>, unread; null when there is none.</summary>
    public static string? ManagedConfigFile(string dir) =>
        ManagedFiles.Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    static JsonObject PermissionDocument() {
        var permission = new JsonObject { ["*"] = (JsonNode?)"deny" };

        foreach (var tool in OpenCodeForbiddenTools.Names) permission[tool] = (JsonNode?)"deny";

        return permission;
    }

    static (JsonObject, string)? FromFile(string path) {
        if (!File.Exists(path)) return null;

        string text;

        try {
            text = File.ReadAllText(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new HeadlessRunRefusedException($"OpenCode config '{path}' cannot be read: {ex.Message}");
        }

        return Parse(text, $"'{path}'");
    }

    static (JsonObject, string) Parse(string text, string name) {
        JsonNode? node;

        try {
            node = JsonNode.Parse(text, documentOptions: Jsonc);
        } catch (JsonException ex) {
            throw new HeadlessRunRefusedException($"OpenCode config {name} is not valid JSON: {ex.Message}");
        }

        return node is JsonObject layer ? (layer, name) : throw new HeadlessRunRefusedException($"OpenCode config {name} is not a JSON object.");
    }

    static string? ReadAuth(string path) {
        if (!File.Exists(path)) return null;

        try {
            var text = File.ReadAllText(path);

            return string.IsNullOrWhiteSpace(text) ? null : text;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new HeadlessRunRefusedException($"OpenCode credentials '{path}' cannot be read: {ex.Message}");
        }
    }
}
