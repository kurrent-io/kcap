using System.Buffers;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// What a person approving a hosted Codex's app-server request is shown. Permission cards read Claude's tool
/// vocabulary, which Codex's hooks use for the same calls, so a plain command shows as <c>Bash</c> with the script it
/// runs, and a one-file edit as <c>Edit</c>, <c>MultiEdit</c> or <c>Write</c> with the file and its lines. A change that
/// one file cannot show goes as <c>apply_patch</c> naming every path, and any other request as it came. Only what is
/// shown changes: the answer goes back on the request itself.
/// </summary>
internal static class CodexApprovalCard {
    public static (string ToolName, JsonElement? ToolInput) For(string itemType, JsonElement? request, CodexFileChanges fileChanges) {
        if (request is not { } r) return (itemType, null);

        return itemType switch {
            "commandExecution" when CodexApprovalRequest.PlainCommand(r) is { } command => ("Bash", Shell(command, r)),
            "fileChange"                                                               => FileChange(r, fileChanges),
            _                                                                          => (itemType, r),
        };
    }

    static JsonElement Shell(string command, JsonElement request) =>
        Json(w => {
            w.WriteString("command", CodexShellCommand.Script(command));
            if (request.Str("cwd") is { } cwd) w.WriteString("cwd", cwd);
            Reason(w, request);
        });

    static (string, JsonElement) FileChange(JsonElement request, CodexFileChanges fileChanges) {
        JsonElement[] changes = request.Str("itemId") is { } itemId && fileChanges.Of(itemId) is { } observed && observed.IsArray
            ? [.. observed.EnumerateArray()]
            : [];

        // A grant root asks for writes under it for the rest of the session, beyond this edit's file.
        if (changes is [var change] && !CodexApprovalRequest.Names(request, "grantRoot") && change.Str("path") is { Length: > 0 } path) {
            switch (Kind(change), change.Obj("kind")?.Str("move_path")) {
                case ("add", _):
                    return ("Write", Json(w => {
                        w.WriteString("file_path", path);
                        w.WriteString("content", change.Str("diff") ?? "");
                        Reason(w, request);
                    }));
                case ("update", null):
                    return Edit(path, Hunks(change.Str("diff") ?? ""), request);
            }
        }

        return ("apply_patch", Json(w => {
            w.WriteStartArray("changes");
            foreach (var each in changes) {
                w.WriteStartObject();
                if (each.Str("path") is { } p) w.WriteString("path", p);
                if (Kind(each) is { } kind) w.WriteString("kind", kind);
                if (each.Obj("kind")?.Str("move_path") is { } move) w.WriteString("move_path", move);
                if (each.Str("diff") is { } diff) w.WriteString("diff", diff);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Reason(w, request);
        }));
    }

    static (string, JsonElement) Edit(string path, List<(string Old, string New)> hunks, JsonElement request) {
        if (hunks.Count <= 1) {
            var (old, @new) = hunks is [var only] ? only : ("", "");
            return ("Edit", Json(w => {
                w.WriteString("file_path", path);
                w.WriteString("old_string", old);
                w.WriteString("new_string", @new);
                Reason(w, request);
            }));
        }

        return ("MultiEdit", Json(w => {
            w.WriteString("file_path", path);
            w.WriteStartArray("edits");
            foreach (var (old, @new) in hunks) {
                w.WriteStartObject();
                w.WriteString("old_string", old);
                w.WriteString("new_string", @new);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Reason(w, request);
        }));
    }

    /// <summary>Each hunk of a unified diff as the lines it replaces and the lines it leaves. Anything before the
    /// first hunk is a header.</summary>
    static List<(string Old, string New)> Hunks(string diff) {
        var hunks = new List<(List<string> Old, List<string> New)>();

        foreach (var line in diff.TrimEnd('\n').Split('\n')) {
            if (line.StartsWith("@@", StringComparison.Ordinal)) {
                hunks.Add(([], []));
                continue;
            }
            if (hunks.Count == 0) continue;

            var (old, @new) = hunks[^1];
            switch (line.Length == 0 ? ' ' : line[0]) {
                case ' ':
                    old.Add(line.Length == 0 ? "" : line[1..]);
                    @new.Add(line.Length == 0 ? "" : line[1..]);
                    break;
                case '-': old.Add(line[1..]); break;
                case '+': @new.Add(line[1..]); break;
            }
        }

        return [.. hunks.Select(h => (string.Join('\n', h.Old), string.Join('\n', h.New)))];
    }

    // The schema's kind is an object naming its type; a bare string is read the same.
    static string? Kind(JsonElement change) => change.Obj("kind")?.Str("type") ?? change.Str("kind");

    // The PTY hook carries the same justification as the shell input's description.
    static void Reason(Utf8JsonWriter w, JsonElement request) {
        if (request.Str("reason") is { Length: > 0 } reason) w.WriteString("description", reason);
    }

    static JsonElement Json(Action<Utf8JsonWriter> body) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
