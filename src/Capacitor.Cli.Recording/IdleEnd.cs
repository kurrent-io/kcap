namespace Capacitor.Cli.Recording;

/// <summary>When a recorder ends its source because the transcript went quiet.</summary>
internal static class IdleEnd {

    /// <summary>
    /// Codex has no session-end hook and the desktop app's single long-lived
    /// `codex app-server` process is the watchdog's parent PID for EVERY
    /// conversation, so the parent-exit watchdog never fires per-conversation —
    /// sessions stay "active" until the whole app quits. This idle window is the
    /// fallback: if the rollout file gets no new lines for this long, the watcher
    /// POSTs session-end (reason "idle_timeout"). Self-correcting — Codex fires a
    /// Stop hook per turn that re-spawns the watcher and the read model
    /// reactivates the session, so a resumed conversation comes back to life.
    /// </summary>
    internal static readonly TimeSpan DefaultCodexIdleTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Resolves the Codex idle timeout from KCAP_CODEX_IDLE_MINUTES, falling back
    /// to <see cref="DefaultCodexIdleTimeout"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveCodexIdleTimeout(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCodexIdleTimeout;

    /// <summary>
    /// Cursor has no shell hook that reliably signals a conversation's end, so an idle transcript is its
    /// fallback too, on its own knob (default 60 minutes).
    /// </summary>
    internal static readonly TimeSpan DefaultCursorIdleCeiling = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Resolves the Cursor idle ceiling from KCAP_CURSOR_IDLE_CEILING_MINUTES, falling back to
    /// <see cref="DefaultCursorIdleCeiling"/> for unset/blank/non-numeric/non-positive values.
    /// </summary>
    internal static TimeSpan ResolveCursorIdleCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCursorIdleCeiling;

    /// <summary>
    /// Reap ceiling for a Claude CHILD (subagent) watcher, whose only other exits are the
    /// SubagentStop-driven StopWatcher signal and the parent-exit watchdog — so a missed
    /// SubagentStop leaks it for the life of the parent session.
    ///
    /// Far longer than the 60-minute vendor ceilings because it is a leak backstop, not an
    /// end-of-conversation detector: a child watcher posts no session-end, so reaping late costs
    /// nothing while reaping a LIVE subagent drops the rest of its transcript.
    /// </summary>
    internal static readonly TimeSpan DefaultClaudeSubagentIdleCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the Claude subagent idle ceiling from KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES, falling
    /// back to <see cref="DefaultClaudeSubagentIdleCeiling"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveClaudeSubagentIdleCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultClaudeSubagentIdleCeiling;

    /// <summary>
    /// Reap ceiling for a Codex collab child, the backstop for a child orphaned by a hard-killed parent: it
    /// has no parent-pid watchdog, and StopWatcher only reaches the session's recorder. Sized like
    /// <see cref="DefaultClaudeSubagentIdleCeiling"/>: reaping late costs nothing, and reaping a live subagent drops
    /// the rest of its transcript.
    /// </summary>
    internal static readonly TimeSpan DefaultCodexSubagentReapCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the Codex subagent reap ceiling from KCAP_CODEX_SUBAGENT_REAP_MINUTES — its own
    /// knob because KCAP_CODEX_SUBAGENT_IDLE_MINUTES already tunes the subagent-stop grace.
    /// Falls back to <see cref="DefaultCodexSubagentReapCeiling"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveCodexSubagentReapCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCodexSubagentReapCeiling;

    /// <summary>
    /// The idle window a recorder measures against, per vendor and role, each vendor on its own knob.
    /// The role argument exists for Claude, whose child watchers get the subagent backstop rather than a
    /// conversation-idle window.
    /// </summary>
    internal static TimeSpan ResolveIdleWindow(string vendor, bool isSessionWatcher, Func<string, string?> env) =>
        vendor switch {
            "antigravity"                     => ResolveCodexIdleTimeout(env("KCAP_ANTIGRAVITY_IDLE_MINUTES")),
            "cursor"                          => ResolveCursorIdleCeiling(env("KCAP_CURSOR_IDLE_CEILING_MINUTES")),
            "copilot"                         => ResolveCodexIdleTimeout(env("KCAP_COPILOT_IDLE_MINUTES")),
            "claude" when !isSessionWatcher   => ResolveClaudeSubagentIdleCeiling(env("KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES")),
            "codex" when !isSessionWatcher    => ResolveCodexSubagentReapCeiling(env("KCAP_CODEX_SUBAGENT_REAP_MINUTES")),
            _                                 => ResolveCodexIdleTimeout(env("KCAP_CODEX_IDLE_MINUTES")),
        };

