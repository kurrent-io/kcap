using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Decides who a user-channel message came from: a person, a trigger from outside the session, or the
/// harness carrying on. What the harness recorded decides first; the text decides for every other
/// harness and for messages recorded before the origin was stored.
/// A rule written for one vendor applies to that vendor's messages, and to a message whose vendor the
/// caller does not know. Claude Code's shapes, generic reminders and kcap's own markers apply to every vendor.
public static class UserMessageClassifier {
    static readonly UserMessageClass Person = new(UserMessageKind.Person, null);

    /// Bare text with no recorded origin and no vendor, for a caller holding only what a card stored.
    public static UserMessageClass Classify(string? text) => Classify(text ?? "", origin: null);

    /// <param name="message">The message; its `claude_code` extension is read for the origin.</param>
    /// <param name="vendor">The harness that recorded it, when the caller knows.</param>
    /// <param name="scheduled">Prompts the session scheduled for itself so far, when the caller tracks them.</param>
    public static UserMessageClass Classify(UserMessageReceived message, string? vendor = null, ScheduledPrompts? scheduled = null) =>
        Classify(message.Content ?? "", UserMessageOrigin.Of(message), vendor, scheduled);

    /// <inheritdoc cref="Classify(string, UserMessageOrigin?, string?, ScheduledPrompts?, out bool)"/>
    public static UserMessageClass Classify(string text, UserMessageOrigin? origin, string? vendor = null, ScheduledPrompts? scheduled = null) =>
        Classify(text, origin, vendor, scheduled, out _);

    /// <param name="text">The message's text.</param>
    /// <param name="origin">What the harness recorded about who wrote it.</param>
    /// <param name="vendor">The harness that recorded it, when the caller knows.</param>
    /// <param name="scheduled">Prompts the session scheduled for itself so far, when the caller tracks them.</param>
    /// <param name="byText">True when the decision fell to the checks from <paramref name="scheduled"/> onward and
    /// the schedule could still change it, so a class stored without the session's schedule has to apply those
    /// checks again on read. A line recorded as the person's is never a firing, so it is final as stored.</param>
    public static UserMessageClass Classify(string text, UserMessageOrigin? origin, string? vendor, ScheduledPrompts? scheduled, out bool byText) {
        byText = false;

        // Kiro Crew wraps every prompt in its own context; the request inside decides.
        if (KiroCrewPromptEnvelope.IsCutOff(text)) return new(UserMessageKind.Person, CrewRequestCutOff);
        // So does Cursor, in a session recorded before its envelope was unwrapped at ingest. What the harness
        // recorded about who wrote the message still holds for the ask inside it.
        if (UserPromptDisplay.AskText(text) is var ask && !ReferenceEquals(ask, text)) {
            if (KiroCrewPromptEnvelope.IsCutOff(ask)) return new(UserMessageKind.Person, CrewRequestCutOff);

            text = ask;
        }

        // Reminders ride on asks, triggers and payloads alike; what is left decides.
        var clean = SystemInstructionParser.Parse(text).CleanText.Trim();

        // A person may type words a harness also sends, so where Claude's origin or Codex's content kinds
        // record who wrote a line, text alone decides only for what no person types. Kinds that include
        // Codex's own context vouch for nothing: the text says whether the person's words follow it.
        var human = origin?.Kind == "human" || (Applies(vendor, Codex) && origin?.ContentItemKinds is { Count: > 0 } kinds && !CodexPrelude.HasHarnessPart(kinds));

        // The lead is the fallback for a summary recorded without its flag; a person quoting it keeps their words.
        if (origin?.IsCompactSummary == true || (!human && clean.StartsWith(CompactionPrefix, StringComparison.Ordinal)))
            return Continue(Compacted);

        if (origin?.LocalCommand is { } receipt) return Continue(receipt);

        switch (origin?.Kind) {
            case "task-notification":
                return Continue(HarnessLabel(clean) ?? "Background task finished");

            case "peer" when origin.IsHandback:
                return Continue(HarnessLabel(clean) ?? "Subagent reported back");

            case "peer":
                return new(UserMessageKind.Trigger, PeerSession);

            case "coordinator" or "auto-continuation":
                return Continue(null);
        }

        if (!human && IsTrigger(clean, out var trigger)) return new(UserMessageKind.Trigger, trigger);

        byText = !human;

        // A firing carries no origin of its own, so a line recorded as the person's is theirs whatever it repeats.
        if (!human && scheduled?.IsLoopFiring(clean) == true) return new(UserMessageKind.Trigger, ScheduledLoop);
        if (!human && scheduled?.IsWakeUp(clean) == true) return Continue(ScheduledWakeUp);

        // A slash command and a shell line are the person's, whatever the harness flagged them.
        if (LocalCommandParser.IsPure(clean, out var local) && local.Kind == LocalCommandParser.LocalCommandKind.Command)
            return Command(LocalCommandParser.DisplayLabel(local));
        if (BashCommandParser.IsPure(clean, out var bash) && bash.Kind == BashCommandParser.BashKind.Input)
            return Command(BashCommandParser.DisplayLabel(bash));
        if (Applies(vendor, Codex) && CodexShellCommand.IsMatch(clean))
            return Command(CodexShellCommand.Command(clean) is { } shell ? "$ " + shell : "Shell command");
        if (Applies(vendor, Codex) && WholeFrame.Is(clean, "<user_action>", "</user_action>"))
            return Command("Review results");

        if (origin?.IsMeta == true) return Continue(HarnessLabel(clean));

        if (HarnessLabel(clean) is { } harness) return Continue(harness);

        // Text that is only reminders has nothing of the person in it; an image with no text is an ask.
        if (clean.Length == 0) return text.Length == 0 ? Person : Continue(null);

        // Ahead of the prelude: an aborted-turn block is both, and it is the interrupt a reader needs.
        foreach (var (prefix, label, typeable, owner) in ContinuationPrefixes)
            if (Applies(vendor, owner) && clean.StartsWith(prefix, StringComparison.Ordinal) && !(human && typeable)) return Continue(label);

        if (!human && Applies(vendor, Codex) && CodexPrelude.IsMatch(clean)) return Continue(SessionContext);

        return Person;
    }

