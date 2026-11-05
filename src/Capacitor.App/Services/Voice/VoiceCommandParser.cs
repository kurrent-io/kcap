using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// Reads a spoken command. Control phrases must be the whole utterance, so a prompt that merely
/// mentions one ("stop the flaky test from retrying") still goes to the agent; anything that is
/// not a control phrase is a prompt.
public static partial class VoiceCommandParser {
    static readonly TimeSpan MatchBudget = TimeSpan.FromMilliseconds(200);
    const string ReadReplyPattern =
        "(read|repeat|say)( me)?( the)?( last| latest)? (reply|response|answer|message)( again)?|what did (it|they|the agent) say";

    public static VoiceCommand Parse(string utterance) {
        var original = utterance.Trim();
        var text = Normalize(original);

        if (Matches(text, ReadReplyPattern)) return new VoiceCommand.ReadReply();
        if (Matches(text, "repeat( that| it| yourself)?|say (that|it) again|come again|pardon|what did you (just )?say|again"))
            return new VoiceCommand.Repeat();
        if (Matches(text, "wait( a (minute|moment|second|sec)| for me)?|(one|just a) (moment|minute|second|sec)|give me a (minute|moment|second|sec)|let me think"))
            return new VoiceCommand.Wait();
        if (Matches(text, "(the )?previous (part|section|bit)|(go )?back a (part|section|bit)|(read )?(the )?(part|section) before"))
            return new VoiceCommand.PreviousPart();
        if (Matches(text, "(read )?more|tell me more|continue|go on|keep going|carry on|next part|(read )?(the )?next (part|section|bit)"))
            return new VoiceCommand.ReadMore(original);
        if (Matches(text, "read (it )?all|read (it )?all of it|read everything|read (the )?(whole|full|entire) (thing|reply|response|answer)( out)?"))
            return new VoiceCommand.ReadAll();
        if (ReplyPart().Match(text) is { Success: true } part) return new VoiceCommand.ReadPart(part.Groups["q"].Value.Trim());
        if (ExplicitPrompt().Match(original) is { Success: true } prompt) return new VoiceCommand.Prompt(Tail(prompt), Explicit: true);
        if (DictatePhrase().Match(original) is { Success: true } dictate) return new VoiceCommand.Dictate(Tail(dictate));
        if (NewSessionPhrase().Match(original) is { Success: true } start) {
            var goal = Tail(start);
            var (rest, level) = SplitPermissions(goal);
            return new VoiceCommand.NewSession(rest.Length == 0 ? null : rest, level);
        }

        if (text is "yes" or "yeah" or "yep" or "yup" or "sure" or "confirm" or "confirmed" or "do it" or "go ahead" or "yes do it"
            or "correct" or "yes go ahead" or "yeah go ahead" or "go for it" or "sounds good" or "do that" or "yes do that"
            or "absolutely" or "of course" or "affirmative" or "thats right")
            return new VoiceCommand.Confirm();
        if (text is "no" or "nope" or "nah" or "cancel" or "cancel that" or "never mind" or "nevermind" or "forget it" or "dont"
            or "no dont" or "dont do it" or "no thanks" or "not now" or "no not yet" or "not yet")
            return new VoiceCommand.Cancel();

        if (Matches(text, "(always allow|allow always|allow (it )?always|always approve|approve always)( it| that| this)?"))
            return new VoiceCommand.PermissionReply(PermissionAnswer.AllowAlways);
        if (Matches(text, "(allow|approve|accept|grant)( it| that| this)?( once)?|yes allow( it)?|permit( it)?"))
            return new VoiceCommand.PermissionReply(PermissionAnswer.Allow);
        if (Matches(text, "(deny|reject|decline|refuse|dont allow|do not allow|block)( it| that| this)?"))
            return new VoiceCommand.PermissionReply(PermissionAnswer.Deny);

        if (Matches(text, "(stop|end|kill|terminate) (the |this )?(agent|session)")) return new VoiceCommand.StopAgent();
        if (Matches(text, "interrupt( it| the agent| that)?|(stop|pause) working|hold on|escape|press escape"))
            return new VoiceCommand.Interrupt();
        if (Matches(text, "stop talking|stop speaking|be quiet|quiet|shush|silence|shut up|stop|enough|thats enough"))
            return new VoiceCommand.StopSpeaking();
        if (Matches(text, "stop listening|go to sleep|sleep|mute( yourself)?|voice off|turn off voice( control)?"))
            return new VoiceCommand.StopListening();

        if (Matches(text, "send( it| that| the message| the draft| the prompt)?|submit( it)?")) return new VoiceCommand.SendDraft();

        if (Matches(text, "(next|following) (session|agent|one)|go to (the )?next (session|one)")) return new VoiceCommand.NextSession();
        if (Matches(text, "(previous|last|prior) (session|agent|one)|go (to (the )?previous|back) (session|one)"))
            return new VoiceCommand.PreviousSession();
        if (Matches(text, "(list|show|read)( me)?( the| all| my)? sessions|(what|which) sessions( are)?( there| running| open)?|what (agents|sessions) are running"))
            return new VoiceCommand.ListSessions();
        if (SessionTarget().Match(text) is { Success: true } open) return new VoiceCommand.OpenSession(open.Groups["q"].Value.Trim());

        if (Matches(text, "(go )?home|go back|(close|leave|exit) (the |this )?(session|workspace)|(back to|open|show)( the)? (launcher|home)"))
            return new VoiceCommand.GoHome();

        if (Matches(text, "(show|open|switch to|go to)( the)? chat( tab)?|chat")) return new VoiceCommand.ShowTab(VoiceTab.Chat);
        if (Matches(text, "(show|open|switch to|go to)( the)? terminal( tab)?|terminal")) return new VoiceCommand.ShowTab(VoiceTab.Terminal);
        if (Matches(text, "(show|open|switch to|go to)( the)? (pull request|pr|p r)( tab)?|pull request"))
            return new VoiceCommand.ShowTab(VoiceTab.PullRequest);
        if (Matches(text, "(show|open|switch to|go to)( the)? (artifacts|artefacts|documents|plans)( tab)?"))
            return new VoiceCommand.ShowTab(VoiceTab.Artefacts);

        if (Regex.Match(text, $"^(use|set|switch to|turn on|enable|start in|go to)( the)? (?<mode>{VoicePermissionLevels.Pattern})( mode| permissions)?( for new sessions| by default| from now on)?$",
                RegexOptions.CultureInvariant, MatchBudget) is { Success: true } setting
            && VoicePermissionLevels.Parse(setting.Groups["mode"].Value) is { } chosen)
            return new VoiceCommand.SetPermissions(chosen);

        if (RepositoryTarget().Match(text) is { Success: true } repo) return new VoiceCommand.SelectRepository(repo.Groups["q"].Value.Trim());
        if (HarnessTarget().Match(text) is { Success: true } harness) return new VoiceCommand.SelectHarness(harness.Groups["q"].Value.Trim());

        if (Matches(text, "status|whats (the )?status|whats happening|what is (it|the agent) doing|whats (it|the agent) doing|how is it going|are you (there|listening)"))
            return new VoiceCommand.Status();
        if (Matches(text, "(open|show)( the)? settings|settings|preferences")) return new VoiceCommand.OpenSettings();
        if (Matches(text, "refresh|reload|refresh (the )?(session|page|work)")) return new VoiceCommand.Refresh();
        if (Matches(text, "zoom in|bigger( text)?|larger( text)?|increase (the )?text( size)?")) return new VoiceCommand.Zoom(1);
        if (Matches(text, "zoom out|smaller( text)?|decrease (the )?text( size)?")) return new VoiceCommand.Zoom(-1);
        if (Matches(text, "reset (the )?(zoom|text size)|normal (size|zoom)|actual size")) return new VoiceCommand.Zoom(0);
        if (Matches(text, "(show|open|bring up)( the)? (window|app|kcap|capacitor)|wake up")) return new VoiceCommand.ShowWindow();
        if (Matches(text, "help|what can (i|you) (say|do)|(list|show)( the)? commands|commands")) return new VoiceCommand.Help();

        return new VoiceCommand.Prompt(original);
    }

