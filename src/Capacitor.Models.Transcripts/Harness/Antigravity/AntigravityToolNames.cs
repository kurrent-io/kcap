using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// <summary>
/// Antigravity's tool calls as the Claude names and argument keys every kcap surface already reads, exactly as
/// kcap-server's normalizer translates them: mapped keys are renamed in place, native keys with no Claude
/// counterpart are kept beside them, a <c>file://</c> path loses its scheme, and a call whose shape does not match
/// keeps its native name and arguments. The legacy names (<c>view_file</c>, <c>write_to_file</c>,
/// <c>replace_file_content</c>, <c>list_dir</c>, <c>grep_search</c>, <c>find_by_name</c>) are still written by
/// older Antigravity builds and stay mapped.
/// </summary>
public static class AntigravityToolNames {
    public static (string Name, JsonObject? Arguments) Translate(string name, JsonObject? native) {
        if (native is null) return (name, null);

        var translated = name switch {
            "run_command"   => Rename(native, "Bash", "CommandLine", ("CommandLine", "command"), ("toolSummary", "description")),
            "read_file"     => Read(native, "Path"),
            "view_file"     => Read(native, "AbsolutePath"),
            "edit_file"     => Rename(native, "Edit", "Path", ("Path", "file_path"), ("Target", "old_string"), ("Replacement", "new_string")),
            "write_file"    => Rename(native, "Write", "Path", ("Path", "file_path"), ("Content", "content")),
            "write_to_file" => Rename(native, "Write", "TargetFile", ("TargetFile", "file_path"), ("CodeContent", "content")),
            "replace_file_content" or "multi_replace_file_content" => Replace(native),
            "list_dir"      => Rename(native, "LS", "DirectoryPath", ("DirectoryPath", "path")),
            "grep_search"   => Rename(native, "Grep", "Query", ("Query", "pattern"), ("SearchPath", "path")),
            "find_by_name"  => Rename(native, "Glob", "Pattern", ("Pattern", "pattern"), ("SearchDirectory", "path")),
            "search_web"    => Rename(native, "WebSearch", "query"),
            "call_mcp_tool" => Mcp(native),
            _               => null,
        };

        return translated ?? (name, native);
    }

    /// The same translation over serialized parameters; parameters that are not a JSON object, and a call it does
    /// not translate, come back as they went in.
    public static (string Name, string? InputJson) Translate(string name, string? parametersJson) {
        if (ParseObject(parametersJson) is not { } native) return (name, parametersJson);

        var (translated, arguments) = Translate(name, native);
        return translated == name ? (name, parametersJson) : (translated, arguments?.ToJsonString());
    }

    static (string, JsonObject)? Rename(JsonObject native, string name, string required, params (string From, string To)[] keys) {
        if (Text(native, required) is null) return null;

        var mapped = new JsonObject();
        foreach (var (key, value) in native) {
            var to = keys.FirstOrDefault(k => k.From == key).To;
            mapped[to ?? key] = to is "file_path" or "path" ? PathValue(value) : value?.DeepClone();
        }

        return (name, mapped);
    }

    static (string, JsonObject)? Read(JsonObject native, string pathKey) {
        if (Text(native, pathKey) is null) return null;

        var start  = Line(native, "StartLine");
        var end    = Line(native, "EndLine");
        var mapped = new JsonObject();

        foreach (var (key, value) in native) {
            if (key == pathKey) mapped["file_path"] = PathValue(value);
            else if (key is not ("StartLine" or "EndLine")) mapped[key] = value?.DeepClone();
        }

        if (start is not null) mapped["offset"] = start;
        if (end is { } last && last >= (start ?? 1)) mapped["limit"] = last - (start ?? 1) + 1;

        return ("Read", mapped);
    }

    static (string, JsonObject)? Replace(JsonObject native) {
        if (Text(native, "TargetFile") is null) return null;

        var edits = new JsonArray();
        if (native["ReplacementChunks"] is JsonArray chunks) {
            foreach (var chunk in chunks.OfType<JsonObject>())
                if (Text(chunk, "TargetContent") is { } before && Text(chunk, "ReplacementContent") is { } after)
                    edits.Add(Edit(before, after));
        } else if (Text(native, "TargetContent") is { } before && Text(native, "ReplacementContent") is { } after) {
            edits.Add(Edit(before, after));
        }

        if (edits.Count == 0) return null;

        var mapped = new JsonObject { ["file_path"] = PathValue(native["TargetFile"]) };
        if (edits.Count == 1) {
            mapped["old_string"] = edits[0]!["old_string"]!.DeepClone();
            mapped["new_string"] = edits[0]!["new_string"]!.DeepClone();
        } else {
            mapped["edits"] = edits;
        }

        foreach (var (key, value) in native)
            if (key is not ("TargetFile" or "TargetContent" or "ReplacementContent" or "ReplacementChunks"))
                mapped[key] = value?.DeepClone();

        return (edits.Count == 1 ? "Edit" : "MultiEdit", mapped);
    }

    static JsonNode Edit(string before, string after) => new JsonObject { ["old_string"] = before, ["new_string"] = after };

    static (string, JsonObject)? Mcp(JsonObject native) {
        if (Text(native, "ServerName") is not { Length: > 0 } server || Text(native, "ToolName") is not { Length: > 0 } tool) return null;

        var arguments = native["Arguments"] switch {
            JsonObject obj                                              => obj.DeepClone().AsObject(),
            JsonValue value when value.TryGetValue<string>(out var json) => ParseObject(json),
            _                                                           => null,
        };

        return ($"mcp__{server}__{tool}", arguments ?? new JsonObject());
    }

    static JsonObject? ParseObject(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            return JsonNode.Parse(json) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static long? Line(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<long>(out var line) ? line : null;

    static JsonNode? PathValue(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var path) && path.StartsWith("file://", StringComparison.Ordinal)
            ? JsonValue.Create(path["file://".Length..])
            : value?.DeepClone();
}
