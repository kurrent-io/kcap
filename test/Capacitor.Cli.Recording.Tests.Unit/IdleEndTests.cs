namespace Capacitor.Cli.Recording.Tests.Unit;

public class IdleEndTests {
    static readonly DateTimeOffset IdleNow    = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan       IdleWindow = TimeSpan.FromMinutes(60);

    [Test]
    [Arguments(null, 60)]      // unset → default 60 min
    [Arguments("", 60)]        // empty → default
    [Arguments("abc", 60)]     // non-numeric → default
    [Arguments("0", 60)]       // non-positive → default (clamped)
    [Arguments("-5", 60)]      // negative → default
    [Arguments("15", 15)]      // valid override
    [Arguments("600", 600)]    // large but allowed
    public async Task ResolveCodexIdleTimeout_parses_env_with_default(string? env, int expectedMinutes) {
        var result = IdleEnd.ResolveCodexIdleTimeout(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_disconnected_time_covers_the_overage() {
        // 70 min of wall-clock since last activity, but 15 of those were a SignalR outage. Connected
        // idle = 55 min < 60 min window → must NOT idle-end (a mid-session outage is not idleness).
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(70), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(15));

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_true_when_connected_idle_exceeds_window_despite_prior_outage() {
        // 75 min wall-clock, 10 of them a brief outage → connected idle = 65 min > 60 min → idle-end.
        // (Models repeated reconnects with no new lines still ending after the connected budget.)
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(75), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(10));

        await Assert.That(should).IsTrue();
    }

    [Test]
    public async Task ShouldEndOnIdle_default_disconnected_is_zero_preserving_prior_behavior() {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow);

        await Assert.That(should).IsTrue();
    }

    [Test]
    public async Task ShouldEndOnIdle_true_for_idle_codex_session_watcher() {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsTrue();
    }

    [Test]
    [Arguments("claude")]
    [Arguments("gemini")]
    [Arguments("pi")]
    [Arguments("copilot")]
    [Arguments("kiro")]
    public async Task ShouldEndOnIdle_false_for_non_codex(string vendor) {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: vendor, isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    /// <summary>A session with no turn counts too: its start hook already registered it, and nothing else
    /// would stop its watcher. A session whose host was found keeps a parent-exit signal, so idling there ends nothing.</summary>
    [Test]
    [Arguments(true, true, true, 61, true)]
    [Arguments(true, true, false, 61, true)]
    [Arguments(true, true, true, 59, false)]
    [Arguments(true, false, true, 61, false)]
    [Arguments(false, true, true, 61, false)]
    public async Task ShouldEndOnIdle_for_copilot_only_for_a_session_watcher_with_no_end_signal(
            bool noEndSignal, bool isSessionWatcher, bool thresholdReached, int quietMinutes, bool expected) {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "copilot", isSessionWatcher: isSessionWatcher, thresholdReached: thresholdReached,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(quietMinutes), now: IdleNow, idleTimeout: IdleWindow,
            noEndSignal: noEndSignal);

        await Assert.That(should).IsEqualTo(expected);
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_not_yet_idle() {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(59), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_below_threshold() {
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: false,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_exactly_at_timeout_boundary() {
        // Strictly greater-than: exactly == idleTimeout is NOT yet idle.
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(60), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_tool_in_flight_even_if_idle() {
        // A tool call is in progress — must NOT idle-end even after the timeout window.
        var should = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: true);

        await Assert.That(should).IsFalse();
    }

    // No Cursor shell hook fires per-conversation the way a parent-exit watchdog needs, so an idle transcript (no file
    // growth AND heartbeat gone stale) is the fallback signal a Cursor session has ended. Unlike
    // Codex/Antigravity, the watcher itself must NOT synthesize session-end on this path — end
    // synthesis has exactly one owner (the hook or the server-side sweep) — so the idle-ceiling
    // exit is wired to skip PostSessionEndOnParentExitAsync at the RunWatch call site.
    [Test]
    public async Task Cursor_idle_ceiling_ends_on_idle_without_posting_session_end() {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // a Cursor CHILD (subagent) watcher never buffers, so
    // WatchState.ThresholdReached never flips true for it; excluding non-session-watchers from
    // the idle ceiling would make every Cursor child watcher permanently ineligible to idle-exit.
    [Test]
    public async Task Cursor_child_watcher_is_idle_ceiling_eligible_without_threshold_reached() {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        // Not yet idle — still false.
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // Regression guard: Antigravity spawns no child watchers at all, so a non-session watcher
    // carrying that vendor is a misconfiguration — it must stay ineligible rather than reap
    // anything.
    [Test]
    public async Task Antigravity_child_watcher_stays_ineligible_for_the_idle_ceiling() {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "antigravity", isSessionWatcher: false, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // A Claude CHILD (subagent) watcher's only exit paths were the SubagentStop-driven
    // StopWatcher signal and the parent-exit watchdog, so a missed SubagentStop leaked the watcher
    // for the whole life of the parent session (observed: 8 watchers surviving 1-12 days against a
    // live parent). Child watchers join the ceiling; like Cursor's, they never buffer, so the
    // threshold gate cannot apply.
    [Test]
    public async Task Claude_child_watcher_is_idle_ceiling_eligible_without_threshold_reached() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // The whole reason the ceiling is safe: a subagent running a long tool call writes nothing to
    // its transcript between the tool_use and its tool_result, so transcript silence alone must
    // never end it.
    [Test]
    public async Task Claude_child_watcher_never_idle_exits_while_a_tool_is_in_flight() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60),
            toolInFlight: true)).IsFalse();
    }

    // Regression guard: the ceiling is scoped to CHILD watchers. A Claude SESSION watcher has a
    // working parent-exit watchdog and a sessionEnd hook, so it must stay ineligible — otherwise a
    // user idling in a live Claude session would have their session ended out from under them.
    [Test]
    public async Task Claude_session_watcher_stays_ineligible_for_the_idle_ceiling() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // The tool tracker is what stops the ceiling reaping a LIVE subagent that is quiet because a
    // build/test run is in flight. If this gate ever stops matching the watchers the ceiling
    // applies to, PendingClaudeToolCalls stays empty, toolInFlight is permanently false, and the
    // suppression silently disappears — so the gate is a predicate rather than an inline
    // condition buried in the drain loop.
    [Test]
    [Arguments("claude", false, true)]   // child watcher: the only one with a ceiling, so the only one that needs the guard
    [Arguments("claude", true,  false)]  // session watcher: no ceiling, so parsing every line would be pure waste
    [Arguments("codex",  false, false)]  // other vendors have their own trackers
    [Arguments("cursor", false, false)]
    public async Task TracksClaudeToolCalls_matches_the_watchers_the_ceiling_applies_to(
            string vendor, bool isSessionWatcher, bool expected) {
        await Assert.That(ToolCallTracking.TracksClaudeToolCalls(vendor, isSessionWatcher)).IsEqualTo(expected);
    }

    // Guard on the composed policy at its REAL default, not a test-supplied window: a Claude
    // subagent goes quiet for six hours before it is reaped, and never while a tool is in flight.
    [Test]
    public async Task Claude_subagent_ceiling_composes_to_six_hours_and_yields_to_tools_in_flight() {
        var now    = DateTimeOffset.UtcNow;
        var window = IdleEnd.ResolveIdleWindow("claude", isSessionWatcher: false, _ => null);

        bool ExitsAfter(TimeSpan quiet, bool toolInFlight = false) => IdleEnd.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now - quiet, now: now, idleTimeout: window, toolInFlight: toolInFlight);

        await Assert.That(ExitsAfter(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1))).IsTrue();
        await Assert.That(ExitsAfter(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59))).IsFalse();

