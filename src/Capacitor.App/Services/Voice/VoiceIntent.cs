using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Capacitor.App.Services.Voice;

/// The instructions, answer shape and reading of an interpreter's answer, shared by every model
/// so they all choose from the same actions. The model only picks actions; the executor still
/// carries them out, so a confirmation or a missing session is handled as it is for a phrase.
public static class VoiceIntent {
    static readonly FrozenDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["prompt"] = "send words to the coding agent of the open session (or start one when none is open); value: the words, as spoken",
        ["new_session"] = "start a new agent session; value: what it should do, or empty to be asked; permissions: the level the user asked for this one session, if any",
        ["set_permissions"] = "set how much every new session may do without asking; value: manual, accept_edits, auto or bypass",
        ["open_session"] = "open the session that session names; value: empty",
        ["next_session"] = "open the next session in the list",
        ["previous_session"] = "open the previous session in the list",
        ["list_sessions"] = "read out the sessions",
        ["go_home"] = "close the open session and go back to the launcher",
        ["show_tab"] = "show a tab of the open session; value: chat, terminal, pull_request or artifacts",
        ["use_repository"] = "pick the repository new sessions start in; value: its name",
        ["use_harness"] = "pick the agent new sessions use; value: claude, codex or another agent's name",
        ["stop_agent"] = "stop the open session's agent for good (the user is asked to confirm)",
        ["interrupt"] = "interrupt what the open session's agent is doing right now",
        ["allow"] = "allow the pending permission request once",
        ["always_allow"] = "always allow the pending permission request",
        ["deny"] = "deny the pending permission request",
        ["read_reply"] = "read the agent's latest reply",
        ["read_more"] = "read the next part of the reply being read",
        ["read_part"] = "read one part of the reply; value: the part's name or number",
        ["read_all"] = "read the whole reply",
        ["previous_part"] = "read the part of the reply before the one just read",
        ["repeat"] = "say again what the app said last",
        ["wait"] = "give the user more time to answer what the app asked",
        ["dictate"] = "type words into the composer without sending; value: the words",
        ["send_draft"] = "send what the composer holds",
        ["status"] = "say what the open session is doing",
        ["open_settings"] = "open the settings window",
        ["refresh"] = "refresh the open session",
        ["zoom_in"] = "make the text bigger",
        ["zoom_out"] = "make the text smaller",
        ["zoom_reset"] = "reset the text size",
        ["stop_speaking"] = "stop talking",
        ["stop_listening"] = "turn voice control off",
        ["help"] = "list what can be said",
        ["answer"] = "say something back: an answer to a question about the sessions or the app, only from what is listed here; value: the answer, one or two short sentences",
        ["unclear"] = "the request is ambiguous; value: one short question to ask back",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string SystemPrompt { get; } = BuildSystemPrompt();

    /// The JSON schema an answer must match.
    public static string Schema { get; } = BuildSchema();

    static string BuildSystemPrompt() {
        var sb = new StringBuilder();
        sb.Append("You turn one spoken command to a desktop app for coding agents into app actions. ");
        sb.Append("The speech was transcribed and may have small errors. Reply only with JSON matching the schema: ");
        sb.Append("one action, its value, and the session it is about.\n\n");
        sb.Append("Actions:\n");
        foreach (var (name, description) in Actions) sb.Append("- ").Append(name).Append(": ").Append(description).Append('\n');
        sb.Append("""

            Rules:
            - session is the title, exactly as listed, of the session the user names ("the docs one", "the login session"); it is opened before the action runs. It is empty when they name none, and the action is about the open session.
            - Words meant for the coding agent (a task, a question about code, an answer to the agent) are a prompt. Keep the user's words; drop only words that route them, such as "tell the login session to".
            - Match sessions by meaning, not spelling: "the login one" is the session whose title is about login.
            - Close, end, kill or stop a session means stop_agent, also when it names a session: then session is that one.
            - Starting a session with another agent ("a codex session") is use_harness with that agent; the user starts it next. Leaving or going back means go_home. Turning yourself or the voice off means stop_listening.
            - A question about the sessions themselves (how many, which one is waiting) is answer, from the list. "What did it say" is read_reply.
            - The recent conversation, when given, is what the user is answering: "the other one" or "yes, the second one" refers to what was said last.
            - When the last thing said is an agent's reply or question, an answer to it ("yes", "no, use the other file", "the second option", "do both") is a prompt with session set to that agent's session. Keep the user's words as spoken.
            - When the last thing said is the app's question, answer it with the action it asked about, or unclear.
            - Never invent a session, part or repository that is not listed. When two could be meant, use unclear.
            - value is an empty string when the action takes none.
            - permissions is empty unless the user names how much a new session may do without asking: bypass (also "yolo", "full access", "no permission prompts"), auto, accept_edits or manual.

            Examples, with the sessions "Add the billing page" (open) and "Migrate the database":
            "shut down the database one" -> {"action":"stop_agent","value":"","session":"Migrate the database"}
            "close it" -> {"action":"stop_agent","value":"","session":""}
            "take me to the migration" -> {"action":"open_session","value":"","session":"Migrate the database"}
            "back to the launcher" -> {"action":"go_home","value":"","session":""}
            "how many are there" -> {"action":"answer","value":"There are two sessions.","session":""}
            "which one needs me" -> {"action":"answer","value":"Migrate the database is waiting for you.","session":""}
            "add tests for the invoice total" -> {"action":"prompt","value":"add tests for the invoice total","session":""}
            "why does the total round down" -> {"action":"prompt","value":"why does the total round down","session":""}
            "ask the database session to add an index" -> {"action":"prompt","value":"add an index","session":"Migrate the database"}
            "new session to write the changelog" -> {"action":"new_session","value":"write the changelog","session":""}
            "wait, hold on" -> {"action":"interrupt","value":"","session":""}
            "I want a codex one" -> {"action":"use_harness","value":"codex","session":""}
            "type hello world but don't send it" -> {"action":"dictate","value":"hello world","session":""}
            "a bit larger" -> {"action":"zoom_in","value":"","session":""}
            "switch yourself off" -> {"action":"stop_listening","value":"","session":""}
            "what did it answer" -> {"action":"read_reply","value":"","session":""}
            "new session in bypass mode to clean up the logs" -> {"action":"new_session","value":"clean up the logs","session":"","permissions":"bypass"}
            "let new sessions edit files without asking" -> {"action":"set_permissions","value":"accept_edits","session":"","permissions":""}
            """);
        return sb.ToString();
    }

