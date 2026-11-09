using Capacitor.Cli.Recording.Harness.Antigravity;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Unit tests for the Antigravity watcher extractors: title-text extraction
/// from transcript_full.jsonl lines, the &lt;USER_REQUEST&gt; envelope strip, the
/// title-event gate (<see cref="TranscriptLineText.IsEvent"/>), and the idle-timeout policy
/// generalized to the antigravity GUI vendor (<see cref="IdleEnd.ShouldEndOnIdle"/>).
/// </summary>
public class AntigravityWatchExtractorTests {
    const string UserLine =
        """{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","content":"<USER_REQUEST>\nAdd a health endpoint\n</USER_REQUEST>\n<ADDITIONAL_METADATA>cwd=/repo</ADDITIONAL_METADATA>"}""";

    const string AssistantLine =
        """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","content":"Sure, adding it now.","thinking":"plan"}""";

    const string ToolLine =
        """{"step_index":2,"source":"MODEL","type":"RUN_COMMAND","status":"DONE","content":"ran ls"}""";

    [Test]
    public async Task User_text_strips_the_USER_REQUEST_envelope_and_metadata() {
        await Assert.That(TranscriptLineText.TryExtractUserText(UserLine, "antigravity"))
            .IsEqualTo("Add a health endpoint");
    }

    [Test]
    public async Task User_text_falls_back_to_raw_content_without_envelope() {
        const string bare =
            """{"type":"USER_INPUT","content":"just do it"}""";
        await Assert.That(TranscriptLineText.TryExtractUserText(bare, "antigravity"))
            .IsEqualTo("just do it");
    }

    [Test]
    public async Task Assistant_text_is_the_planner_response_content() {
        await Assert.That(TranscriptLineText.TryExtractAssistantText(AssistantLine, "antigravity"))
            .IsEqualTo("Sure, adding it now.");
    }

    [Test]
    public async Task Extractors_ignore_the_wrong_role_and_plumbing_lines() {
        // A tool step is neither a user nor an assistant title event.
        await Assert.That(TranscriptLineText.TryExtractUserText(ToolLine, "antigravity")).IsNull();
        await Assert.That(TranscriptLineText.TryExtractAssistantText(ToolLine, "antigravity")).IsNull();
        // A PLANNER_RESPONSE is not user text; a USER_INPUT is not assistant text.
        await Assert.That(TranscriptLineText.TryExtractUserText(AssistantLine, "antigravity")).IsNull();
        await Assert.That(TranscriptLineText.TryExtractAssistantText(UserLine, "antigravity")).IsNull();
    }

    [Test]
    public async Task IsEvent_counts_only_conversational_steps_with_text() {
        await Assert.That(TranscriptLineText.IsEvent(UserLine, "antigravity")).IsTrue();
        await Assert.That(TranscriptLineText.IsEvent(AssistantLine, "antigravity")).IsTrue();
        await Assert.That(TranscriptLineText.IsEvent(ToolLine, "antigravity")).IsFalse();

        // Empty prompt yields no titleable text → not an event.
        const string emptyUser = """{"type":"USER_INPUT","content":"<USER_REQUEST></USER_REQUEST>"}""";
        await Assert.That(TranscriptLineText.IsEvent(emptyUser, "antigravity")).IsFalse();
    }

    [Test]
    public async Task PendingToolCalls_tracks_calls_versus_results_and_suppresses_idle() {
        var state = new Capacitor.Cli.Core.WatchState();

        // A PLANNER_RESPONSE with two tool_calls → two in flight.
        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state,
            """{"type":"PLANNER_RESPONSE","tool_calls":[{"name":"run_command"},{"name":"view_file"}]}""");
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(2);

        // Each result step clears one.
        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state, """{"type":"RUN_COMMAND","content":"ran"}""");
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(1);
        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state, """{"type":"VIEW_FILE","content":"…"}""");
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(0);
        // Never goes negative.
        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state, """{"type":"RUN_COMMAND","content":"x"}""");
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(0);
    }

    [Test]
    public async Task PendingToolCalls_excludes_async_subagent_orchestration_so_a_parent_can_idle_end() {
        // define_subagent/invoke_subagent resolve via a SEPARATE conversation (the child
        // reports back through brain/<parent>/messages), never as a result step in this transcript.
        // Counting them would pin the count > 0 forever and suppress idle-end, so a subagent-invoking
        // parent would only end when Antigravity quits. They must be excluded from the in-flight count.
        var state = new Capacitor.Cli.Core.WatchState();

        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state,
            """{"type":"PLANNER_RESPONSE","tool_calls":[{"name":"define_subagent"},{"name":"invoke_subagent"},{"name":"invoke_subagent"},{"name":"list_dir"}]}""");
        // Only the real command (list_dir) counts as in flight — the 3 subagent calls are excluded.
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(1);

        // Its result step clears it → count 0 → idle-end is not suppressed.
        AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state, """{"type":"LIST_DIRECTORY","content":"…"}""");
        await Assert.That(state.PendingAntigravityToolCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ShouldEndOnIdle_suppressed_while_a_tool_is_in_flight() {
        var now  = DateTimeOffset.UnixEpoch.AddHours(5);
        var idle = TimeSpan.FromMinutes(60);
        // Past the idle window, but a tool is in flight → do NOT end (long command running).
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            "antigravity", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now - TimeSpan.FromMinutes(61), now: now, idleTimeout: idle,
            toolInFlight: true)).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_fires_for_antigravity_like_codex() {
        var now  = DateTimeOffset.UnixEpoch.AddHours(5);
        var idle = TimeSpan.FromMinutes(60);

        // Past the idle window on a threshold-reached session watcher → end.
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            "antigravity", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now - TimeSpan.FromMinutes(61), now: now, idleTimeout: idle)).IsTrue();

        // Within the window → keep running.
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            "antigravity", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now - TimeSpan.FromMinutes(59), now: now, idleTimeout: idle)).IsFalse();

        // Subagent watchers never self-end on idle.
        await Assert.That(IdleEnd.ShouldEndOnIdle(
            "antigravity", isSessionWatcher: false, thresholdReached: true,
            lastActivityAt: now - TimeSpan.FromMinutes(61), now: now, idleTimeout: idle)).IsFalse();
    }
}
