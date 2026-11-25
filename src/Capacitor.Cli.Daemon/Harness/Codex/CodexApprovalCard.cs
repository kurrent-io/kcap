using System.Buffers;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// What a person approving a hosted Codex's app-server request is shown. Permission cards read Claude's tool
/// vocabulary, which Codex's hooks use for the same calls, so a plain command shows as <c>Bash</c> with the script it
/// runs, and a one-file edit as <c>Edit</c>, <c>MultiEdit</c> or <c>Write</c> with the file and its lines. A change that
/// one file's lines cannot show goes as <c>apply_patch</c> with every path, its raw diff and any grant beyond them; one
/// whose changes are not yet known, and any other request, as it came. Only what is shown changes: the answer goes back
/// on the request itself.
/// </summary>
internal static class CodexApprovalCard {
    public static (string ToolName, JsonElement? ToolInput) For(string itemType, JsonElement? request, CodexFileChanges fileChanges) {
        if (request is not { } r) return (itemType, null);

        return itemType switch {
            "commandExecution" when CodexApprovalRequest.PlainCommand(r) is { } command => ("Bash", Shell(command, r)),
            "fileChange"                                                               => FileChange(itemType, r, fileChanges),
            _                                                                          => (itemType, r),
        };
    }

    static JsonElement Shell(string command, JsonElement request) =>
        Json(w => {
            w.WriteString("command", CodexShellCommand.Script(command));
            if (request.Str("cwd") is { } cwd) w.WriteString("cwd", cwd);
            Reason(w, request);
        });

    static (string, JsonElement) FileChange(string itemType, JsonElement request, CodexFileChanges fileChanges) {
        // Until its changes are known the card cannot say what the edit is: an empty patch would read as no change.
        if (request.Str("itemId") is not { } itemId || fileChanges.Of(itemId) is not { } observed || !observed.IsArray)
            return (itemType, request);

        JsonElement[] changes = [.. observed.EnumerateArray()];

        // A grant root asks for writes under it for the rest of the session, beyond this edit's file.
        if (changes is [var change] && !CodexApprovalRequest.Names(request, "grantRoot") && change.Str("path") is { Length: > 0 } path) {
            switch (Kind(change), change.Obj("kind")?.Str("move_path")) {
                case ("add", _):
                    return ("Write", Json(w => {
                        w.WriteString("file_path", path);
                        w.WriteString("content", change.Str("diff") ?? "");
                        Reason(w, request);
                    }));
                case ("update", null) when Hunks(change.Str("diff") ?? "") is { Count: > 0 } hunks && hunks.All(Shows):
                    return Edit(path, hunks, request);
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

            if (request.Str("grantRoot") is { } root) {
                var grant = $"Write under {root} for the rest of the session";
                w.WriteString("grant_root", root);
                w.WriteString("description", request.Str("reason") is { Length: > 0 } reason ? $"{grant}: {reason}" : grant);
            } else {
                Reason(w, request);
            }
        }));
    }

    // A card folds CRLF into LF and drops a missing final newline, so a hunk whose sides read the same there changes
    // only what its lines cannot show.
    static bool Shows((string Old, string New) hunk) =>
        !string.Equals(hunk.Old.Replace("\r\n", "\n"), hunk.New.Replace("\r\n", "\n"), StringComparison.Ordinal);

    static (string, JsonElement) Edit(string path, List<(string Old, string New)> hunks, JsonElement request) =>
        hunks is [var (old, @new)]
            ? ("Edit", Json(w => {
                w.WriteString("file_path", path);
                w.WriteString("old_string", old);
                w.WriteString("new_string", @new);
                Reason(w, request);
            }))
            : ("MultiEdit", Json(w => {
                w.WriteString("file_path", path);
                w.WriteStartArray("edits");
                foreach (var (o, n) in hunks) {
                    w.WriteStartObject();
                    w.WriteString("old_string", o);
                    w.WriteString("new_string", n);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Reason(w, request);
            }));

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
