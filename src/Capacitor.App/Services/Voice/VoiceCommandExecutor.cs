namespace Capacitor.App.Services.Voice;

/// Carries out one parsed command against the app and words the answer. Runs on the UI thread.
public sealed class VoiceCommandExecutor(IVoiceHost host) {
    readonly Lock _replyLock = new();
    ReplyOutline? _reply;
    // The level a "new session with …" asked for before saying what the session should do.
    VoicePermissionLevel? _nextLevel;

    internal const string HelpText =
        "Say a prompt and I'll send it to the open session, or start a new session when none is open. " +
        "Start one with a permission level, as in: new session with bypass permissions to fix the login bug. " +
        "You can also say: new session, open session followed by its name, next or previous session, list sessions, go home, " +
        "show chat, terminal, pull request or artifacts, use repository, use Claude or Codex, allow, always allow, deny, interrupt, " +
        "stop the agent, read the reply, then read more, previous part, read a part by name, or read it all, " +
        "repeat, wait, " +
        "status, zoom in, zoom out, open settings, stop talking, or stop listening.";

    public async Task<VoiceResponse> ExecuteAsync(VoiceCommand command) {
        var response = await RunAsync(command);
        var readsReply = command is VoiceCommand.ReadReply or VoiceCommand.ReadMore or VoiceCommand.ReadPart
            or VoiceCommand.ReadAll or VoiceCommand.PreviousPart;
        return readsReply && response is { Failed: false, WatchTurn: false, Speaker: null }
            ? response with { Speaker = host.OpenSession }
            : response;
    }

    /// A reply to what an agent said last: a yes or no settles its permission request when one is
    /// waiting, anything else goes to it as a prompt. Its session is opened first.
    public async Task<VoiceResponse> AnswerAgentAsync(VoiceSession speaker, string words, bool? yes) {
        if (host.OpenSession?.AgentId != speaker.AgentId && !host.Open(speaker.AgentId))
            return VoiceResponse.Fail($"{speaker.Title} can't be opened.");
        if (yes is { } allow && host.PendingPermission is not null)
            return await RunAsync(new VoiceCommand.PermissionReply(allow ? PermissionAnswer.Allow : PermissionAnswer.Deny));
        return await PromptAsync(words);
    }

