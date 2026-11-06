using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Recording.Harness.Codex;
using Capacitor.Cli.Recording.Harness.Gemini;

namespace Capacitor.Cli.Recording;

/// <summary>What a transcript line says, per vendor: the prompts and replies titles are made from, and
/// whether a line is a turn at all.</summary>
public static partial class TranscriptLineText {

    static readonly Regex CommandNameRegex = CommandNameRx();

    /// <summary>
    /// A prompt the user typed or a reply from the agent. Slash commands, injected preludes and
    /// session headers are not turns, so a session holding only those sends no lines.
    /// </summary>

    public static bool IsRealTurnLine(string line, string vendor, OnceLog? parseErrors = null) =>
        vendor == "gemini"
            ? GeminiLines.IsImportRelevantLine(line)
            : TryExtractAssistantText(line, vendor) is not null
           || (TryExtractUserText(line, vendor, parseErrors) is { } prompt && !string.IsNullOrWhiteSpace(prompt) && !CommandNameRegex.IsMatch(prompt))
           || (vendor == "claude" && IsClaudeImagePrompt(line));

    /// <summary>A Claude prompt that is only an attached image has no text to extract, yet is the user's turn.</summary>
    static bool IsClaudeImagePrompt(string line) {
        if (!line.Contains("\"image\"", StringComparison.Ordinal)) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") == "user"
                && root.Bool("isMeta") != true
                && root.Obj("message")?.Arr("content") is { } content
                && content.EnumerateArray().Any(block => block.Str("type") == "image");
        } catch (JsonException) {
            return false;
        }
    }

    /// <summary>Whether a session watcher's held-back lines go to the server: once they hold a real turn, or
    /// reach <see cref="WatchState.TranscriptThreshold"/> whatever they hold.</summary>
    public static bool ShouldFlushBuffer(IReadOnlyList<string> buffered, string vendor, OnceLog? parseErrors = null) =>
        buffered.Count >= WatchState.TranscriptThreshold || buffered.Any(line => IsRealTurnLine(line, vendor, parseErrors));

    /// <summary>
    /// Whether the transcript from <paramref name="fromLine"/> on holds a real turn. Read at shutdown, so a
    /// turn written after the last poll still counts; a transcript that was never written holds none.
    /// </summary>
    public static bool UnsentLinesHoldRealTurn(string transcriptPath, int fromLine, string vendor, OnceLog? parseErrors = null) {
        try {
            return File.ReadLinesShared(transcriptPath).Skip(fromLine)
                .Any(line => !string.IsNullOrWhiteSpace(line) && IsRealTurnLine(line, vendor, parseErrors));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>
    /// Records the session's first prompt, unless it is a slash command: the next real prompt titles the
    /// session instead.
    /// </summary>

    public static void SetFirstUserText(WatchState state, string userText, Action<string> log) {
        var cmdMatch = CommandNameRegex.Match(userText);

        if (cmdMatch.Success) {
            // Skip slash commands — wait for the next real user prompt to generate the title
            log($"Skipping slash command /{cmdMatch.Groups[1].Value} for title generation");

            return;
        }

        state.FirstUserText = userText;
    }

    public static string? TryExtractAssistantText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"    => TryExtractCodexAssistantText(line),
            "copilot"  => TryExtractCopilotAssistantText(line),
            "kiro"     => TryExtractKiroAssistantText(line),
            "pi"       => TryExtractPiAssistantText(line),
            "opencode" => TryExtractOpenCodeText(line, "assistant"),
            "antigravity" => TryExtractAntigravityText(line, "assistant"),
            "grok"     => TryExtractGrokText(line, "agent_message_chunk"),
            _          => TryExtractClaudeAssistantText(line)
        };

    static string? TryExtractClaudeAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant") {
                return null;
            }

            if (root.Obj("message")?.Arr("content") is not { } content) {
                return null;
            }

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") == "text") {
                    var text = block.Str("text")?.Trim();

                    if (!string.IsNullOrEmpty(text)) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractCodexAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "assistant") return null;

            return CodexLineText.ExtractCodexBlockText(payload.Value, "output_text")?.Trim() is { Length: > 0 } text ? text : null;
        } catch {
            return null;
        }
    }

    public static bool IsEvent(string line, string vendor = "claude") {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (vendor == "copilot") {
                // user.message / assistant.message are the only conversational
                // envelopes; everything else (hook.*, system.*, tool.*,
                // session.*) is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("type") is "user.message" or "assistant.message";
            }

            if (vendor == "kiro") {
                // Prompt / AssistantMessage are the conversational lines;
                // ToolResults is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("kind") is "Prompt" or "AssistantMessage";
            }

            if (vendor == "codex") {
                // Codex rolls everything into a top-level response_item envelope;
                // a "message" payload (user or assistant) is the analog of Claude's
                // user/assistant event for title-threshold purposes. User-role
                // payloads that are codex-injected preludes
                // (<environment_context>, AGENTS.md, <turn_aborted>) must NOT
                // count — otherwise a fresh session with no real prompts can
                // reach the 5-event threshold and produce a low-context title.
                if (root.Str("type") != "response_item") return false;

                var payload = root.Obj("payload");

                if (payload?.Str("type") != "message") return false;

                var role = payload.Value.Str("role");

                return role switch {
                    "assistant" => true,
                    "user"      => CodexLineText.ExtractCodexBlockText(payload.Value, "input_text") is { } text
                                && !CodexLineText.IsCodexInjectedUserPrelude(text),
                    _           => false
                };
            }

            if (vendor == "pi") {
                // Pi conversational envelopes are type:"message" with
                // message.role user/assistant (Pi has no top-level user/assistant
                // type). Gate on CONTENT, not just role: an empty user/assistant
                // envelope produces no canonical event (the server's NormalizeUser
                // returns null for empty content, NormalizeAssistant for zero
                // parts), so it must not count toward the title-event threshold
                // either. Mirrors PiImportSource.IsImportRelevantLine.
                if (root.Str("type") != "message") return false;
                if (root.Obj("message") is not { } piMsg) return false;

                return piMsg.Str("role") switch {
                    "user"      => PiContent.HasUserContent(piMsg),
                    "assistant" => PiContent.HasAssistantContent(piMsg),
                    _           => false
                };
            }

            if (vendor == "opencode") {
                // OpenCode envelopes are {info,parts} with info.role user/assistant.
                // Gate on text content: a turn with no non-hidden text (e.g. tool-only)
                // yields no titleable text, so it must not count toward the title-event
                // threshold. Mirrors the Pi content gate.
                if (root.Obj("info")?.Str("role") is not ("user" or "assistant")) return false;

                return OpenCodeTextParts(root.Arr("parts")) is not null;
            }

            if (vendor == "antigravity") {
                // Antigravity transcript_full.jsonl lines carry a `type`; only the two
                // conversational steps count toward the title-event threshold, and only
                // when they have text (a tool-only PLANNER_RESPONSE or an empty prompt
                // yields no titleable text). Everything else (RUN_COMMAND, VIEW_FILE,
                // GENERIC, CHECKPOINT, INVOKE_SUBAGENT, SYSTEM_*) is plumbing.
                return root.Str("type") switch {
                    "USER_INPUT"       => TryExtractAntigravityText(line, "user")      is not null,
                    "PLANNER_RESPONSE" => TryExtractAntigravityText(line, "assistant") is not null,
                    _                  => false
                };
            }

            if (vendor == "grok") return TryExtractGrokText(line, "user_message_chunk") is not null
                                      || TryExtractGrokText(line, "agent_message_chunk") is not null;

            return root.Str("type") is "user" or "assistant";
        } catch {
            return false;
        }
    }

    public static string? TryExtractUserText(string line, string vendor = "claude", OnceLog? parseErrors = null) =>
        vendor switch {
            "codex"    => TryExtractCodexUserText(line, parseErrors),
            "copilot"  => TryExtractCopilotUserText(line),
            "kiro"     => TryExtractKiroUserText(line),
            "pi"       => TryExtractPiUserText(line),
            "opencode" => TryExtractOpenCodeText(line, "user"),
            "antigravity" => TryExtractAntigravityText(line, "user"),
            "grok"     => TryExtractGrokText(line, "user_message_chunk"),
            _          => TryExtractClaudeUserText(line, parseErrors)
        };

    // Grok's updates.jsonl lines are ACP session/update notifications, each text run whole.
    static string? TryExtractGrokText(string line, string sessionUpdate) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("method") != "session/update") return null;
            if (root.Obj("params")?.Obj("update") is not { } update || update.Str("sessionUpdate") != sessionUpdate) return null;

            return update.Obj("content")?.Str("text")?.Trim() is { Length: > 0 } text ? text : null;
        } catch {
            return null;
        }
    }

    // ── OpenCode extractors ───────────────────────────────────────────
    //
    // OpenCode transcript lines are {info,parts} with info.role user/assistant. Title
    // text is the joined non-hidden text parts — mirrors the server's
    // OpenCodeTranscriptNormalizer.ExtractText so the watcher's titles match ingest.
    static string? TryExtractOpenCodeText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("info")?.Str("role") != role) return null;

            return OpenCodeTextParts(root.Arr("parts"));
        } catch {
            return null;
        }
    }

    static string? OpenCodeTextParts(JsonElement? parts) {
        if (parts is not { } arr) return null;

        var pieces = new List<string>();
        foreach (var part in arr.EnumerateArray()) {
            if (part.Str("type") != "text") continue;
            if (part.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("ignored",   out var ign) && ign.ValueKind == JsonValueKind.True) continue;
            if (part.Str("text")?.Trim() is { Length: > 0 } t) pieces.Add(t);
        }

        return pieces.Count > 0 ? string.Join("\n", pieces) : null;
    }

    // ── Antigravity extractors ────────────────────────────────────────
    //
    // Antigravity writes brain/<id>/.system_generated/logs/transcript_full.jsonl as
    // {step_index, source, type, status, content, thinking, tool_calls, …} lines.
    // USER_INPUT (source USER_EXPLICIT) carries the prompt wrapped in
    // <USER_REQUEST>…</USER_REQUEST> plus trailing <ADDITIONAL_METADATA>/
    // <USER_SETTINGS_CHANGE> blocks; PLANNER_RESPONSE (source MODEL) carries the
    // assistant's visible `content`. Mirrors the server's
    // AntigravityTranscriptNormalizer so watcher titles match ingest.
    static string? TryExtractAntigravityText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            var want = role == "user" ? "USER_INPUT" : "PLANNER_RESPONSE";
            if (root.Str("type") != want) return null;

            if (root.Str("content")?.Trim() is not { Length: > 0 } content) return null;

            return role == "user" ? StripAntigravityUserWrapper(content) : content;
        } catch {
            return null;
        }
    }

    // Pull the human prompt out of the <USER_REQUEST> envelope, dropping the trailing
    // metadata blocks Antigravity appends. Falls back to the raw text when no envelope
    // is present.
    static string? StripAntigravityUserWrapper(string content) {
        const string open = "<USER_REQUEST>", close = "</USER_REQUEST>";
        var start = content.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return content;

        start += open.Length;
        var end   = content.IndexOf(close, start, StringComparison.Ordinal);
        var inner = (end < 0 ? content[start..] : content[start..end]).Trim();

        return inner.Length > 0 ? inner : null;
    }

    // ── Kiro extractors ───────────────────────────────────────────
    //
    // Kiro CLI writes ~/.kiro/sessions/cli/{id}.jsonl as {"version","kind","data"}
    // lines: kind "Prompt" (user) / "AssistantMessage" / "ToolResults", whose
    // data.content[] holds {"kind":"text"|"toolUse"|"toolResult","data":…} blocks.
    // For titles we want the first user prompt and the first assistant text.

    static string? TryExtractKiroUserText(string line) => KiroLineText(line, "Prompt");

    static string? TryExtractKiroAssistantText(string line) => KiroLineText(line, "AssistantMessage");

    static string? KiroLineText(string line, string kind) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("kind") != kind) return null;
            if (root.Obj("data")?.Arr("content") is not { } content) return null;

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("kind") == "text" && block.Str("data")?.Trim() is { Length: > 0 } text)
                    return text;
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    // ── Copilot extractors ────────────────────────────────────────
    //
    // Copilot's events.jsonl envelope: {type, data, id, timestamp, parentId,
    // agentId?}. Subagent-tagged events (agentId set) are skipped — a
    // subagent's user.message is the parent's task prompt, not something the
    // human typed, and using it for the title would mislabel the session.

    static string? TryExtractCopilotUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user.message") return null;
            if (root.Str("agentId") is not null) return null;

            // data.content is the clean prompt; transformedContent wraps it in
            // injected <current_datetime>/<system_reminder> noise.
            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    static string? TryExtractCopilotAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant.message") return null;
            if (root.Str("agentId") is not null) return null;

            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    // ── Pi extractors ─────────────────────────────────────────────
    //
    // Pi emits type:"message" with message.role user/assistant — NOT Claude's
    // top-level type:"user"/"assistant" — so the watcher title path needs its
    // own branch (mirrors the server PiTranscriptNormalizer mapping). Content is
    // a string or an array of {type:"text",text} blocks.

    static string? TryExtractPiUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "user") return null;

            if (msg.Str("content")?.Trim() is { Length: > 0 } strContent) return strContent;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractPiAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "assistant") return null;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractClaudeUserText(string line, OnceLog? parseErrors) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user") {
                return null;
            }

            // Skip system-injected meta messages (e.g. <local-command-caveat>)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True) {
                return null;
            }

            var msg = root.Obj("message");

            // message.content can be a string or an array
            if (msg?.Str("content") is { } strContent) {
                return strContent.StartsWith("<local-command-stdout>", StringComparison.Ordinal) ? null : StripSystemInstructions(strContent);
            }

            if (msg?.Arr("content") is { } arrContent) {
                foreach (var element in arrContent.EnumerateArray()) {
                    if (element.Str("type") == "text" && element.Str("text") is { } text
                     && !text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)) {
                        return StripSystemInstructions(text);
                    }
                }
            }
        } catch (Exception ex) {
            parseErrors?.Write($"TryExtractUserText parse error (further errors suppressed): {ex.Message}");
        }

        return null;
    }

    static string? TryExtractCodexUserText(string line, OnceLog? parseErrors) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "user") return null;

            // Skip codex-injected wrappers (environment_context, AGENTS.md, turn_aborted). These
            // role:"user" payloads precede the real prompt and would otherwise title the session.
            var text = CodexLineText.ExtractCodexBlockText(payload.Value, "input_text");

            return text is null || CodexLineText.IsCodexInjectedUserPrelude(text) ? null : text;
        } catch (Exception ex) {
            parseErrors?.Write($"TryExtractUserText (codex) parse error (further errors suppressed): {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Strips XML-like system instruction blocks from user text so they don't pollute title generation.
    /// Removes content between tags like &lt;system_instructions&gt;, &lt;system-reminder&gt;, etc.
    /// </summary>
    public static string? StripSystemInstructions(string? text) {
        if (text is null) {
            return null;
        }

        var stripped = SystemInstructionsRegex.Replace(text, "").Trim();

        return stripped.Length > 0 ? stripped : null;
    }

    [GeneratedRegex(@"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemInstructionsRx();

    static readonly Regex SystemInstructionsRegex = SystemInstructionsRx();

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Compiled)]
    private static partial Regex CommandNameRx();
}
