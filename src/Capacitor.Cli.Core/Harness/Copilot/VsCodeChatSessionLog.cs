using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Reads VS Code's <c>chatSessions/{id}.jsonl</c>, a mutation log over its chat model: <c>kind</c> 0 is a full
/// snapshot, 1 sets the value at path <c>k</c>, 2 truncates the array at <c>k</c> to <c>i</c> and pushes <c>v</c>, 3
/// deletes. Past a few hundred entries VS Code rewrites the file as one fresh snapshot, so it is replayed whole, never
/// tailed. Its <c>version</c> stays put while fields come and go, so nothing here throws on shape: an entry or field
/// that is not what is expected is left out and named, and everything else is still read.
/// </summary>
public static class VsCodeChatSessionLog {
    public const long MaxBytes = 256L * 1024 * 1024;

    // Tool results are stored as render trees far deeper than the parser's default limit of 64.
    static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4096 };

    /// <summary>Null when there is nothing to read yet: the log is missing, locked, empty, or holds only a snapshot
    /// still being written.</summary>
    public static VsCodeChatSession? Read(string path, string sessionId) {
        string text;
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return new(null, [], [$"the log is {stream.Length} bytes, over the {MaxBytes}-byte limit"]);

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        return Replay(text, sessionId);
    }

    public static VsCodeChatSession? Replay(string text, string sessionId) {
        var       skipped = new List<string>();
        JsonNode? state   = null;
        var       lines   = text.Split('\n');
        var       pending = false;

        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            // A last line with no newline after it may still be being written.
            var unterminated = i == lines.Length - 1;

            JsonObject? entry;
            try {
                entry = JsonNode.Parse(line, null, DocumentOptions) as JsonObject;
            } catch (JsonException) {
                if (unterminated) pending = true;
                else skipped.Add($"line {i + 1} is not JSON");
                continue;
            }

            if (entry is null || Int(entry["kind"]) is not { } kind) {
                skipped.Add($"line {i + 1} has no kind");
                continue;
            }

            if (kind == 0) {
                state = Detach(entry, "v");
                continue;
            }

            if (kind is not (1 or 2 or 3)) {
                skipped.Add($"line {i + 1} has unknown kind {kind}");
                continue;
            }

            if (state is null) {
                skipped.Add($"line {i + 1} comes before any snapshot");
                continue;
            }

            if (!Apply(ref state, entry, kind)) skipped.Add($"line {i + 1} (kind {kind}) does not fit the snapshot");
        }

        if (state is not JsonObject root) {
            if (state is null && pending && skipped.Count == 0) return null;

            skipped.Add("the log has no snapshot");
            return new(null, [], skipped);
        }

        return Project(root, sessionId, skipped);
    }

    static bool Apply(ref JsonNode state, JsonObject entry, long kind) {
        if (entry["k"] is not JsonArray path || !path.All(IsSegment)) return false;

        if (kind == 1) {
            var value = Detach(entry, "v");
            if (path.Count == 0) {
                if (value is null) return false;
                state = value;
                return true;
            }

            return Walk(state, path, path.Count - 1) is { } parent && SetChild(parent, path[^1]!, value);
        }

        if (kind == 2) {
            var target = path.Count == 0 ? state : Walk(state, path, path.Count);
            if (target is not JsonArray array) {
                if (target is not null || path.Count == 0) return false;
                if (Walk(state, path, path.Count - 1) is not { } parent) return false;

                array = [];
                if (!SetChild(parent, path[^1]!, array)) return false;
            }

            if (entry.ContainsKey("i")) {
                if (Int(entry["i"]) is not { } length || length < 0) return false;
                while (array.Count > length) array.RemoveAt(array.Count - 1);
            }

            if (entry.ContainsKey("v")) {
                if (Detach(entry, "v") is not JsonArray items) return false;

                var moved = items.ToList();
                items.Clear();
                foreach (var item in moved) array.Add(item);
            }

            return true;
        }

        if (path.Count == 0 || Walk(state, path, path.Count - 1) is not { } owner) return false;

        switch (owner, Key(path[^1]!), Index(path[^1]!)) {
            case (JsonObject obj, { } key, _):
                obj.Remove(key);
                return true;
            case (JsonArray arr, _, { } index):
                if (index < arr.Count) arr[(int)index] = null;
                return true;
            default:
                return false;
        }
    }

    static JsonNode? Walk(JsonNode node, JsonArray path, int count) {
        JsonNode? current = node;
        for (var i = 0; i < count && current is not null; i++) current = Child(current, path[i]!);
        return current;
    }

    static JsonNode? Child(JsonNode node, JsonNode segment) => (node, Key(segment), Index(segment)) switch {
        (JsonObject obj, { } key, _)                             => obj[key],
        (JsonArray arr, _, { } index) when index < arr.Count => arr[(int)index],
        _                                                         => null,
    };

    static bool SetChild(JsonNode parent, JsonNode segment, JsonNode? value) {
        switch (parent, Key(segment), Index(segment)) {
            case (JsonObject obj, { } key, _):
                obj[key] = value;
                return true;
            case (JsonArray arr, _, { } index) when index < arr.Count:
                arr[(int)index] = value;
                return true;
            case (JsonArray arr, _, { } index) when index == arr.Count:
                arr.Add(value);
                return true;
            default:
                return false;
        }
    }

    static bool IsSegment(JsonNode? segment) => segment is not null && (Key(segment) is not null || Index(segment) is not null);

    static string? Key(JsonNode segment) => Str(segment);

    static long? Index(JsonNode segment) => Int(segment) is { } n && n >= 0 && n < int.MaxValue ? n : null;

    static JsonNode? Detach(JsonObject entry, string name) {
        var value = entry[name];
        entry.Remove(name);
        return value;
    }

    static VsCodeChatSession Project(JsonObject root, string sessionId, List<string> skipped) {
        if (root["sessionId"] is { } idNode) {
            if (Str(idNode) is not { } id) {
                skipped.Add("sessionId is not a string");
            } else if (!SameSession(id, sessionId)) {
                skipped.Add($"the log is session {id}'s");
                return new(null, [], skipped);
            }
        }

        string? title = null;
        if (root["customTitle"] is { } titleNode) {
            if (Str(titleNode) is { } raw) title = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            else skipped.Add("customTitle is not a string");
        }

        var requests = new List<VsCodeChatRequest>();
        switch (root["requests"]) {
            case null:
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) {
                    if (ProjectRequest(array[i], i, skipped) is { } request) requests.Add(request);
                }
                break;
            default:
                skipped.Add("requests is not an array");
                break;
        }

        return new(title, requests, skipped);
    }

    static VsCodeChatRequest? ProjectRequest(JsonNode? node, int index, List<string> skipped) {
        if (node is not JsonObject request) {
            skipped.Add($"requests[{index}] is not an object");
            return null;
        }

        if (Str(request["requestId"]) is not { Length: > 0 } requestId) {
            skipped.Add($"requests[{index}] has no requestId");
            return null;
        }

        var complete = false;
        if (request["modelState"] is { } state) {
            if (state is JsonObject stateObj && Int(stateObj["value"]) is { } value) complete = value == 1;
            else skipped.Add($"requests[{index}].modelState has no numeric value");
        }

        long? completionTokens = null;
        if (request["completionTokens"] is { } tokens) {
            if (Int(tokens) is { } n && n >= 0) completionTokens = n;
            else skipped.Add($"requests[{index}].completionTokens is not a count");
        }

        double? credits = null;
        if (request["copilotCredits"] is { } creditsNode) {
            if (Dbl(creditsNode) is { } c && double.IsFinite(c) && c >= 0) credits = c;
            else skipped.Add($"requests[{index}].copilotCredits is not a non-negative number");
        }

        return new(requestId, complete, completionTokens, credits, Model(request, index, skipped));
    }

    // The extension names the model it resolved an alias to; the core's auto-mode part says the same when it is not there.
    static string? Model(JsonObject request, int index, List<string> skipped) {
        if (request["result"] is JsonObject result && result["metadata"] is JsonObject metadata
         && metadata["resolvedModel"] is { } resolved) {
            if (Str(resolved) is { Length: > 0 } model) return model;
            skipped.Add($"requests[{index}].result.metadata.resolvedModel is not a string");
        }

        if (request["response"] is not JsonArray parts) return null;

        string? fromPart = null;
        foreach (var part in parts) {
            if (part is JsonObject obj && Str(obj["kind"]) == "autoModeResolution"
             && obj["resolved"] is JsonObject chosen && Str(chosen["id"]) is { Length: > 0 } id) {
                fromPart = id;
            }
        }

        return fromPart;
    }

    static bool SameSession(string a, string b) =>
        string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    static JsonElement? Element(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<JsonElement>(out var element) ? element : null;

    static string? Str(JsonNode? node) => Element(node) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    static long? Int(JsonNode? node) => Element(node) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var n) ? n : null;

    static double? Dbl(JsonNode? node) => Element(node) is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var d) ? d : null;
}
