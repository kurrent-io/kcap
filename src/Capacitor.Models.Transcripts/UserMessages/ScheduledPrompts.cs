using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Prompts the session arranged to receive later. A wake-up's prompt and a `/loop` command's prompt come
/// back as plain user messages with no marker of their own, so the only way to recognise one is to have
/// seen it scheduled. Fed every event of one session in stream order.
public sealed class ScheduledPrompts {
    readonly HashSet<string> _wakeUps = [];
    readonly HashSet<string> _loops = [];

    public void Observe(object? payload) {
        switch (payload) {
            case AssistantToolCallsGenerated calls:
                foreach (var call in calls.ToolCalls)
                    if (IsWakeUpTool(call.ToolName) && Key(SchemaExtensions.Text(call.Arguments, "prompt")) is { Length: > 0 } wake)
                        _wakeUps.Add(wake);
                break;

            case UserMessageReceived message when LoopArgs(message.Content) is { } loopArgs && Key(LoopPrompt(loopArgs)) is { Length: > 0 } loop:
                _loops.Add(loop);
                break;
        }
    }

    public bool IsEmpty => _wakeUps.Count == 0 && _loops.Count == 0;

    public bool IsWakeUp(string? text) => Key(text) is { Length: > 0 } key && _wakeUps.Contains(key);

    public bool IsLoopFiring(string? text) => Key(text) is { Length: > 0 } key && _loops.Contains(key);

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
