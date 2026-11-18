using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// A chat VS Code moved from its Local target to Copilot. Its transcript starts with a <c>session.start</c> whose
/// producer is <see cref="Producer"/>, repeats the Local chat's history up to its first <c>session.resume</c>, and names
/// no Local chat. The repeated prompts keep the Local chat log's request times to the millisecond, so the first one,
/// with its text, finds the Local chat among VS Code's chat logs.
/// </summary>
public static class CopilotMovedChat {
    public const string Producer = "vscode-copilot-migration";

    public const string MarkerType = "kcap.vscode_moved_from";

    /// <summary>The marker's line number: once per transcript, out of the server's transcript watermark and clear of the
    /// Local usage lines, which start one above it.</summary>
    public const int MarkerLineNumber = 1_500_000_000;

    /// <summary>The first copied prompt of a moved chat's transcript, and the line and time of the resume that ends its
    /// history.</summary>
    public sealed record History(string Cwd, long FirstPromptAtMs, string FirstPrompt, int ResumeLine, DateTimeOffset ResumedAt);

    /// <summary>The history of a moved chat, read from its transcript's lines up to its first resume; null for any
    /// other transcript, or one whose history has not ended yet.</summary>
    public static History? Read(IEnumerable<string> lines) {
        string? cwd    = null;
        long?   at     = null;
        string? prompt = null;
        var     number = -1;

        foreach (var line in lines) {
            number++;

            JsonObject? record;
            try { record = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { record = null; }

            var type = Str(record?["type"]);
            var data = record?["data"] as JsonObject;

            if (number == 0) {
                if (type != "session.start" || Str(data?["producer"]) != Producer) return null;

                cwd = Str((data?["context"] as JsonObject)?["cwd"]);
                continue;
            }

            if (type == "session.resume") {
                return cwd is { Length: > 0 } && at is { } sent && prompt is not null
                    ? new History(cwd, sent, prompt, number, DateTimeOffset.TryParse(Str(record?["timestamp"]), out var resumed) ? resumed : DateTimeOffset.UnixEpoch)
                    : null;
            }

            if (type == "user.message" && prompt is null && Str(data?["content"]) is { } content
             && DateTimeOffset.TryParse(Str(record?["timestamp"]), out var time)) {
                prompt = content;
                at     = time.ToUnixTimeMilliseconds();
            }
        }

        return null;
    }

    /// <summary>The Local chat whose request matches the history's first prompt, searched in the VS Code workspaces
    /// open on the chat's folder; null when none matches, or when more than one does.</summary>
    public static string? FindLocalChat(IReadOnlyList<string> workspaceStorageDirs, History history) {
        string? found = null;

        foreach (var chatLog in ChatLogsFor(workspaceStorageDirs, history.Cwd)) {
            var id = Path.GetFileNameWithoutExtension(chatLog);

            if (VsCodeChatSessionLog.Read(chatLog, id) is not { } session
             || !session.Requests.Any(r => r.SentAtMs == history.FirstPromptAtMs && r.Text == history.FirstPrompt)) continue;

            if (found is not null) return null;

            found = id;
        }

        return found;
    }

    /// <summary>The marker naming the Local chat. Its id comes from the two chats, so a re-sent marker dedupes.</summary>
    public static string Marker(string movedSessionId, string localSessionId, DateTimeOffset at) => new JsonObject {
        ["type"]      = MarkerType,
        ["id"]        = TranscriptIds.Hash(Encoding.UTF8.GetBytes($"copilot-moved-from:{movedSessionId}:{localSessionId}")).ToString("D"),
        ["timestamp"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["data"]      = new JsonObject { ["sessionId"] = localSessionId },
    }.ToJsonString();

    static IEnumerable<string> ChatLogsFor(IReadOnlyList<string> workspaceStorageDirs, string cwd) {
        foreach (var storage in workspaceStorageDirs) {
            string[] workspaces;
            try { workspaces = Directory.GetDirectories(storage); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var workspace in workspaces) {
                if (!OpenOn(Path.Combine(workspace, "workspace.json"), cwd)) continue;

                string[] logs;
                try { logs = Directory.GetFiles(Path.Combine(workspace, "chatSessions"), "*.jsonl"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                foreach (var log in logs) yield return log;
            }
        }
    }

    // A single-folder workspace names its folder; a multi-root one names its .code-workspace file, whose first folder is
    // the chat's cwd.
    static bool OpenOn(string workspaceJson, string cwd) {
        JsonObject? workspace;
        try { workspace = JsonNode.Parse(File.ReadAllText(workspaceJson)) as JsonObject; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }

        if (LocalPath(Str(workspace?["folder"])) is { } folder) return SamePath(folder, cwd);

        if (LocalPath(Str(workspace?["workspace"])) is not { } file) return false;

        JsonObject? definition;
        try { definition = JsonNode.Parse(File.ReadAllText(file), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }

        if ((definition?["folders"] as JsonArray)?.FirstOrDefault() is not JsonObject first || Str(first["path"]) is not { } path) return false;

        return SamePath(Path.GetFullPath(path, Path.GetDirectoryName(file)!), cwd);
    }

    static string? LocalPath(string? uri) =>
        uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