    // "with bypass permissions to fix the bug" or "fix the bug in auto mode": the level is said
    // before or after the task, and is not part of it.
    static (string Goal, VoicePermissionLevel? Level) SplitPermissions(string goal) {
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (VoicePermissionLevels.Parse(goal.TrimEnd('.', '!')) is { } alone) return ("", alone);
        var modes = VoicePermissionLevels.Pattern;
        if (Regex.Match(goal, $@"^(with|in|using|on)\s+(?<mode>{modes})(\s+(mode|permissions))?\b[\s,.:]*((to|and|that|for|about)\b)?\s*(?<rest>.*)$",
                options | RegexOptions.Singleline, MatchBudget) is { Success: true } lead
            && VoicePermissionLevels.Parse(lead.Groups["mode"].Value) is { } before)
            return (lead.Groups["rest"].Value.Trim(), before);
        if (Regex.Match(goal, $@"^(?<rest>.*?)[\s,]+(with|in|using|on)\s+(?<mode>{modes})(\s+(mode|permissions))?[.!]?$",
                options | RegexOptions.Singleline, MatchBudget) is { Success: true } trail
            && VoicePermissionLevels.Parse(trail.Groups["mode"].Value) is { } after)
            return (trail.Groups["rest"].Value.Trim(), after);
        return (goal, null);
    }

