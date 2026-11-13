using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Copilot's per-session <c>workspace.yaml</c> carries its own <c>name</c>, auto-generated unless
/// <c>user_named</c> is set — the same file <c>CopilotImportSource</c> reads for import. A session Copilot never
/// summarised is named with its first prompt, whole or cut mid-word; that is no title, so it reads as none and the
/// server titles the session from the prompt as it does any other harness's.</summary>
public sealed partial class CopilotWorkspaceTitle(string workspaceYamlPath) : IHarnessTitleStore {
    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        var meta = CopilotWorkspaceYaml.TryRead(workspaceYamlPath);

        if (string.IsNullOrWhiteSpace(meta?.Name)) return null;
        if (!meta.UserNamed && RepeatsFirstPrompt(meta.Name)) return null;

        return new StoreTitle(meta.Name.Trim(), meta.UserNamed ? HarnessTitleKind.Rename : HarnessTitleKind.Auto, null);
    }

    bool RepeatsFirstPrompt(string name) {
        if (FirstPrompt(Path.Combine(Path.GetDirectoryName(workspaceYamlPath) ?? "", "events.jsonl")) is not { } prompt) return false;

        var cut = Collapsed(name).TrimEnd('.', '…', ' ');

        return cut.Length > 0 && Collapsed(prompt).StartsWith(cut, StringComparison.Ordinal);
    }

    static string? FirstPrompt(string eventsJsonl) {
        try {
            foreach (var line in File.ReadLinesShared(eventsJsonl)) {
                if (!line.Contains("\"user.message\"", StringComparison.Ordinal)) continue;

                using var doc = JsonDocument.Parse(line);

                if (doc.RootElement.Str("type") == "user.message" && doc.RootElement.Obj("data")?.Str("content") is { Length: > 0 } content)
                    return content;
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // An unreadable transcript leaves the name as Copilot wrote it.
        }

        return null;
    }

    static string Collapsed(string text) => Whitespace().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
