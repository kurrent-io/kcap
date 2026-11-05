using System.Net;
using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The `&lt;task-notification&gt;` message Claude Code injects when a background command or subagent
/// finishes. A subagent that hands back reports through the agent-message frame instead, which
/// <see cref="AgentMessageParser"/> reads into the same shape.
public static partial class TaskNotificationParser {
    public enum TaskOutcome { Success, Failure, Unknown }

    public readonly record struct TaskNotification(
        string?     TaskId,
        string?     ToolUseId,
        string?     OutputFile,
        string?     Status,
        string?     Summary,
        int?        ExitCode,
        string?     Note,
        string?     Result,
        TaskOutcome Outcome
    );

    // A status in neither this set nor "completed" is Unknown, never a false success.
    static readonly HashSet<string> FailureStatuses = new(StringComparer.OrdinalIgnoreCase) {
        "failed", "error", "cancelled", "canceled", "aborted",
        "timed_out", "timeout", "interrupted", "killed",
    };

    const int LabelMax = 200;

    /// The agent's name as the summary states it (`Agent "Check PR" finished` gives `Check PR`); null
    /// for a background command.
    public static string? AgentName(in TaskNotification n) =>
        n.Summary is { Length: > 0 } summary && AgentNamePattern().Match(summary) is { Success: true } m
     && m.Groups[1].Value.Trim() is { Length: > 0 } name ? name : null;

    /// The notice reports a shell command or a monitor, which have no stream of their own.
    public static bool IsShellTask(in TaskNotification n) =>
        n.Summary is { Length: > 0 } summary && ShellTask().IsMatch(summary);

    /// The message opens with the tag, or is the agent-message frame a subagent reports through.
    public static bool IsMatch(string? text) =>
        text is not null && (text.AsSpan().TrimStart().StartsWith("<task-notification") || AgentMessageParser.IsMatch(text));

    /// Returns exactly <see cref="IsMatch"/>; every field is null when its tag is absent.
    public static bool TryParse(string? text, out TaskNotification result) {
        if (AgentMessageParser.TryParse(text, out var message)) {
            result = FromAgentMessage(message);
            return true;
        }

        if (!IsMatch(text)) {
            result = default;
            return false;
        }

        var status = Extract(text!, Status());
        // The summary is XML-escaped, and it quotes the command a background task ran.
        var summary = Extract(text!, Summary()) is { } escaped ? WebUtility.HtmlDecode(escaped) : null;
        var exitCode = ParseExitCode(summary);

        result = new(
            TaskId:     Extract(text!, TaskId()),
            ToolUseId:  Extract(text!, ToolUseId()),
            OutputFile: Extract(text!, OutputFile()),
            Status:     status,
            Summary:    summary,
            ExitCode:   exitCode,
            Note:       Extract(text!, Note()),
            Result:     Extract(text!, ResultTag()),
            Outcome:    ClassifyOutcome(status, exitCode)
        );
        return true;
    }

    // A hand-back ends the agent's run, so it is that agent's completion, named by the id its stream
    // is keyed by. Any other agent message is mid-run and completes nothing.
    static TaskNotification FromAgentMessage(AgentMessageParser.AgentMessage message) =>
        message.IsHandback
            ? new(TaskId: message.From, ToolUseId: null, OutputFile: null, Status: "completed",
                  Summary: "Subagent report", ExitCode: null, Note: null, Result: message.Body, Outcome: TaskOutcome.Success)
            : new(TaskId: null, ToolUseId: null, OutputFile: null, Status: null,
                  Summary: "Message from another agent", ExitCode: null, Note: null, Result: message.Body, Outcome: TaskOutcome.Unknown);

    static TaskOutcome ClassifyOutcome(string? status, int? exitCode) {
        if (exitCode is not null and not 0) return TaskOutcome.Failure;

        var s = status?.Trim();
        if (s is not null && FailureStatuses.Contains(s)) return TaskOutcome.Failure;
        if (s is not null && s.Equals("completed", StringComparison.OrdinalIgnoreCase)) return TaskOutcome.Success;

        return TaskOutcome.Unknown;
    }

    /// The outcome glyph and the summary as written (it already carries any exit code), bounded at 200 characters.
    public static string DisplayLabel(in TaskNotification n) {
        var glyph = n.Outcome switch {
            TaskOutcome.Success => "✓ ",
            TaskOutcome.Failure => "✗ ",
            _                   => "",
        };
        var body = !string.IsNullOrWhiteSpace(n.Summary)
            ? n.Summary!.Trim()
            : n.Outcome == TaskOutcome.Failure ? "Background task failed" : "Background task";
        var label = glyph + body;

        return label.Length <= LabelMax ? label : label[..LabelMax] + "…";
    }

    static string? Extract(string text, Regex rx) => rx.Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    static int? ParseExitCode(string? summary) =>
        summary is not null && ExitCode().Match(summary) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var code) ? code : null;

    // A stored payload can end before a closing tag; the lazy match still stops at a real one.
    [GeneratedRegex("<task-id>(.*?)(?:</task-id>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskId();

    [GeneratedRegex("<tool-use-id>(.*?)(?:</tool-use-id>|$)", RegexOptions.Singleline)]
    private static partial Regex ToolUseId();

    [GeneratedRegex("<output-file>(.*?)(?:</output-file>|$)", RegexOptions.Singleline)]
    private static partial Regex OutputFile();

    [GeneratedRegex("<status>(.*?)(?:</status>|$)", RegexOptions.Singleline)]
    private static partial Regex Status();

    [GeneratedRegex("<summary>(.*?)(?:</summary>|$)", RegexOptions.Singleline)]
    private static partial Regex Summary();

    [GeneratedRegex("<note>(.*?)(?:</note>|$)", RegexOptions.Singleline)]
    private static partial Regex Note();

    [GeneratedRegex("<result>(.*?)(?:</result>|$)", RegexOptions.Singleline)]
    private static partial Regex ResultTag();

    [GeneratedRegex(@"exit code (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitCode();

    [GeneratedRegex("""^\s*Agent\s+"([^"]+)"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex AgentNamePattern();

    [GeneratedRegex(@"^\s*(?:Background command|Monitor)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShellTask();
}