    /// The label of a marker saying the person stopped the agent mid-turn.
    public const string Interrupted = UserPromptDisplay.Interrupted;

    /// The label of a Kiro Crew ask whose text Kiro cut off before recording it.
    public const string CrewRequestCutOff = "Kiro Crew request (cut off)";

    public const string Compacted = "Conversation compacted";

    public const string SessionContext = "Session context";

    public const string PeerSession = "Message from another session";

    public const string CiEvent = "CI event";

    public const string ScheduledLoop = "Scheduled loop";

    public const string ScheduledWakeUp = "Scheduled wake-up";

    const string CompactionPrefix = "This session is being continued from a previous conversation";

    const string Codex  = "codex";
    const string Cursor = "cursor";
    const string Gemini = "gemini";
    const string Kiro   = "kiro";

    /// Text a harness sends on the user channel, its label, whether a person might type the same words,
    /// and the vendor that writes it (null for every vendor).
    static readonly (string Prefix, string? Label, bool Typeable, string? Vendor)[] ContinuationPrefixes = [
        ("[Image: source:", null, false, null),
        ("[Image: original ", null, false, null),
        ("Stop hook feedback:", "Stop hook", false, null),
        ("A session-scoped Stop hook is now active", "Goal set", false, null),
        ("Continue from where you left off.", null, true, null),
        ("The app was quit while you were working", "Resumed after the app quit", false, null),
        ("<!-- kcap-memory-index", SessionContext, false, null),
        ("[handback-send-enforce]", null, false, null),
        ("<turn_aborted>", Interrupted, false, Codex),
        ("The user interrupted the previous turn on purpose", Interrupted, false, Codex),
        ("<external_codex_apps_open_page>", null, false, Codex),
        ("<hook_context>", SessionContext, false, Gemini),
        ("[System: ", null, false, Gemini),
        ("<available_subagent_types>", null, false, Cursor),
        ("Briefly inform the user about the task result", "Background task finished", false, Cursor),
        ("## Objective (iteration ", "Goal iteration", false, Kiro),
        ("[Subagent completion event]\nAgent `", "Subagent reported back", false, Kiro),
        ("[SYSTEM] Sub-agent synthesis", "Synthesizing sub-agent results", false, Kiro),
    ];

    static bool Applies(string? vendor, string? owner) =>
        owner is null || vendor is null || string.Equals(vendor, owner, StringComparison.OrdinalIgnoreCase);

    static bool IsTrigger(string clean, out string label) {
        label = "";

        if (WholeFrame.Is(clean, "<ci-monitor-event", "</ci-monitor-event>")) {
            label = CiEvent;
            return true;
        }

        // Another session talking: the bare frame, or the frame behind the harness's own preamble line, which
        // ends at its colon; words of the person's own after the colon are not the preamble.
        if (WholeFrame.Is(AfterPeerPreamble(clean), PeerFrame, "</cross-session-message>")) {
            label = PeerSession;
            return true;
        }

        return false;
    }

    const string PeerPreamble = "Another Claude session sent a message";
    const string PeerFrame = "<cross-session-message";

    static string AfterPeerPreamble(string clean) {
        if (!clean.StartsWith(PeerPreamble, StringComparison.Ordinal)) return clean;

        var colon = clean.IndexOf(':');
        var line = clean.IndexOf('\n');

        return colon < 0 || (line >= 0 && colon > line) ? clean : clean[(colon + 1)..];
    }

    static string? HarnessLabel(string text) => UserPromptDisplay.IsHarnessPayload(text, out var label) ? label : null;

    static UserMessageClass Continue(string? label) => new(UserMessageKind.Continuation, label, IsInterrupt: label == Interrupted);

    static UserMessageClass Command(string label) => new(UserMessageKind.Person, label, IsCommand: true);
}