    async Task<VoiceResponse> RunAsync(VoiceCommand command) {
        if (command is not (VoiceCommand.StopSpeaking or VoiceCommand.StopListening)) host.ShowWindow();
        switch (command) {
            case VoiceCommand.Prompt { Text: var text }:
                return await PromptAsync(text);
            case VoiceCommand.Dictate { Text: var text }:
                return host.Dictate(text) ? new VoiceResponse("Typed. Say send when you're ready.") : VoiceResponse.Fail("There's nowhere to type that.");
            case VoiceCommand.SendDraft:
                return await host.SendDraftAsync() is { } why ? VoiceResponse.Fail(why) : new VoiceResponse("Sent.") { WatchTurn = true };
            case VoiceCommand.NewSession { Goal: null, Permissions: var level }:
                host.GoHome();
                _nextLevel = level;
                var ahead = level is { } chosen ? $" It will use {LevelName(chosen)}." : "";
                return new VoiceResponse($"What should the new session do? {host.LauncherSummary}{ahead}") { FollowUp = VoiceFollowUp.Prompt };
            case VoiceCommand.NewSession { Goal: { } goal, Permissions: { } level }:
                return await StartAt(goal, level);
            case VoiceCommand.NewSession { Goal: { } goal }:
                return await StartAsync(goal);
            case VoiceCommand.SetPermissions { Level: VoicePermissionLevel.Bypass }:
                return new VoiceResponse("Let every new session here run without asking permission? Say yes to confirm.") {
                    FollowUp = VoiceFollowUp.Confirm,
                    OnConfirm = () => SetPermissionsAsync(VoicePermissionLevel.Bypass),
                };
            case VoiceCommand.SetPermissions { Level: var level }:
                return await SetPermissionsAsync(level);
            case VoiceCommand.OpenSession { Query: var query }:
                return OpenMatching(query);
            case VoiceCommand.NextSession:
                return Step(1);
            case VoiceCommand.PreviousSession:
                return Step(-1);
            case VoiceCommand.ListSessions:
                return ListSessions() with { Reads = true };
            case VoiceCommand.GoHome:
                host.GoHome();
                return new VoiceResponse("Home.");
            case VoiceCommand.ShowTab { Tab: var tab }:
                return host.ShowTab(tab) ? new VoiceResponse(TabName(tab) + ".") : VoiceResponse.Fail($"There's no {TabName(tab).ToLowerInvariant()} to show here.");
            case VoiceCommand.SelectRepository { Query: var query }:
                return await host.SelectRepositoryAsync(query) is { } repo
                    ? new VoiceResponse($"Using {repo}.") : VoiceResponse.Fail($"I couldn't find a repository called {query}.");
            case VoiceCommand.SelectHarness { Query: var query }:
                return await host.SelectHarnessAsync(query) is { } harness
                    ? new VoiceResponse($"Using {harness}.") : VoiceResponse.Fail($"{query} isn't available on this machine.");
            case VoiceCommand.StopAgent:
                return host.OpenSession is { } open
                    ? new VoiceResponse($"Stop {open.Title}? Say yes to confirm.") {
                        FollowUp = VoiceFollowUp.Confirm,
                        OnConfirm = () => Task.FromResult(host.StopAgent() ? new VoiceResponse("Stopping.") : VoiceResponse.Fail("It can't be stopped now.")),
                    }
                    : VoiceResponse.Fail("No session is open.");
            case VoiceCommand.Interrupt:
                return host.Interrupt() ? new VoiceResponse("Interrupted.") : VoiceResponse.Fail("Nothing to interrupt.");
            case VoiceCommand.PermissionReply { Answer: var answer }:
                if (host.PendingPermission is null) return VoiceResponse.Fail("Nothing is waiting for permission.");
                return await host.AnswerPermissionAsync(answer)
                    ? new VoiceResponse(answer switch {
                        PermissionAnswer.Deny => "Denied.",
                        PermissionAnswer.AllowAlways => "Always allowed.",
                        _ => "Allowed.",
                    }) { WatchTurn = answer != PermissionAnswer.Deny }
                    : VoiceResponse.Fail("That answer didn't go through.");
            case VoiceCommand.ReadReply:
                return host.LastReply is { Length: > 0 } reply ? OpenReply(reply) : VoiceResponse.Fail("There's no reply to read.");
            case VoiceCommand.ReadMore { Spoken: var spoken }:
                return await ReadMoreAsync(spoken);
            case VoiceCommand.ReadPart { Query: var part }:
                return ReadPart(part);
            case VoiceCommand.PreviousPart:
                if (Outline() is not { } earlier) return VoiceResponse.Fail("There's no reply to read.");
                if (earlier.IsShort) return new VoiceResponse(earlier.Opening()) { Reads = true };
                return earlier.Back() is { } back
                    ? new VoiceResponse(back) { OpensReply = earlier.HasMore, Reads = true }
                    : VoiceResponse.Fail("There's no earlier part.");
            case VoiceCommand.ReadAll:
                return Outline() is { } all ? new VoiceResponse(all.All()) { Reads = true } : VoiceResponse.Fail("There's no reply to read.");
            case VoiceCommand.Status:
                return new VoiceResponse(host.StatusSummary);
            case VoiceCommand.OpenSettings:
                host.OpenSettings();
                return new VoiceResponse("Settings.");
            case VoiceCommand.Refresh:
                return host.Refresh() ? new VoiceResponse("Refreshing.") : VoiceResponse.Fail("Nothing to refresh.");
            case VoiceCommand.Zoom { Direction: var direction }:
                host.Zoom(direction);
                return new VoiceResponse(direction switch { > 0 => "Bigger.", < 0 => "Smaller.", _ => "Normal size." });
            case VoiceCommand.ShowWindow:
                return new VoiceResponse("Here.");
            case VoiceCommand.StopListening:
                return new VoiceResponse("Voice control off. Turn it back on in Settings.") { FollowUp = VoiceFollowUp.StopListening };
            case VoiceCommand.StopSpeaking:
                return new VoiceResponse("");
            case VoiceCommand.Help:
                return new VoiceResponse(HelpText) { Reads = true };
            case VoiceCommand.Say { Text: var text }:
                return new VoiceResponse(text);
            case VoiceCommand.Confirm or VoiceCommand.Cancel:
                return VoiceResponse.Fail("There's nothing to confirm.");
            default:
                return VoiceResponse.Fail("I didn't catch that.");
        }
    }