    static string BuildSchema() {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) {
            json.WriteStartObject();
            json.WriteString("type", "object");
            json.WriteStartObject("properties");
            json.WriteStartObject("action");
            json.WriteString("type", "string");
            json.WriteStartArray("enum");
            foreach (var name in Actions.Keys) json.WriteStringValue(name);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("value");
            json.WriteString("type", "string");
            json.WriteEndObject();
            json.WriteStartObject("permissions");
            json.WriteString("type", "string");
            json.WriteStartArray("enum");
            foreach (var level in new[] { "", "manual", "accept_edits", "auto", "bypass" }) json.WriteStringValue(level);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("session");
            json.WriteString("type", "string");
            json.WriteEndObject();
            json.WriteEndObject();
            WriteRequired(json, "action", "value", "session");
            json.WriteBoolean("additionalProperties", false);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteRequired(Utf8JsonWriter json, params string[] names) {
        json.WriteStartArray("required");
        foreach (var name in names) json.WriteStringValue(name);
        json.WriteEndArray();
    }

    // An agent's reply is kept to its end, where its question sits; the prompt stays small.
    const int RecentChars = 400;

    /// The utterance with what is on screen, as the user message.
    public static string Message(string utterance, VoiceContext context) {
        var sb = new StringBuilder();
        sb.Append("Sessions, in the rail's order:\n");
        if (context.Sessions.Count == 0) sb.Append("(none)\n");
        foreach (var session in context.Sessions) {
            sb.Append("- ").Append(session.Title);
            if (session.Status.Length > 0) sb.Append(" (").Append(session.Status).Append(')');
            if (session.IsOpen) sb.Append(" [open]");
            sb.Append('\n');
        }
        sb.Append("Open session: ").Append(context.OpenSession ?? "none, the launcher is showing").Append('\n');
        sb.Append("New sessions start with: ").Append(context.Launcher).Append('\n');
        if (context.PendingPermission is { } permission) sb.Append("Waiting for permission: ").Append(permission).Append('\n');
        if (context.ReplyParts is { } parts) sb.Append("Reply being read has: ").Append(parts).Append('\n');
        if (context.Recent is { Count: > 0 } recent) {
            sb.Append("\nRecent conversation, oldest first:\n");
            foreach (var turn in recent) {
                if (turn.Said.Length > 0) sb.Append("- user: ").Append(turn.Said).Append('\n');
                if (turn.Answer.Length == 0) continue;
                sb.Append(turn.Said.Length > 0 ? "  " : "- ");
                sb.Append(turn.Speaker is { } speaker ? $"agent of \"{speaker.Title}\"" : "app").Append(": ");
                sb.Append(turn.Answer.Length > RecentChars ? "…" + turn.Answer[^RecentChars..] : turn.Answer).Append('\n');
            }
        }
        sb.Append("\nThe user said: \"").Append(utterance).Append('"');
        return sb.ToString();
    }

    /// The commands an answer names: the session it names opened first, unless that is the open
    /// one, then the action. Null when it is not an answer of the expected shape.
    public static IReadOnlyList<VoiceCommand>? Read(string answer, string utterance, VoiceContext context) {
        try {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var action = Text(root, "action");
            var value = Text(root, "value");
            var session = Listed(Text(root, "session"), context);
            if (action == "open_session") {
                var target = session.Length > 0 ? session : value;
                return target.Length > 0 ? [new VoiceCommand.OpenSession(target)] : null;
            }
            var level = VoicePermissionLevels.Parse(Text(root, "permissions").Replace('_', ' '));
            if (action == "new_session") return [new VoiceCommand.NewSession(value.Length > 0 ? value : null, level)];
            if (action == "set_permissions")
                return VoicePermissionLevels.Parse(value.Replace('_', ' ')) is { } chosen ? [new VoiceCommand.SetPermissions(chosen)] : null;
            if (Command(action, value, utterance) is not { } command) return null;
            var other = session.Length > 0 && !string.Equals(session, context.OpenSession, StringComparison.OrdinalIgnoreCase);
            return other && AboutASession(command) ? [new VoiceCommand.OpenSession(session), command] : [command];
        } catch (JsonException) {
            return null;
        }
    }

    // Models copy a session with its listed status ("Fix login (working)"); the title alone is meant.
    static string Listed(string session, VoiceContext context) =>
        context.Sessions.Select(s => s.Title).Where(t => session.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            .MaxBy(t => t.Length) ?? session;

    static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    // Commands about the app as a whole ("zoom in") ignore a session the model named anyway.
    static bool AboutASession(VoiceCommand command) => command is VoiceCommand.Prompt or VoiceCommand.StopAgent
        or VoiceCommand.Interrupt or VoiceCommand.ShowTab or VoiceCommand.ReadReply or VoiceCommand.Status
        or VoiceCommand.Refresh or VoiceCommand.Dictate or VoiceCommand.PermissionReply;

    static VoiceCommand? Command(string? action, string value, string utterance) => action switch {
        // An empty prompt keeps the words as spoken rather than sending nothing.
        "prompt" => new VoiceCommand.Prompt(value.Length > 0 ? value : utterance, Explicit: true),
        "new_session" => new VoiceCommand.NewSession(value.Length > 0 ? value : null),
        "next_session" => new VoiceCommand.NextSession(),
        "previous_session" => new VoiceCommand.PreviousSession(),
        "list_sessions" => new VoiceCommand.ListSessions(),
        "go_home" => new VoiceCommand.GoHome(),
        "show_tab" => new VoiceCommand.ShowTab(value.ToLowerInvariant() switch {
            "terminal" => VoiceTab.Terminal,
            "pull_request" or "pull request" or "pr" => VoiceTab.PullRequest,
            "artifacts" or "artefacts" => VoiceTab.Artefacts,
            _ => VoiceTab.Chat,
        }),
        "use_repository" when value.Length > 0 => new VoiceCommand.SelectRepository(value),
        "use_harness" when value.Length > 0 => new VoiceCommand.SelectHarness(value),
        "stop_agent" => new VoiceCommand.StopAgent(),
        "interrupt" => new VoiceCommand.Interrupt(),
        "allow" => new VoiceCommand.PermissionReply(PermissionAnswer.Allow),
        "always_allow" => new VoiceCommand.PermissionReply(PermissionAnswer.AllowAlways),
        "deny" => new VoiceCommand.PermissionReply(PermissionAnswer.Deny),
        "read_reply" => new VoiceCommand.ReadReply(),
        "read_more" => new VoiceCommand.ReadMore("read more"),
        "read_part" when value.Length > 0 => new VoiceCommand.ReadPart(value),
        "read_all" => new VoiceCommand.ReadAll(),
        "previous_part" => new VoiceCommand.PreviousPart(),
        "repeat" => new VoiceCommand.Repeat(),
        "wait" => new VoiceCommand.Wait(),
        "dictate" when value.Length > 0 => new VoiceCommand.Dictate(value),
        "send_draft" => new VoiceCommand.SendDraft(),
        "status" => new VoiceCommand.Status(),
        "open_settings" => new VoiceCommand.OpenSettings(),
        "refresh" => new VoiceCommand.Refresh(),
        "zoom_in" => new VoiceCommand.Zoom(1),
        "zoom_out" => new VoiceCommand.Zoom(-1),
        "zoom_reset" => new VoiceCommand.Zoom(0),
        "stop_speaking" => new VoiceCommand.StopSpeaking(),
        "stop_listening" => new VoiceCommand.StopListening(),
        "help" => new VoiceCommand.Help(),
        "answer" or "unclear" when value.Length > 0 => new VoiceCommand.Say(value),
        _ => null,
    };
}
