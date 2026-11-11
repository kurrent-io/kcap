using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Recording;
using Capacitor.Cli.Recording.Harness.Cursor;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Exercise live-drain versus final-drain handling of an incomplete final line, watcher idle exit
/// without duplicate session-end synthesis, and reactivation through both start and non-start hooks.
/// Drive CursorHookCommand.HandleCore for reactivation; a predicate-only test cannot establish spawning.
/// This suite has no in-process SignalR host: the acknowledgement wire and server lease sweep have
/// separate server-side coverage. TranscriptReader and CursorRewriteGuard supply the local read seams.
/// </summary>
[NotInParallel] // KCAP_WATCHER_DIR is process-global, and other classes pin it too.
public class CursorTailingWatcherTests {
    TestRecorders Recorders => field ??= new(Config.Root);

    CursorMarkers Markers => new(Config.Root, TimeProvider.System);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    static readonly TempDir Tmp = new();

    static string? _previousWatcherDir;

    [Before(Class)]
    public static void SetUp() {
        _previousWatcherDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", Tmp.Path);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", _previousWatcherDir);
        Tmp.Dispose();
    }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ── 1. Force-quit mid-turn → tail drained ─────────────────────────────────────────────────

    /// <summary>
    /// Simulates a Cursor agent force-quit mid-write of its final transcript line: the process
    /// dies before flushing the trailing newline, but the bytes already on disk parse as a
    /// complete JSON record. Every LIVE drain (the watcher's normal per-poll read) must hold that
    /// line back — consuming a truncated write would risk dropping a still-growing line — but the
    /// SHUTDOWN final drain (what <c>RunWatch</c> runs right before exit, whether the exit is
    /// StopWatcher, idle-ceiling, or parent-exit) must consume it: a force-quit means nothing will
    /// ever write the newline, so holding it forever would silently lose the last turn.
    /// </summary>
    [Test]
    public async Task ForceQuitMidTurn_HeldByLiveDrain_ThenConsumedByFinalDrain() {
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("session.jsonl");
        // Two flushed lines, then a force-quit mid-write of the third: complete JSON, no
        // trailing newline (Cursor writes the record body first, the newline last).
        await File.WriteAllTextAsync(
            transcriptPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"first"}]}}""" + "\n" +
            """{"role":"assistant","message":{"content":[{"type":"text","text":"second"}]}}""" + "\n" +
            """{"role":"user","message":{"content":[{"type":"text","text":"force-quit line"}]}}"""
        );

        await using (var liveStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            var liveDrain = await TranscriptReader.ReadNewCompleteLinesAsync(
                liveStream, linesProcessed: 0, IncompleteFinalLinePolicy.Hold, CancellationToken.None);

            // Only the two flushed lines — the unterminated third is held, and NextPosition
            // stays before it so a later drain re-reads it once complete. HeldIncompleteFinalLine
            // is true here too (there IS a real held partial) — it just isn't acted on by a live
            // drain; only the final drain below treats it as a needs-import signal.
            await Assert.That(liveDrain.Lines.Count).IsEqualTo(2);
            await Assert.That(liveDrain.NextPosition).IsEqualTo(2);
            await Assert.That(liveDrain.HeldIncompleteFinalLine).IsTrue();
        }

        // The agent process is gone — nothing will ever append the trailing newline. The
        // shutdown final drain must still recover the line rather than losing the turn.
        await using (var finalStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            var finalDrain = await TranscriptReader.ReadNewCompleteLinesAsync(
                finalStream, linesProcessed: 2, IncompleteFinalLinePolicy.ConsumeIfComplete, CancellationToken.None);

            await Assert.That(finalDrain.Lines.Count).IsEqualTo(1);
            await Assert.That(finalDrain.Lines[0]).Contains("force-quit line");
            await Assert.That(finalDrain.NextPosition).IsEqualTo(3);
            await Assert.That(finalDrain.HeldIncompleteFinalLine).IsFalse(); // consumed, not held
        }
    }

    // ── 2. Idle-ceiling exit without Cursor synthesizing its own session-end ─────────────────

    /// <summary>
    /// Combines the two decisions <c>RunWatch</c> makes back to back at its idle-ceiling exit:
    /// <see cref="IdleEnd.ShouldEndOnIdle"/> (should the watcher exit?) and
    /// <see cref="WatchCommand.CursorSuppressesEndPost"/> (should it also POST session-end on the
    /// way out?). For Cursor these must diverge from every other idle-ceiling vendor: the watcher
    /// exits, but it must NOT be the one to synthesize session-end (that stays owned by the
    /// sessionEnd hook / the server-side lease sweep — see the class doc for where "sweep closes"
    /// is actually proven). Codex/Antigravity, by contrast, DO post it themselves.
    /// </summary>
    [Test]
    public async Task IdleCeiling_EndsTheCursorSession_ButSuppressesSessionEndSynthesis() {
        var now   = DateTimeOffset.UtcNow;
        var idle  = now.AddMinutes(-61);
        var idleTimeout = TimeSpan.FromMinutes(60);

        var cursorShouldExit = IdleEnd.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: idleTimeout);
        await Assert.That(cursorShouldExit).IsTrue();

        var cursorSuppresses = WatchCommand.CursorSuppressesEndPost(vendor: "cursor", idleExit: cursorShouldExit);
        await Assert.That(cursorSuppresses).IsTrue();

        // Contrast: Codex hits the SAME idle-ceiling exit condition but must NOT suppress —
        // the suppression is Cursor-specific, not "every idle-ceiling vendor".
        var codexShouldExit = IdleEnd.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: idleTimeout);
        await Assert.That(codexShouldExit).IsTrue();
        await Assert.That(WatchCommand.CursorSuppressesEndPost(vendor: "codex", idleExit: codexShouldExit)).IsFalse();

        // And Cursor's OTHER exit paths (not idle-ceiling) are unaffected by the suppression —
        // e.g. an explicit StopWatcher-driven exit (idleExit=false) still posts normally.
        await Assert.That(WatchCommand.CursorSuppressesEndPost(vendor: "cursor", idleExit: false)).IsFalse();
    }

    // ── 3. Open-but-idle past ceiling → closed, next hook reactivates (both resume shapes) ──

    /// <summary>
    /// A prior watcher for this session is gone (no pid file — "closed", whether by the
    /// idle-ceiling exit above or the server-side sweep). The very next <c>sessionStart</c> hook
    /// — Cursor's own resume/reconnect event — must spawn a fresh watcher, driven through the
    /// REAL <see cref="CursorHookCommand.HandleCore"/> dispatcher (JSON parsing, event-map
    /// resolution, the sessionStart-specific spawn-before-POST ordering) rather than calling the
    /// spawn predicate directly.
    /// </summary>
    [Test]
    public async Task Reactivation_ViaSessionStartHook_SpawnsAFreshWatcher() {
        using var tmp = new TempDir();
        var sessionId      = NewSessionId();
        var transcriptPath = tmp.PathTo($"{sessionId}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, """{"role":"user","message":{"content":[]}}""" + "\n");

        var spawner = new FakeWatcherSpawner();

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.PathTo("spool"), time: TimeProvider.System);

        var body = $$"""{"hook_event_name":"sessionStart","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath.Replace(@"\", @"\\")}}"}""";
        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), spawner), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleCore(client, new StringReader(body), spool);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spawner.Keys).IsEquivalentTo([sessionId]);
    }

    /// <summary>
    /// The "every-hook spawn path" half of the same criterion: a resume that arrives via ANY
    /// non-terminal, non-<c>sessionStart</c> hook (here <c>postToolUse</c>, Cursor's own
    /// telemetry-only event) must ALSO spawn a fresh watcher once its own lifecycle POST
    /// succeeds — the recovery-spawn precedence <c>CursorHookCommand.ShouldSpawnWatcher</c>
    /// encodes, driven here through the real dispatcher rather than the pure predicate alone.
    /// </summary>
    [Test]
    public async Task Reactivation_ViaNonSessionStartHook_SpawnsAFreshWatcher() {
        using var tmp = new TempDir();
        var sessionId      = NewSessionId();
        var transcriptPath = tmp.PathTo($"{sessionId}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, """{"role":"user","message":{"content":[]}}""" + "\n");

        var spawner = new FakeWatcherSpawner();

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.PathTo("spool"), time: TimeProvider.System);

        var body = $$"""{"hook_event_name":"postToolUse","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath.Replace(@"\", @"\\")}}","tool_name":"Bash"}""";
        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), spawner), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleCore(client, new StringReader(body), spool);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spawner.Keys).IsEquivalentTo([sessionId]);
    }

    // ── 5. Resume at an unterminated final line, then its terminator arrives → no line drift (r6) ──

    /// <summary>
    /// end-to-end regression for the P1 finding: a watcher resumes at a
    /// server frontier that lands exactly on a COMPLETE-but-unterminated final record (the r5
    /// scenario — a prior process's shutdown final drain already sent and the server already
    /// acked that record), then Cursor appends the record's own trailing newline before writing
    /// its NEXT record (Cursor's normal write order: body, then '\n', then the next record).
    ///
    /// Before this fix, <c>SeedCursorByteOffsetAsync</c> seeded <c>CursorByteOffset</c> at EOF
    /// while leaving <c>LinesProcessed</c> at the final record's own (already-acked) line number.
    /// The next poll's bounded read then started exactly at that EOF, saw the freshly-appended
    /// leading <c>'\n'</c>, and — because <see cref="TranscriptReader.ReadNewCompleteLinesAsync"/>
    /// seeds its line index from <c>LinesProcessed</c> — misread it as closing a NEW, phantom
    /// empty line, then labelled the real next record one line too high, permanently: the server,
    /// still waiting at the true frontier, would see a persistent apparent gap while the watcher
    /// kept resending from the stale offset.
    ///
    /// This composes the same two pure seams the rest of this class uses instead of a live
    /// SignalR round trip: <see cref="TranscriptRecorder.SeedCursorByteOffsetAsync"/> (the resume) and
    /// <see cref="TranscriptReader.ReadNewCompleteLinesAsync"/> (the next poll's bounded read), fed
    /// from the SAME <c>WatchState</c> fields <c>RunWatch</c>/<c>DrainNewLines</c> thread between
    /// them in production.
    /// </summary>
    [Test]
    public async Task ResumeAtUnterminatedFinalLine_ThenTerminatorArrives_NextRecordKeepsItsTrueLineNumber() {
        using var tmp = new TempDir();
        var sessionId = NewSessionId();
        try {
            var transcriptPath = tmp.PathTo("session.jsonl");
            // Line 0 (terminated) + line 1, complete but not yet terminated — exactly what a
            // prior watcher's shutdown final drain sent and the server already acknowledged.
            await File.WriteAllTextAsync(transcriptPath, "{\"a\":1}\n{\"b\":2}");

            var guard = new CursorRewriteGuard(Config.Root, sessionId, TimeProvider.System);
            // The server resumes this fresh watcher process at line 2 (1-based: 2 lines already
            // acked — line 0 and the unterminated line 1).
            var state = new WatchState { LinesProcessed = 2 };

            var seeded = await Recorders.For(sessionId, transcriptPath, null, vendor: "cursor").SeedCursorByteOffsetAsync(
                state, lineNumber: 2, guard, CancellationToken.None);

            await Assert.That(seeded).IsTrue();
            await Assert.That(Markers.IsQuarantined(sessionId)).IsFalse();
            // Rewound to the unterminated record's own start, NOT EOF — and LinesProcessed
            // rewound with it, so the two frontiers stay in lockstep.
            await Assert.That(state.CursorByteOffset).IsEqualTo(8L);  // "{\"a\":1}\n".Length
            await Assert.That(state.LinesProcessed).IsEqualTo(1);

            // Cursor now appends the terminator for the already-acked line 1, followed by the
            // NEXT record — its normal write order (body, then '\n', then the next record's body).
            await File.AppendAllTextAsync(transcriptPath, "\n{\"c\":3}\n");

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var drainRead = await TranscriptReader.ReadNewCompleteLinesAsync(
                stream, state.LinesProcessed, IncompleteFinalLinePolicy.Hold, CancellationToken.None,
                captureRawBytes: true, rawBytesReadFrom: state.CursorByteOffset, newRangeByteOffset: state.CursorByteOffset);

            // The rewound line 1 ("{"b":2}") is re-read/re-sent — harmless, the server's
            // source-ack frontier dedupes a resend at/behind it. No phantom empty line appears
            // between it and the next record.
            await Assert.That(drainRead.Lines).IsEquivalentTo(["{\"b\":2}", "{\"c\":3}"]);
            await Assert.That(drainRead.LineNumbers).IsEquivalentTo([1, 2]);

            // The critical assertion: {"c":3} is NOT shifted to line 3 — it keeps its true,
            // natural 0-indexed position (2), immediately after line 1.
            var newRecordIndex = drainRead.Lines.IndexOf("{\"c\":3}");
            await Assert.That(drainRead.LineNumbers[newRecordIndex]).IsEqualTo(2);

            // Byte/line frontiers stay aligned — NextPosition (the next LinesProcessed value) is
            // exactly 3, matching the file's true 3 complete lines; no permanent gap opens up.
            await Assert.That(drainRead.NextPosition).IsEqualTo(3);
        } finally {
            try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { }
        }
    }
}