        // A 12-hour build still holds the watcher open — transcript silence alone never reaps.
        await Assert.That(ExitsAfter(TimeSpan.FromHours(12), toolInFlight: true)).IsFalse();
    }

    // The ceiling only reaps anything if a Claude CHILD watcher gets the subagent window rather
    // than the 60-minute Codex default the `_` branch gives.
    [Test]
    public async Task ResolveIdleWindow_gives_claude_child_watchers_the_subagent_ceiling() {
        var window = IdleEnd.ResolveIdleWindow("claude", isSessionWatcher: false, _ => null);

        await Assert.That(window).IsEqualTo(IdleEnd.DefaultClaudeSubagentIdleCeiling);
    }

    [Test]
    public async Task ResolveIdleWindow_honours_the_claude_subagent_env_override() {
        var window = IdleEnd.ResolveIdleWindow(
            "claude", isSessionWatcher: false,
            name => name == "KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES" ? "90" : null);

        await Assert.That(window).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    // Each vendor keeps reading its OWN knob — a Claude subagent must not be retunable via the
    // Codex knob, and vice versa.
    [Test]
    [Arguments("codex",       true,  "KCAP_CODEX_IDLE_MINUTES",           "15", 15)]
    [Arguments("copilot",     true,  "KCAP_COPILOT_IDLE_MINUTES",         "15", 15)]
    [Arguments("codex",       false, "KCAP_CODEX_SUBAGENT_REAP_MINUTES",  "45", 45)]
    [Arguments("antigravity", true,  "KCAP_ANTIGRAVITY_IDLE_MINUTES",     "20", 20)]
    [Arguments("cursor",      true,  "KCAP_CURSOR_IDLE_CEILING_MINUTES",  "25", 25)]
    [Arguments("claude",      false, "KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES", "30", 30)]
    public async Task ResolveIdleWindow_reads_the_vendors_own_knob(
            string vendor, bool isSessionWatcher, string knob, string value, int expectedMinutes) {
        var window = IdleEnd.ResolveIdleWindow(
            vendor, isSessionWatcher, name => name == knob ? value : null);

        await Assert.That(window).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Test]
    [Arguments(null, 360)]    // unset → default 6h
    [Arguments("", 360)]      // empty → default
    [Arguments("abc", 360)]   // non-numeric → default
    [Arguments("0", 360)]     // non-positive → default
    [Arguments("-5", 360)]    // negative → default
    [Arguments("90", 90)]     // valid override
    public async Task ResolveClaudeSubagentIdleCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = IdleEnd.ResolveClaudeSubagentIdleCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    // A Codex collab CHILD watcher has no other exit path: the parent's session-end teardown is
    // server-side records only, it is spawned without --parent-pid (the parent session watcher's
    // ancestry has no codex process to resolve), and it is never sent StopWatcher. Children join
    // the ceiling as a leak backstop; like Claude's, they never buffer, so the threshold gate
    // cannot apply.
    [Test]
    public async Task Codex_child_watcher_is_idle_ceiling_eligible_without_threshold_reached() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now - (TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1)), now: now,
            idleTimeout: TimeSpan.FromHours(6))).IsTrue();

        // Not yet idle — still false.
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromHours(6))).IsFalse();
    }

    // Same safety property as the Claude child ceiling: a subagent running a long tool call
    // writes nothing to its rollout between the call and its output record, so rollout silence
    // alone must never reap it.
    [Test]
    public async Task Codex_child_watcher_never_idle_exits_while_a_tool_is_in_flight() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddHours(-12), now: now, idleTimeout: TimeSpan.FromHours(6),
            toolInFlight: true)).IsFalse();
    }

    // The ceiling only reaps anything if RunWatch hands a Codex CHILD watcher the reap ceiling
    // rather than the 60-minute session window — reaping a quiet-but-live subagent at 60 minutes
    // would drop the rest of its transcript.
    [Test]
    public async Task ResolveIdleWindow_gives_codex_child_watchers_the_reap_ceiling() {
        var window = IdleEnd.ResolveIdleWindow("codex", isSessionWatcher: false, _ => null);

        await Assert.That(window).IsEqualTo(IdleEnd.DefaultCodexSubagentReapCeiling);
    }

    // KCAP_CODEX_SUBAGENT_IDLE_MINUTES is already taken by the subagent-stop grace, so the reap
    // ceiling gets its own knob.
    [Test]
    [Arguments(null, 360)]    // unset → default 6h
    [Arguments("abc", 360)]   // non-numeric → default
    [Arguments("0", 360)]     // non-positive → default
    [Arguments("45", 45)]     // valid override
    public async Task ResolveCodexSubagentReapCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = IdleEnd.ResolveCodexSubagentReapCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    // ResolveCursorIdleClock: the idle clock is the LATER of transcript
    // activity and the hook heartbeat, for Cursor only.
    [Test]
    public async Task ResolveCursorIdleClock_prefers_the_later_hook_heartbeat_for_cursor() {
        var now             = DateTimeOffset.UtcNow;
        var staleActivity   = now.AddMinutes(-61);
        var freshHeartbeat  = now.AddMinutes(-2); // Cursor still firing hooks recently

        var resolved = IdleEnd.ResolveCursorIdleClock("cursor", staleActivity, freshHeartbeat);

        await Assert.That(resolved).IsEqualTo(freshHeartbeat);
    }

    [Test]
    public async Task ResolveCursorIdleClock_keeps_transcript_activity_when_the_heartbeat_is_older() {
        var now           = DateTimeOffset.UtcNow;
        var freshActivity = now.AddMinutes(-2);
        var staleHeartbeat = now.AddMinutes(-61);

        var resolved = IdleEnd.ResolveCursorIdleClock("cursor", freshActivity, staleHeartbeat);

        await Assert.That(resolved).IsEqualTo(freshActivity);
    }

    [Test]
    public async Task ResolveCursorIdleClock_keeps_transcript_activity_when_no_heartbeat_recorded() {
        var now      = DateTimeOffset.UtcNow;
        var activity = now.AddMinutes(-61);

        var resolved = IdleEnd.ResolveCursorIdleClock("cursor", activity, hookHeartbeatAt: null);

        await Assert.That(resolved).IsEqualTo(activity);
    }

    // Regression guard: for every non-Cursor vendor the heartbeat argument is ignored entirely —
    // the caller never even reads the file for them, but pin the degrade-to-activity behavior
    // defensively in case a heartbeat value is ever passed anyway.
    [Test]
    public async Task ResolveCursorIdleClock_ignores_the_heartbeat_for_non_cursor_vendors() {
        var now      = DateTimeOffset.UtcNow;
        var activity = now.AddMinutes(-61);
        var heartbeat = now; // would win for cursor, must NOT win here

        var resolved = IdleEnd.ResolveCursorIdleClock("codex", activity, heartbeat);

        await Assert.That(resolved).IsEqualTo(activity);
    }

    [Test]
    [Arguments(null, 60)]      // unset → default 60 min
    [Arguments("", 60)]        // empty → default
    [Arguments("abc", 60)]     // non-numeric → default
    [Arguments("0", 60)]       // non-positive → default (clamped)
    [Arguments("-5", 60)]      // negative → default
    [Arguments("30", 30)]      // valid override
    public async Task ResolveCursorIdleCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = IdleEnd.ResolveCursorIdleCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }
}