    /// Lowercase words with no punctuation and no leading or trailing politeness.
    internal static string Normalize(string text) {
        var lowered = text.ToLowerInvariant().Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal);
        var words = Spaces().Replace(NonWord().Replace(lowered, " "), " ").Trim();
        words = Politeness().Replace(words, "");
        return TrailingPlease().Replace(words, "").Trim();
    }

    static bool Matches(string text, string pattern) =>
        Regex.IsMatch(text, $"^(?:{pattern})$", RegexOptions.CultureInvariant, MatchBudget);

    static string Tail(Match match) => match.Groups["rest"].Value.Trim().TrimStart(',', ':', '-', '.').Trim();

    [GeneratedRegex(@"^\s*(please\s+)?(say|tell (it|the agent|claude|codex)( to)?|ask (it|the agent)( to)?|prompt|message)\b[\s,:]+(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExplicitPrompt();

    [GeneratedRegex(@"^\s*(please\s+)?(type|dictate|draft|write down)\b[\s,:]+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DictatePhrase();

    [GeneratedRegex(@"^\s*(please\s+)?(((start|create|launch|begin) (a |an )?(new )?|open (a |an )?new )(session|agent|chat|task)|new (session|agent|chat|task))(\s*(to|that|and|with the prompt|with prompt|for|about)\b)?(?<rest>([\s,:.].*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NewSessionPhrase();

    [GeneratedRegex(@"^(open|switch to|go to|show|select|jump to)( the)? (session|agent|chat) (called |named |about |for )?(?<q>.+)$|^(open|switch to|go to|jump to)( the)? (?<q>.+?) (session|agent|chat)$")]
    private static partial Regex SessionTarget();

    [GeneratedRegex(@"^(use|select|choose|switch to|pick)( the)? (repo|repository|project) (?<q>.+)$")]
    private static partial Regex RepositoryTarget();

    [GeneratedRegex(@"^(use|select|choose|switch to|pick)( the)? (?<q>claude( code)?|codex|codecs|copilot|cursor|gemini|grok|kiro|mistral( vibe)?|vibe|muse|open ?code|pi|antigravity)( harness| agent)?$")]
    private static partial Regex HarnessTarget();

    [GeneratedRegex(@"^(read|go to|skip to|jump to)( the)? (part|section) (?<q>.+)$|^(read|go to|skip to|jump to)( the)? (?<q>.+?) (part|section|bit)$")]
    private static partial Regex ReplyPart();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^((please|can you|could you|would you|ok|okay|hey|um|uh) )+")]
    private static partial Regex Politeness();

    [GeneratedRegex(@" please$")]
    private static partial Regex TrailingPlease();
}