    /// <summary>
    /// Whether a recorder should end its source because the transcript went idle, gated to the vendors and
    /// roles whose parent-exit watchdog can't fire per conversation: see the switch below. Session watchers
    /// also need the threshold reached; child watchers never buffer, so they are eligible without it. The
    /// boundary tick does not yet count as idle. A tool in flight suppresses the end, since a long command
    /// writes no lines between its start and its result.
    /// <para>Cursor's caller must not follow this with a session-end post: its end has one owner, the
    /// sessionEnd hook or the server's lease-gated sweep.</para>
    /// </summary>
    internal static bool ShouldEndOnIdle(
            string         vendor,
            bool           isSessionWatcher,
            bool           thresholdReached,
            DateTimeOffset lastActivityAt,
            DateTimeOffset now,
            TimeSpan       idleTimeout,
            bool           toolInFlight              = false,
            TimeSpan       disconnectedSinceActivity = default,
            bool           noEndSignal               = false
        ) {
        // Which vendors may idle-exit, and in which role:
        //   codex             — both. Session: the idle window is the fallback session-end signal
        //                       (no per-conversation parent-exit hook). CHILD: the reap ceiling
        //                       is a leak backstop — no parent-pid watchdog, and the
        //                       server's StopWatcher only reaches the session watcher, so a child
        //                       that outlives the parent's teardown would otherwise never exit.
        //                       Server-side finalization stays with the child's own live
        //                       subagent-stop POST or the parent-end teardown, never this exit.
        //   antigravity       — session only; it spawns no child watchers at all.
        //   cursor            — both; no shell hook fires a reliable parent-exit signal either.
        //   claude            — CHILD only: nothing else finalizes it (a missed SubagentStop
        //                       leaks it for the life of the parent). Its SESSION watcher has a
        //                       working watchdog AND a sessionEnd hook, so a ceiling there could
        //                       end a live session out from under a thinking user.
        //   copilot           — a SESSION with no end signal only: VS Code Local, which has neither a
        //                       session-end hook nor a parent, or one whose host was not found. A session
        //                       with both has the same ceiling-end risk as Claude's session watcher.
        var eligible = vendor switch {
            "codex" or "cursor" => true,
            "antigravity"       => isSessionWatcher,
            "claude"            => !isSessionWatcher,
            "copilot"           => isSessionWatcher && noEndSignal,
            _                   => false,
        };

        if (!eligible) return false;

        // A child watcher never buffers, so requiring ThresholdReached would make it permanently
        // ineligible. A session watcher keeps the gate: a session nobody has taken a turn in has not
        // gone idle, and its end comes from the parent's exit instead. A Local session has no parent
        // to end it, so idling is its end with or without a turn.
        if (isSessionWatcher && !thresholdReached && !noEndSignal) return false;

        return now - lastActivityAt - disconnectedSinceActivity > idleTimeout && !toolInFlight;
    }

    /// <summary>
    /// Cursor's idle clock is the later of transcript activity and the hook heartbeat, since every Cursor
    /// hook touches the heartbeat whether or not the transcript moved, and a session still firing hooks must
    /// not idle-exit. For every other vendor the caller passes no heartbeat.
    /// </summary>
    internal static DateTimeOffset ResolveCursorIdleClock(
            string          vendor,
            DateTimeOffset  lastActivityAt,
            DateTimeOffset? hookHeartbeatAt
        ) =>
        vendor == "cursor" && hookHeartbeatAt is { } hb && hb > lastActivityAt
            ? hb
            : lastActivityAt;
}
