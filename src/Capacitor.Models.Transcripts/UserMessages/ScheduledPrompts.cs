using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Prompts the session arranged to receive later. A wake-up's prompt and a `/loop` command's prompt come
/// back as plain user messages with no marker of their own, so the only way to recognise one is to have
/// seen it scheduled. A typed `/compact` comes back too, as command tags after the compaction summary.
/// Fed every event of one session in stream order.
public sealed class ScheduledPrompts {
    readonly HashSet<string> _wakeUps = [];
    readonly HashSet<string> _loops = [];
    string? _typedCompact;

    public void Observe(object? payload) {
        if (payload is UserMessageReceived person && IsPersons(person)) ObserveCompact(person.Content);

        switch (payload) {
            case AssistantToolCallsGenerated calls when !IsSidechain(calls):
                foreach (var call in calls.ToolCalls)
                    if (IsWakeUpTool(call.ToolName) && Key(SchemaExtensions.Text(call.Arguments, "prompt")) is { Length: > 0 } wake)
                        _wakeUps.Add(wake);
                break;

            case UserMessageReceived message when IsPersons(message) && LoopArgs(message.Content) is { } loopArgs && Key(LoopPrompt(loopArgs)) is { Length: > 0 } loop:
                _loops.Add(loop);
                break;
        }
    }

    public bool IsEmpty => _wakeUps.Count == 0 && _loops.Count == 0 && _typedCompact is null;

    /// True for the command-tag copy of a `/compact` the person typed earlier in the stream. A tagged
    /// `/compact` with no typed prompt ahead of it is the only record of that command.
    public bool IsCompactEcho(string? text) =>
        _typedCompact is { } typed && LocalCommandParser.IsPure(text?.Trim(), out var command)
        && command is { Kind: LocalCommandParser.LocalCommandKind.Command, CommandName: "/compact" }
        && LocalCommandParser.DisplayLabel(command) == typed;

    void ObserveCompact(string? text) {
        var clean = SystemInstructionParser.Parse(text ?? "").CleanText.Trim();
        if (LocalCommandParser.IsPure(clean, out var command)) {
            if (command is { Kind: LocalCommandParser.LocalCommandKind.Command, CommandName: "/compact" }) _typedCompact = null;
            return;
        }
        if (clean == "/compact") _typedCompact = clean;
        else if (clean.StartsWith("/compact ", StringComparison.Ordinal)) _typedCompact = "/compact " + clean[9..].Trim();
    }

    public bool IsWakeUp(string? text) => Key(text) is { Length: > 0 } key && _wakeUps.Contains(key);

    public bool IsLoopFiring(string? text) => Key(text) is { Length: > 0 } key && _loops.Contains(key);

    // Only the person starts a loop: another session, a subagent or the harness quoting `/loop` does not.
    static bool IsPersons(UserMessageReceived message) =>
        SchemaExtensions.Text(SchemaExtensions.Slug(message, ClaudeCodeExtension.Slug), ClaudeCodeExtension.OriginKind) is null or "human"
     && !IsSidechain(message);

    // A subagent's schedule is its own; its wake-ups never fire in the session that started it.
    static bool IsSidechain(object payload) =>
        SchemaExtensions.Flag(SchemaExtensions.Slug(payload, ClaudeCodeExtension.Slug), ClaudeCodeExtension.IsSidechain);

    // Claude Code's own tool, or kcap's MCP tool under whatever server prefix the harness records.
    static bool IsWakeUpTool(string toolName) =>
        toolName is "ScheduleWakeup" or KcapWakeUpTool || toolName.EndsWith("__" + KcapWakeUpTool, StringComparison.Ordinal);

    const string KcapWakeUpTool = "schedule_wakeup";

    // The command tags when Claude Code records the slash command, the bare text when a harness does not.
    static string? LoopArgs(string? text) {
        if (LocalCommandParser.Parse(text) is { Kind: LocalCommandParser.LocalCommandKind.Command, CommandName: "/loop" } command)
            return command.CommandArgs;

        var trimmed = text?.TrimStart();

        return trimmed?.StartsWith("/loop ", StringComparison.Ordinal) == true ? trimmed[6..] : null;
    }

    // "/loop 10m check the PR" fires "check the PR"; the interval is optional.
    static string LoopPrompt(string args) {
        var trimmed = args.Trim();
        var space = trimmed.IndexOf(' ');

        return space > 0 && char.IsDigit(trimmed[0]) ? trimmed[(space + 1)..] : trimmed;
    }

    static string? Key(string? text) => text?.Trim();
}
