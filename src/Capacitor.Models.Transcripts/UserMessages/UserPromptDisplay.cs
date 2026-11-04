using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Cursor;
using Capacitor.Models.Transcripts.Harness.Kiro;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Whether a user-channel message is wholly a harness payload, what to show in its place, and the ask
/// inside a vendor's envelope. A task notification, agent message or skill load is recognised by how it
/// opens; a local command or shell payload only when nothing of the message survives stripping its tags.
public static class UserPromptDisplay {
    /// True when the text is wholly a harness payload, with the compact label to show instead. False
    /// leaves the label empty: a message that merely quotes one of these tags has words of its own.
    public static bool IsHarnessPayload(string? text, out string label) {
        label = "";

        if (string.IsNullOrWhiteSpace(text)) return false;

        if (InterruptMarkers.Contains(text.Trim(), StringComparer.Ordinal)) {
            label = Interrupted;
            return true;
        }

        // The agent-message frame and its closing guidance are both the harness, so the opening decides.
        if ((IsWhollyTaskNotification(text) || AgentMessageParser.IsMatch(text)) && TaskNotificationParser.TryParse(text, out var notification)) {
            label = TaskNotificationParser.DisplayLabel(notification);
            return true;
        }

        if (SkillLoadParser.IsMatch(text)) {
            label = SkillLoadParser.DisplayLabel(text);
            return true;
        }

        // Purity is judged on the stripped text, or a payload carrying a system block reads as an ask.
        var clean = SystemInstructionParser.Parse(text).CleanText;

        if (LocalCommandParser.IsPure(clean, out var local)) {
            label = LocalCommandParser.DisplayLabel(local);
            return true;
        }

        if (BashCommandParser.IsPure(clean, out var bash)) {
            label = BashCommandParser.DisplayLabel(bash);
            return true;
        }

        return false;
    }

    /// The compact text to show in place of a message, or null to show the message itself. A trigger
    /// keeps its own words: what the CI run or the other session said is the row's content.
    public static string? DisplayLabel(string? text, string? vendor = null) {
        if (IsHarnessPayload(text, out var label)) return label;

        // A prompt recorded inside a vendor's envelope is labelled by what is inside it.
        if (text is not null && AskText(text) is var ask && !ReferenceEquals(ask, text)) {
            if (IsHarnessPayload(ask, out var inner)) return inner;

            text = ask;
        }

        return UserMessageClassifier.Classify(text ?? "", origin: null, vendor) is { Kind: not UserMessageKind.Trigger, Label: { Length: > 0 } classified }
            ? classified
            : null;
    }

    /// The message without the envelope a vendor recorded around it: the request inside a Kiro Crew
    /// prompt, the query inside a Cursor one. Any other message comes back as the same instance.
    public static string AskText(string text) => AskRange(text) is { } ask ? text.Substring(ask.Start, ask.Length) : text;

    /// A recorded ask sits inside at most a Crew envelope around a Cursor one; a message built of nested
    /// envelopes costs at most this many passes.
    const int MaxEnvelopes = 4;

    /// Where <see cref="AskText(string)"/> finds the ask: each envelope unwraps to a contiguous run of what
    /// it wraps, so the ask is one range of the recorded text. Null when nothing was unwrapped.
    public static (int Start, int Length)? AskRange(string text) {
        var offset = 0;
        var length = text.Length;

        for (var unwrapped = 0; unwrapped < MaxEnvelopes; unwrapped++) {
            var current = text.AsSpan(offset, length);

            if (KiroCrewPromptEnvelope.RequestStart(current) is { } request) {
                offset += request;
                length -= request;
                continue;
            }

            if (!CursorUserQuery.TryFindQuery(current, out var start, out var inner, out _)) break;

            offset += start;
            length = inner;
        }

        return length == text.Length ? null : (offset, length);
    }

    /// The shell payload a message is wholly made of, text intact, for a caller that renders the output.
    /// Purity is judged on what is left beside the payload, never inside it: stdout is arbitrary text,
    /// and reminder-shaped markup a command printed must not be deleted as the harness's.
    public static bool TryShellPayload(string? text, out BashCommandParser.Result result) {
        result = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var bash = BashCommandParser.Parse(text);
        if (bash.Kind == BashCommandParser.BashKind.None) return false;
        if (!string.IsNullOrWhiteSpace(SystemInstructionParser.Parse(bash.RemainingText).CleanText)) return false;

        result = bash;
        return true;
    }

    // Claude Code writes these after Esc or a declined prompt.
    static readonly string[] InterruptMarkers = [
        "[Request interrupted by user]",
        "[Request interrupted by user for tool use]",
    ];

    /// The label of a marker saying the person stopped the agent mid-turn.
    public const string Interrupted = "Interrupted";

    const string NotificationTag = "<task-notification";
    const string NotificationEnd = "</task-notification>";

    /// Stricter than <see cref="TaskNotificationParser.IsMatch"/>, which decides whether to render a card:
    /// this decides whether to discard what someone wrote, so a longer tag that merely starts the same
    /// way, or a person quoting a notification before asking about it, is not one.
    static bool IsWhollyTaskNotification(string text) {
        var span = text.AsSpan().TrimStart();

        if (!span.StartsWith(NotificationTag)) return false;

        var after = span[NotificationTag.Length..];
        if (after.Length > 0 && after[0] is not ('>' or '/' or ' ' or '\t' or '\r' or '\n')) return false;

        // A truncated payload never closed, so nothing follows it either way.
        var close = span.LastIndexOf(NotificationEnd);

        return close < 0 || span[(close + NotificationEnd.Length)..].IsWhiteSpace();
    }
}
