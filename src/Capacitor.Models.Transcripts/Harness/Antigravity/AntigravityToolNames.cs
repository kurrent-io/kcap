using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// <summary>
/// Antigravity's tool names and parameters as the Claude names and input shapes every kcap surface already
/// reads. Only the listed keys carry over; a name not listed keeps its own name and parameters. The legacy
/// names (<c>view_file</c>, <c>write_to_file</c>, <c>list_dir</c>, <c>grep_search</c>, <c>find_by_name</c>)
/// are still written by older Antigravity builds and stay mapped.
/// </summary>
public static class AntigravityToolNames {
    static readonly FrozenDictionary<string, (string Name, (string From, string To)[] Keys)> Table =
        new Dictionary<string, (string, (string, string)[])>(StringComparer.Ordinal) {
            ["run_command"]   = ("Bash",      [("CommandLine", "command"), ("toolSummary", "description")]),
            ["read_file"]     = ("Read",      [("Path", "file_path")]),
            ["view_file"]     = ("Read",      [("AbsolutePath", "file_path")]),
            ["edit_file"]     = ("Edit",      [("Path", "file_path"), ("Target", "old_string"), ("Replacement", "new_string")]),
            ["write_file"]    = ("Write",     [("Path", "file_path"), ("Content", "content")]),
            ["write_to_file"] = ("Write",     [("TargetFile", "file_path"), ("CodeContent", "content")]),
            ["list_dir"]      = ("LS",        [("DirectoryPath", "path")]),
            ["grep_search"]   = ("Grep",      [("Query", "pattern"), ("SearchPath", "path")]),
            ["find_by_name"]  = ("Glob",      [("Pattern", "pattern"), ("SearchDirectory", "path")]),
            ["search_web"]    = ("WebSearch", [("query", "query")]),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static (string Name, string? InputJson) Translate(string name, string? parametersJson) {
        if (name == "call_mcp_tool") return Mcp(parametersJson) ?? (name, parametersJson);
        if (!Table.TryGetValue(name, out var entry)) return (name, parametersJson);
        if (Parse(parametersJson) is not { } doc) return (entry.Name, parametersJson);

        using (doc) {
            var root = doc.RootElement;
            return (entry.Name, Write(w => {
                foreach (var (from, to) in entry.Keys) {
                    if (root.Prop(from) is not { IsNull: false } value) continue;
                    w.WritePropertyName(to);
                    value.WriteTo(w);
                }
                if (entry.Name == "Read") WriteLineRange(w, root.Num("StartLine"), root.Num("EndLine"));
            }));
        }
    }

    static void WriteLineRange(Utf8JsonWriter w, long? start, long? end) {
        if (start is { } offset) w.WriteNumber("offset", offset);
        if (end is { } last && last >= (start ?? 1)) w.WriteNumber("limit", last - (start ?? 1) + 1);
    }

    static (string, string?)? Mcp(string? parametersJson) {
        if (Parse(parametersJson) is not { } doc) return null;

        using (doc) {
            var root = doc.RootElement;
            if (root.Str("ServerName") is not { Length: > 0 } server || root.Str("ToolName") is not { Length: > 0 } tool)
                return null;

            var input = root.Prop("Arguments") switch {
                { IsObject: true } args                                 => args.GetRawText(),
                { IsString: true } args when IsObject(args.GetString()) => args.GetString(),
                _                                                       => "{}",
            };
            return ($"mcp__{server}__{tool}", input);
        }
    }

    static bool IsObject(string? json) {
        if (Parse(json) is not { } doc) return false;
        doc.Dispose();
        return true;
    }

    static JsonDocument? Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.IsObject) return doc;
            doc.Dispose();
        } catch (JsonException) { }
        return null;
    }

    static string Write(Action<Utf8JsonWriter> body) {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