    /// Runs an interpreter's commands in order and answers with everything they said. A failure
    /// stops the rest: stopping the agent after opening the wrong session would stop the wrong one.
    public async Task<VoiceResponse> ExecuteAllAsync(IReadOnlyList<VoiceCommand> commands) {
        var said = new List<string>();
        VoiceResponse? last = null;
        var watch = false;
        var reads = false;
        foreach (var command in commands) {
            last = await ExecuteAsync(command);
            if (last.Speech.Length > 0) said.Add(last.Speech);
            watch |= last.WatchTurn;
            reads |= last.Reads;
            if (last.Failed) break;
        }
        return last is null ? VoiceResponse.Fail("I didn't catch that.") : last with { Speech = string.Join(" ", said), WatchTurn = watch, Reads = reads };
    }

    /// What is on screen, for an interpreter.
    public VoiceContext Context() {
        string? parts;
        lock (_replyLock) parts = _reply is { HasMore: true } reading ? reading.PartNames : null;
        return new VoiceContext(host.Sessions, host.OpenSession?.Title, host.LauncherSummary, host.PendingPermission, parts);
    }

    /// Starts reading a reply: the whole of a short one, the opening and outline of a long one. A
    /// long one leaves the reply open, so its parts can be asked for without the wake word.
    public VoiceResponse OpenReply(string markdown) {
        var outline = ReplyOutline.From(markdown);
        lock (_replyLock) _reply = outline;
        return new VoiceResponse(outline.Opening()) { OpensReply = outline.HasMore, Reads = true };
    }

    /// The part "read more" would read now, to prepare while the current one is spoken.
    public string? PeekNextPart() {
        lock (_replyLock) return _reply?.PeekNext();
    }

    ReplyOutline? Outline() {
        lock (_replyLock) {
            if (_reply is null && host.LastReply is { Length: > 0 } last) _reply = ReplyOutline.From(last);
            return _reply;
        }
    }

    async Task<VoiceResponse> ReadMoreAsync(string spoken) {
        ReplyOutline? reading;
        lock (_replyLock) reading = _reply;
        // Said to the agent, not to the reader, unless a long reply is part-way through.
        if (reading is not { HasMore: true } && VoiceCommandParser.Normalize(spoken) is "continue" or "go on" or "keep going" or "carry on")
            return await PromptAsync(spoken);
        if (Outline() is not { } outline) return VoiceResponse.Fail("There's no reply to read.");
        if (outline.IsShort) return new VoiceResponse(outline.Opening()) { Reads = true };
        return outline.Next() is { } part
            ? new VoiceResponse(part) { OpensReply = outline.HasMore, Reads = true }
            : new VoiceResponse("That's the end of the reply.");
    }

    VoiceResponse ReadPart(string query) {
        if (Outline() is not { } outline) return VoiceResponse.Fail("There's no reply to read.");
        if (outline.IsShort) return new VoiceResponse(outline.Opening()) { Reads = true };
        return outline.Read(query) is { } part
            ? new VoiceResponse(part) { OpensReply = outline.HasMore, Reads = true }
            : VoiceResponse.Fail($"I couldn't find that part. The reply has {outline.PartNames}.") with { OpensReply = true };
    }

    /// The utterance that follows a Prompt follow-up.
    public Task<VoiceResponse> StartFromFollowUpAsync(string goal) {
        var level = _nextLevel;
        _nextLevel = null;
        return level is { } chosen ? StartAt(goal, chosen) : StartAsync(goal);
    }

