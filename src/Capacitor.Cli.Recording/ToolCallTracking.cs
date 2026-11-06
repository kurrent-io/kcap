using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording;

/// <summary>Which tool calls are awaiting their result, per vendor. A tool running writes nothing to the
/// transcript, so idle-end must not read its silence as an idle agent.</summary>
internal static class ToolCallTracking {

    /// <summary>
    /// Which watchers feed <see cref="UpdateCodexPendingToolCalls"/>. It reads raw drain lines, which are
    /// unbounded, so running it for every vendor would make an unrelated recorder parse a multi-megabyte line
    /// it can never match. Not gated on role: a collab child needs it to know when to post its stop.
    /// </summary>

    internal static bool TracksCodexToolCalls(string vendor) => vendor == "codex";

    /// <summary>Only a VS Code Local session idle-ends, and raw drain lines are unbounded, so nothing else parses them here.</summary>
    internal static bool TracksCopilotToolCalls(string vendor, string transcriptPath, bool hostMissing) =>
        vendor == "copilot" && (hostMissing || CopilotPaths.IsVsCodeLocalTranscript(transcriptPath));

    /// <summary>A Local session has no parent process to end it, so a tool that never reports back, such as
    /// one running when VS Code quits, would keep it open and its watcher running for good without a ceiling.</summary>
    internal static readonly TimeSpan CopilotToolCeiling = TimeSpan.FromHours(4);

    internal static bool CopilotToolInFlight(WatchState state, DateTimeOffset now) =>
        state.PendingCopilotToolCalls.Count > 0 && now - state.LastActivityAt < CopilotToolCeiling;

    /// <summary>
    /// Tracks a Copilot tool from <c>tool.execution_start</c> to <c>tool.execution_complete</c>. Malformed
    /// lines are ignored, so this is safe on any transcript line.
    /// </summary>

    internal static void UpdateCopilotPendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "tool.execution_start" when root.Obj("data")?.Str("toolCallId") is { } started:
                    pending.Add(started);
                    break;

                case "tool.execution_complete" when root.Obj("data")?.Str("toolCallId") is { } completed:
                    pending.Remove(completed);
                    break;
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    /// <summary>
    /// Adds a <c>function_call</c> or <c>custom_tool_call</c> response item's <c>call_id</c>, and removes it on the
    /// matching <c>_output</c> variant. Malformed lines are ignored, so this is safe on any transcript line.
    /// </summary>

    internal static void UpdateCodexPendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return;

            var payload = root.Obj("payload");

            if (payload is not { } p) return;

            var callId = p.Str("call_id");

            if (callId is null) return;

            switch (p.Str("type")) {
                case "function_call"
                  or "custom_tool_call":
                    pending.Add(callId);
                    break;

                case "function_call_output"
                  or "custom_tool_call_output":
                    pending.Remove(callId);
                    break;
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    /// <summary>
    /// Which watchers feed <see cref="UpdateClaudePendingToolCalls"/>. Must stay in step with the watchers
    /// <see cref="IdleEnd.ShouldEndOnIdle"/> gives a ceiling to: one reaped on idle that never tracks its tool
    /// calls has no in-flight suppression and would end a live subagent mid-build.
    /// </summary>

    internal static bool TracksClaudeToolCalls(string vendor, bool isSessionWatcher) =>
        vendor == "claude" && !isSessionWatcher;

    /// <summary>
    /// Rebuilds the in-flight set from the lines before a resume cursor. A recorder that resumes mid-tool
    /// never sees the <c>tool_use</c> that opened before its cursor, so the set would come up empty and the
    /// ceiling would treat a live subagent as idle.
    /// </summary>

    internal static TranscriptReplayConsumer ClaudePendingToolCallsReplay(HashSet<string> pending) =>
        new(WindowOnly: true, line => {
            UpdateClaudePendingToolCalls(pending, line);
            return false;
        });

    /// <summary>
    /// Guards the subagent idle ceiling: a <c>tool_use</c> block adds its <c>id</c>, and the matching
    /// <c>tool_result</c> removes it through <c>tool_use_id</c>. Reads the content blocks rather than the record's
    /// <c>type</c>, since the blocks are what carry the ids.
    /// </summary>

    internal static void UpdateClaudePendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Obj("message")?.Arr("content") is not { } content) return;

            foreach (var block in content.EnumerateArray()) {
                switch (block.Str("type")) {
                    case "tool_use" when block.Str("id") is { } id:
                        pending.Add(id);
                        break;

                    case "tool_result" when block.Str("tool_use_id") is { } resultId:
                        pending.Remove(resultId);
                        break;
                }
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    /// <summary>
    /// Codex's counterpart to <see cref="ClaudePendingToolCallsReplay"/>. Both roles need the pending set:
    /// a <c>function_call</c> that opened before the cursor writes nothing until its output lands, so a
    /// session watcher would read <c>toolInFlight</c> false and idle-end a live session. Turn state is
    /// child-only: nothing else re-delivers the acknowledged <c>task_complete</c> that arms the live stop.
    /// </summary>
    internal static TranscriptReplayConsumer CodexWatcherStateReplay(WatchState state, bool isChildWatcher) =>
        new(WindowOnly: true, line => {
            UpdateCodexPendingToolCalls(state.PendingCodexToolCalls, line);
            if (isChildWatcher) state.CodexSubagentTurn.Observe(line);
            return false;
        });
}
