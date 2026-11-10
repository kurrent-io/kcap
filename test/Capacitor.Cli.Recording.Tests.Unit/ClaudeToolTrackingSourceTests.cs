namespace Capacitor.Cli.Recording.Tests.Unit;

public class ClaudeToolTrackingSourceTests {
    const string ToolUse =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_big","name":"Bash","input":{"command":"build"}}]}}""";

    static string OversizedToolResult() =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_big\",\"type\":\"tool_result\",\"content\":\""
      + new string('x', SecretRedactor.MaxRecordBytes + 1024)
      + "\"}]}}";

    /// <summary>
    /// A dropped result must still clear local pending-tool state so the idle ceiling can fire.
    /// </summary>
    [Test]
    public async Task RedactedLines_StrandThePendingId_ButRawLinesClearIt() {
        var viaRedacted = new HashSet<string>(StringComparer.Ordinal);
        ToolCallTracking.UpdateClaudePendingToolCalls(viaRedacted, SecretRedactor.RedactLine(ToolUse));
        ToolCallTracking.UpdateClaudePendingToolCalls(viaRedacted, SecretRedactor.RedactLine(OversizedToolResult()));

        await Assert.That(viaRedacted.Contains("toolu_big")).IsTrue();

        var viaRaw = new HashSet<string>(StringComparer.Ordinal);
        ToolCallTracking.UpdateClaudePendingToolCalls(viaRaw, ToolUse);
        ToolCallTracking.UpdateClaudePendingToolCalls(viaRaw, OversizedToolResult());

        await Assert.That(viaRaw.Count).IsEqualTo(0);
    }

    // A watcher that reconnects mid-tool resumes at a server cursor and never re-reads the
    // tool_use that started before it, so without a backfill the pending set comes up empty and a
    // live subagent is eligible for reaping.
    [Test]
    public async Task Backfill_recovers_a_tool_use_left_open_before_the_resume_cursor() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("backfill.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"still working"}]}}"""]);

        var pending = new HashSet<string>(StringComparer.Ordinal);
        await TranscriptReplay.ReplayAsync(path, 2, [ToolCallTracking.ClaudePendingToolCallsReplay(pending)], CancellationToken.None, _ => { });

        await Assert.That(pending.Contains("toolu_big")).IsTrue();
    }

    [Test]
    public async Task Backfill_leaves_nothing_pending_when_the_tool_already_completed() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("backfill.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, OversizedToolResult()]);

        var pending = new HashSet<string>(StringComparer.Ordinal);
        await TranscriptReplay.ReplayAsync(path, 2, [ToolCallTracking.ClaudePendingToolCallsReplay(pending)], CancellationToken.None, _ => { });

        await Assert.That(pending.Count).IsEqualTo(0);
    }

    // Only the lines before the cursor are the watcher's blind spot; everything from the cursor on
    // arrives through the normal drain, and scanning it here would double-apply it.
    [Test]
    public async Task Backfill_stops_at_the_cursor() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("backfill.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, OversizedToolResult()]);

        var pending = new HashSet<string>(StringComparer.Ordinal);
        await TranscriptReplay.ReplayAsync(path, 1, [ToolCallTracking.ClaudePendingToolCallsReplay(pending)], CancellationToken.None, _ => { });

        await Assert.That(pending.Contains("toolu_big")).IsTrue();
    }

    /// <summary>
    /// The scan is bounded to a window ending at the cursor rather than parsing the whole history:
    /// an unfinished tool is by definition at the tail (nothing is written after its tool_use until
    /// the result arrives), so anything further back is settled and only costs startup latency. It
    /// also means a stale unmatched tool_use from far earlier — an interrupted turn — can't strand
    /// an id and suppress the ceiling forever.
    /// </summary>
    [Test]
    public async Task Backfill_ignores_an_unmatched_tool_use_older_than_the_scan_window() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("backfill.jsonl");

        var lines = new List<string> { ToolUse };  // never resolved, then buried by later activity
        lines.AddRange(Enumerable.Repeat(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"later"}]}}""",
            TranscriptReplay.WindowLines + 10));

        await File.WriteAllLinesAsync(path, lines);

        var pending = new HashSet<string>(StringComparer.Ordinal);
        await TranscriptReplay.ReplayAsync(path, lines.Count, [ToolCallTracking.ClaudePendingToolCallsReplay(pending)], CancellationToken.None, _ => { });

        await Assert.That(pending.Count).IsEqualTo(0);
    }


}