    // Bypassing every prompt is read back first: a misheard word must not hand an agent the machine.
    Task<VoiceResponse> StartAt(string goal, VoicePermissionLevel level) => level == VoicePermissionLevel.Bypass
        ? Task.FromResult(new VoiceResponse($"Start a session that runs without asking permission? {host.LauncherSummary} Say yes to confirm.") {
            FollowUp = VoiceFollowUp.Confirm,
            OnConfirm = () => StartAtAsync(goal, level),
        })
        : StartAtAsync(goal, level);

    async Task<VoiceResponse> StartAtAsync(string goal, VoicePermissionLevel level) {
        if (await host.StartSessionAsync(goal, level) is { } why) return VoiceResponse.Fail(why);
        return new VoiceResponse($"Starting a session with {LevelName(level)}. {host.LauncherSummary}") { WatchTurn = true };
    }

    async Task<VoiceResponse> SetPermissionsAsync(VoicePermissionLevel level) => await host.SetPermissionsAsync(level) is { } name
        ? new VoiceResponse($"New sessions will use {name.ToLowerInvariant()}.")
        : VoiceResponse.Fail("This agent has no permission setting to change.");

    static string LevelName(VoicePermissionLevel level) => level switch {
        VoicePermissionLevel.AcceptEdits => "accept edits",
        VoicePermissionLevel.Auto => "auto permissions",
        VoicePermissionLevel.Bypass => "permissions bypassed",
        _ => "manual permissions",
    };

    async Task<VoiceResponse> PromptAsync(string text) {
        if (host.OpenSession is not { } open) return await StartAsync(text);
        if (await host.SendPromptAsync(text) is { } why) return VoiceResponse.Fail(why);
        return new VoiceResponse($"Sent to {open.Title}.") { WatchTurn = true };
    }

    async Task<VoiceResponse> StartAsync(string goal) {
        if (await host.StartSessionAsync(goal) is { } why) return VoiceResponse.Fail(why);
        return new VoiceResponse($"Starting a session. {host.LauncherSummary}") { WatchTurn = true };
    }

    VoiceResponse OpenMatching(string query) {
        var sessions = host.Sessions;
        if (SessionMatcher.Best(query, sessions) is not { } match)
            return VoiceResponse.Fail(sessions.Count == 0 ? "There are no sessions." : $"I couldn't find a session matching {query}.");
        return host.Open(match.AgentId) ? new VoiceResponse($"Opened {match.Title}.") : VoiceResponse.Fail($"{match.Title} can't be opened.");
    }

    VoiceResponse Step(int direction) {
        var sessions = host.Sessions;
        if (sessions.Count == 0) return VoiceResponse.Fail("There are no sessions.");
        var current = -1;
        for (var i = 0; i < sessions.Count; i++) {
            if (sessions[i].IsOpen) current = i;
        }
        var next = current < 0
            ? (direction > 0 ? 0 : sessions.Count - 1)
            : ((current + direction) % sessions.Count + sessions.Count) % sessions.Count;
        var target = sessions[next];
        return host.Open(target.AgentId) ? new VoiceResponse($"{target.Title}.") : VoiceResponse.Fail($"{target.Title} can't be opened.");
    }

    VoiceResponse ListSessions() {
        var sessions = host.Sessions;
        if (sessions.Count == 0) return new VoiceResponse("There are no sessions.");
        const int spoken = 6;
        var names = sessions.Take(spoken).Select(s => s.Status.Length > 0 ? $"{s.Title}, {s.Status}" : s.Title);
        var more = sessions.Count > spoken ? $" And {sessions.Count - spoken} more." : "";
        return new VoiceResponse($"{sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")}: {string.Join(". ", names)}.{more}");
    }

    static string TabName(VoiceTab tab) => tab switch {
        VoiceTab.Terminal => "Terminal",
        VoiceTab.PullRequest => "Pull request",
        VoiceTab.Artefacts => "Artifacts",
        _ => "Chat",
    };
}
